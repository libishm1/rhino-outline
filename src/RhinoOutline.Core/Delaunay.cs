// RhinoOutline - image to outline curves for Rhino.
// Copyright (C) 2026 RhinoOutline contributors. Licensed under the GNU GPL v2, see LICENSE.
//
// Delaunay triangulation with the sweep-hull method popularised by Delaunator (V. Agafonkin).
// Written for this project; it replaces the Grasshopper Delaunay solver the old alphaShape
// component depended on, so the plug-in needs nothing but RhinoCommon.
//
// Triangles are counter-clockwise for a y-up frame (positive cross product). Orientation tests
// are exact as long as coordinates are multiples of a power of two below about 2^24, which is
// why AlphaShape snaps sample points to a 1/256 pixel grid.

using System;

namespace RhinoOutline.Core
{
  public sealed class Delaunay
  {
    const double Epsilon = 2.220446049250313e-16;

    /// <summary>Input coordinates, x and y interleaved.</summary>
    public readonly double[] Coords;

    /// <summary>Three point indices per triangle.</summary>
    public int[] Triangles = new int[0];

    /// <summary>For each half-edge the opposite half-edge, or -1 on the convex hull.</summary>
    public int[] Halfedges = new int[0];

    /// <summary>Convex hull point indices, counter-clockwise.</summary>
    public int[] Hull = new int[0];

    int[] triangles;
    int[] halfedges;
    int trianglesLen;
    readonly int[] hullPrev;
    readonly int[] hullNext;
    readonly int[] hullTri;
    readonly int[] hullHash;
    readonly int hashSize;
    int hullStart;
    double cx, cy;
    readonly int[] edgeStack = new int[1024];

    public int TriangleCount => Triangles.Length / 3;

    public Delaunay(double[] coords)
    {
      Coords = coords ?? throw new ArgumentNullException(nameof(coords));
      int n = coords.Length >> 1;
      if (n < 3)
        return;

      int maxTriangles = Math.Max(2 * n - 5, 0);
      triangles = new int[maxTriangles * 3];
      halfedges = new int[maxTriangles * 3];
      hashSize = (int)Math.Ceiling(Math.Sqrt(n));
      hullPrev = new int[n];
      hullNext = new int[n];
      hullTri = new int[n];
      hullHash = new int[hashSize];
      Triangulate(n);
    }

    void Triangulate(int n)
    {
      double[] coords = Coords;
      var ids = new int[n];
      var dists = new double[n];

      double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
      double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
      for (int i = 0; i < n; i++)
      {
        double x = coords[2 * i], y = coords[2 * i + 1];
        if (x < minX) minX = x;
        if (y < minY) minY = y;
        if (x > maxX) maxX = x;
        if (y > maxY) maxY = y;
        ids[i] = i;
      }
      double bcx = (minX + maxX) / 2, bcy = (minY + maxY) / 2;

      int i0 = 0, i1 = -1, i2 = -1;
      double minDist = double.PositiveInfinity;
      for (int i = 0; i < n; i++)
      {
        double d = Dist(bcx, bcy, coords[2 * i], coords[2 * i + 1]);
        if (d < minDist)
        {
          i0 = i;
          minDist = d;
        }
      }
      double i0x = coords[2 * i0], i0y = coords[2 * i0 + 1];

      minDist = double.PositiveInfinity;
      for (int i = 0; i < n; i++)
      {
        if (i == i0) continue;
        double d = Dist(i0x, i0y, coords[2 * i], coords[2 * i + 1]);
        if (d < minDist && d > 0)
        {
          i1 = i;
          minDist = d;
        }
      }
      if (i1 < 0)
        return; // every point is the same
      double i1x = coords[2 * i1], i1y = coords[2 * i1 + 1];

      double minRadius = double.PositiveInfinity;
      for (int i = 0; i < n; i++)
      {
        if (i == i0 || i == i1) continue;
        double r = Circumradius2(i0x, i0y, i1x, i1y, coords[2 * i], coords[2 * i + 1]);
        if (r < minRadius)
        {
          i2 = i;
          minRadius = r;
        }
      }
      if (i2 < 0 || double.IsInfinity(minRadius))
        return; // every point is on one line, nothing to triangulate
      double i2x = coords[2 * i2], i2y = coords[2 * i2 + 1];

      // Make the seed triangle counter-clockwise.
      if (Orient(i0x, i0y, i1x, i1y, i2x, i2y) < 0)
      {
        int ti = i1; double tx = i1x, ty = i1y;
        i1 = i2; i1x = i2x; i1y = i2y;
        i2 = ti; i2x = tx; i2y = ty;
      }

      Circumcenter(i0x, i0y, i1x, i1y, i2x, i2y, out cx, out cy);

      for (int i = 0; i < n; i++)
        dists[i] = Dist(coords[2 * i], coords[2 * i + 1], cx, cy);

      // Points are added in order of distance from the seed circle, so each new point is
      // outside the hull of the points before it.
      Array.Sort(dists, ids);

      hullStart = i0;
      int hullSize = 3;

      hullNext[i0] = hullPrev[i2] = i1;
      hullNext[i1] = hullPrev[i0] = i2;
      hullNext[i2] = hullPrev[i1] = i0;

      hullTri[i0] = 0;
      hullTri[i1] = 1;
      hullTri[i2] = 2;

      for (int i = 0; i < hashSize; i++)
        hullHash[i] = -1;
      hullHash[HashKey(i0x, i0y)] = i0;
      hullHash[HashKey(i1x, i1y)] = i1;
      hullHash[HashKey(i2x, i2y)] = i2;

      trianglesLen = 0;
      AddTriangle(i0, i1, i2, -1, -1, -1);

      double xp = 0, yp = 0;
      for (int k = 0; k < n; k++)
      {
        int i = ids[k];
        double x = coords[2 * i], y = coords[2 * i + 1];

        if (k > 0 && Math.Abs(x - xp) <= Epsilon && Math.Abs(y - yp) <= Epsilon)
          continue; // duplicate
        xp = x;
        yp = y;

        if (i == i0 || i == i1 || i == i2)
          continue;

        // Find a hull edge visible from the point, starting from the angular hash.
        int start = 0;
        for (int j = 0, key = HashKey(x, y); j < hashSize; j++)
        {
          start = hullHash[(key + j) % hashSize];
          if (start != -1 && start != hullNext[start])
            break;
        }

        start = hullPrev[start];
        int e = start, q;
        while (true)
        {
          q = hullNext[e];
          if (Orient(coords[2 * e], coords[2 * e + 1], coords[2 * q], coords[2 * q + 1], x, y) < 0)
            break;
          e = q;
          if (e == start)
          {
            e = -1;
            break;
          }
        }
        if (e == -1)
          continue; // on the hull already (a near duplicate)

        int t = AddTriangle(e, i, hullNext[e], -1, -1, hullTri[e]);
        hullTri[i] = Legalize(t + 2);
        hullTri[e] = t;
        hullSize++;

        // Walk forward along the hull, adding triangles while edges stay visible.
        int nn = hullNext[e];
        while (true)
        {
          q = hullNext[nn];
          if (!(Orient(coords[2 * nn], coords[2 * nn + 1], coords[2 * q], coords[2 * q + 1], x, y) < 0))
            break;
          t = AddTriangle(nn, i, q, hullTri[i], -1, hullTri[nn]);
          hullTri[i] = Legalize(t + 2);
          hullNext[nn] = nn; // removed from the hull
          hullSize--;
          nn = q;
        }

        // And backward from the other side.
        if (e == start)
        {
          while (true)
          {
            q = hullPrev[e];
            if (!(Orient(coords[2 * q], coords[2 * q + 1], coords[2 * e], coords[2 * e + 1], x, y) < 0))
              break;
            t = AddTriangle(q, i, e, -1, hullTri[e], hullTri[q]);
            Legalize(t + 2);
            hullTri[q] = t;
            hullNext[e] = e;
            hullSize--;
            e = q;
          }
        }

        hullStart = hullPrev[i] = e;
        hullNext[e] = hullPrev[nn] = i;
        hullNext[i] = nn;

        hullHash[HashKey(x, y)] = i;
        hullHash[HashKey(coords[2 * e], coords[2 * e + 1])] = e;
      }

      Hull = new int[hullSize];
      for (int i = 0, e = hullStart; i < hullSize; i++)
      {
        Hull[i] = e;
        e = hullNext[e];
      }

      Triangles = new int[trianglesLen];
      Halfedges = new int[trianglesLen];
      Array.Copy(triangles, Triangles, trianglesLen);
      Array.Copy(halfedges, Halfedges, trianglesLen);
      triangles = null;
      halfedges = null;
    }

    int HashKey(double x, double y)
    {
      double angle = PseudoAngle(x - cx, y - cy);
      if (double.IsNaN(angle))
        return 0;
      int key = (int)Math.Floor(angle * hashSize) % hashSize;
      return key < 0 ? key + hashSize : key;
    }

    // Flips edges until the triangles around half-edge a satisfy the Delaunay condition.
    int Legalize(int a)
    {
      int i = 0;
      int ar;

      while (true)
      {
        int b = halfedges[a];
        int a0 = a - a % 3;
        ar = a0 + (a + 2) % 3;

        if (b == -1)
        {
          if (i == 0) break;
          a = edgeStack[--i];
          continue;
        }

        int b0 = b - b % 3;
        int al = a0 + (a + 1) % 3;
        int bl = b0 + (b + 2) % 3;

        int p0 = triangles[ar];
        int pr = triangles[a];
        int pl = triangles[al];
        int p1 = triangles[bl];

        bool illegal = InCircle(
          Coords[2 * p0], Coords[2 * p0 + 1],
          Coords[2 * pr], Coords[2 * pr + 1],
          Coords[2 * pl], Coords[2 * pl + 1],
          Coords[2 * p1], Coords[2 * p1 + 1]);

        if (illegal)
        {
          triangles[a] = p1;
          triangles[b] = p0;

          int hbl = halfedges[bl];

          // The flipped edge was on the hull on the far side; repoint the hull triangle.
          if (hbl == -1)
          {
            int e = hullStart;
            do
            {
              if (hullTri[e] == bl)
              {
                hullTri[e] = a;
                break;
              }
              e = hullPrev[e];
            } while (e != hullStart);
          }
          Link(a, hbl);
          Link(b, halfedges[ar]);
          Link(ar, bl);

          int br = b0 + (b + 1) % 3;
          if (i < edgeStack.Length)
            edgeStack[i++] = br;
        }
        else
        {
          if (i == 0) break;
          a = edgeStack[--i];
        }
      }
      return ar;
    }

    void Link(int a, int b)
    {
      halfedges[a] = b;
      if (b != -1)
        halfedges[b] = a;
    }

    int AddTriangle(int i0, int i1, int i2, int a, int b, int c)
    {
      int t = trianglesLen;
      triangles[t] = i0;
      triangles[t + 1] = i1;
      triangles[t + 2] = i2;
      Link(t, a);
      Link(t + 1, b);
      Link(t + 2, c);
      trianglesLen += 3;
      return t;
    }

    /// <summary>Positive when a, b, c turn counter-clockwise (y-up).</summary>
    public static double Orient(double ax, double ay, double bx, double by, double cx, double cy) =>
      (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);

    // True when p is inside the circle through the counter-clockwise triangle a, b, c.
    static bool InCircle(double ax, double ay, double bx, double by, double cx, double cy, double px, double py)
    {
      double dx = ax - px, dy = ay - py;
      double ex = bx - px, ey = by - py;
      double fx = cx - px, fy = cy - py;
      double ap = dx * dx + dy * dy;
      double bp = ex * ex + ey * ey;
      double cp = fx * fx + fy * fy;
      return dx * (ey * cp - bp * fy) - dy * (ex * cp - bp * fx) + ap * (ex * fy - ey * fx) > 0;
    }

    static double PseudoAngle(double dx, double dy)
    {
      double p = dx / (Math.Abs(dx) + Math.Abs(dy));
      return (dy > 0 ? 3 - p : 1 + p) / 4;
    }

    static double Dist(double ax, double ay, double bx, double by)
    {
      double dx = ax - bx, dy = ay - by;
      return dx * dx + dy * dy;
    }

    /// <summary>Squared circumradius; infinity (or NaN) for collinear points.</summary>
    public static double Circumradius2(double ax, double ay, double bx, double by, double cx, double cy)
    {
      double dx = bx - ax, dy = by - ay;
      double ex = cx - ax, ey = cy - ay;
      double bl = dx * dx + dy * dy;
      double cl = ex * ex + ey * ey;
      double d = 0.5 / (dx * ey - dy * ex);
      double x = (ey * bl - dy * cl) * d;
      double y = (dx * cl - ex * bl) * d;
      return x * x + y * y;
    }

    static void Circumcenter(double ax, double ay, double bx, double by, double cx, double cy, out double x, out double y)
    {
      double dx = bx - ax, dy = by - ay;
      double ex = cx - ax, ey = cy - ay;
      double bl = dx * dx + dy * dy;
      double cl = ex * ex + ey * ey;
      double d = 0.5 / (dx * ey - dy * ex);
      x = ax + (ey * bl - dy * cl) * d;
      y = ay + (dx * cl - ex * bl) * d;
    }
  }
}

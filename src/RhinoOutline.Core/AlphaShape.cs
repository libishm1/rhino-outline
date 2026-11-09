// RhinoOutline - image to outline curves for Rhino.
// Copyright (C) 2026 RhinoOutline contributors. Licensed under the GNU GPL v2, see LICENSE.
//
// Alpha shape of a traced image.
//
// The traced paths are sampled densely and triangulated. A triangle whose centroid lies in the
// shape is part of the region; a triangle outside the shape is added when its circumradius is at
// most alpha. That is the classic alpha shape in the limit where the inside of the shape is
// sampled infinitely densely, so alpha only decides which background gaps get bridged:
// gaps narrower than about 2 * alpha close up. The region's boundary is the outline.
//
// This replaces the 2012 alphaShape Grasshopper component, which tested every Delaunay edge
// against circles of radius R through its end points. It also kept edges longer than 2R (it
// fell back to the midpoint when R was too small), output every edge twice when both circles
// were empty, and left the edges unordered for JoinCurves. Here the boundary comes out as closed,
// oriented loops: outer loops counter-clockwise (y-up frame) and holes clockwise.

using System;
using System.Collections.Generic;

namespace RhinoOutline.Core
{
  /// <summary>Samples of the traced paths, their Delaunay triangulation and per-triangle data.</summary>
  public sealed class TriangulatedTrace
  {
    public List<TracePath> Paths;

    /// <summary>Sample coordinates, x and y interleaved, in pixels.</summary>
    public double[] Coords;

    /// <summary>Index of the path each sample lies on.</summary>
    public int[] PathOf;

    /// <summary>Position of the sample among the samples of its path.</summary>
    public int[] IndexOnPath;

    /// <summary>Path parameter of the sample (segment index + local parameter).</summary>
    public double[] ParamOf;

    /// <summary>Number of samples kept on each path.</summary>
    public int[] PathSampleCount;

    /// <summary>Per path: open (a stroke) rather than closed.</summary>
    public bool[] PathOpen;

    public Delaunay Delaunay;

    /// <summary>Per triangle: the centroid is inside the shape.</summary>
    public bool[] Inside;

    /// <summary>Per triangle: circumradius in pixels.</summary>
    public double[] Radius;

    public double SampleSpacing;

    public int PointCount => Coords.Length / 2;

    public Vec2 Point(int i) => new Vec2(Coords[2 * i], Coords[2 * i + 1]);
  }

  /// <summary>One closed boundary of the alpha shape, as indices into the samples.</summary>
  public sealed class OutlineLoop
  {
    public int[] Vertices;

    /// <summary>Shoelace area in image space. Positive for outer loops, negative for holes.</summary>
    public double SignedArea;

    public bool IsHole => SignedArea < 0;

    public List<OutlinePiece> Pieces;
  }

  /// <summary>
  /// A stretch of an outline loop: either a run along one traced path, or a straight bridge
  /// between two samples. Runs let the plug-in reuse the exact traced curves.
  /// </summary>
  public struct OutlinePiece
  {
    public bool IsBridge;

    /// <summary>The run covers the whole closed path.</summary>
    public bool IsFullPath;

    public int Path;

    /// <summary>True when the run follows the path direction.</summary>
    public bool Forward;

    public int FromVertex;
    public int ToVertex;
  }

  /// <summary>Even-odd point containment for the closed sample polygons, bucketed by pixel row.</summary>
  sealed class EvenOdd
  {
    readonly double[] c;
    readonly List<int>[] rows;
    readonly double y0;

    // Samples of one path are consecutive; each closed path closes back on its first sample.
    // Open paths bound no area and are left out.
    public EvenOdd(double[] coords, List<int> pathOf, bool[] open = null)
    {
      c = coords;
      int n = pathOf.Count;
      double minY = double.PositiveInfinity, maxY = double.NegativeInfinity;
      for (int i = 0; i < n; i++)
      {
        minY = Math.Min(minY, coords[2 * i + 1]);
        maxY = Math.Max(maxY, coords[2 * i + 1]);
      }
      y0 = n > 0 ? Math.Floor(minY) : 0;
      rows = new List<int>[n > 0 ? (int)(Math.Floor(maxY) - y0) + 1 : 0];
      int first = 0;
      edges = new List<int>(2 * n);
      for (int i = 0; i < n; i++)
      {
        bool last = i + 1 == n || pathOf[i + 1] != pathOf[i];
        int j = last ? first : i + 1;
        if (last) first = i + 1;
        if (i == j || (open != null && open[pathOf[i]])) continue;
        int e = edges.Count / 2;
        edges.Add(i);
        edges.Add(j);
        int r0 = (int)(Math.Floor(Math.Min(coords[2 * i + 1], coords[2 * j + 1])) - y0);
        int r1 = (int)(Math.Floor(Math.Max(coords[2 * i + 1], coords[2 * j + 1])) - y0);
        for (int r = r0; r <= r1; r++)
          (rows[r] ?? (rows[r] = new List<int>())).Add(e);
      }
    }

    readonly List<int> edges;

    public bool Contains(double x, double y)
    {
      int r = (int)(Math.Floor(y) - y0);
      if (r < 0 || r >= rows.Length || rows[r] == null)
        return false;
      bool inside = false;
      foreach (int e in rows[r])
      {
        int a = edges[2 * e], b = edges[2 * e + 1];
        double ay = c[2 * a + 1], by = c[2 * b + 1];
        if ((ay > y) == (by > y))
          continue;
        double ax = c[2 * a], bx = c[2 * b];
        if (ax + (y - ay) / (by - ay) * (bx - ax) > x)
          inside = !inside;
      }
      return inside;
    }
  }

  public static class AlphaShape
  {
    const double Snap = 256.0;

    /// <summary>Samples and triangulates the traced paths.</summary>
    /// <param name="spacing">Target distance between samples in pixels.</param>
    /// <param name="maxPoints">The spacing grows when the paths would need more samples than this.</param>
    public static TriangulatedTrace Prepare(List<TracePath> paths, double spacing = 0.75, int maxPoints = 300000)
    {
      double total = 0;
      foreach (TracePath p in paths)
      {
        foreach (TraceSegment s in p.Segments)
          total += s.ApproxLength;
      }
      if (total / spacing > maxPoints)
        spacing = total / maxPoints;

      var xs = new List<double>();
      var ys = new List<double>();
      var pathOf = new List<int>();
      var indexOnPath = new List<int>();
      var paramOf = new List<double>();
      var pathCount = new int[paths.Count];
      var seen = new HashSet<long>();

      for (int pi = 0; pi < paths.Count; pi++)
      {
        List<TraceSegment> segments = paths[pi].Segments;
        int index = 0;
        for (int k = 0; k < segments.Count; k++)
        {
          TraceSegment s = segments[k];
          int count = Math.Max(1, (int)Math.Ceiling(s.ApproxLength / spacing));
          for (int j = 0; j < count; j++)
          {
            double t = (double)j / count;
            Vec2 p = s.PointAt(t);
            long qx = (long)Math.Round(p.X * Snap);
            long qy = (long)Math.Round(p.Y * Snap);
            if (!seen.Add((qx << 32) ^ (qy & 0xffffffffL)))
              continue;
            xs.Add(qx / Snap);
            ys.Add(qy / Snap);
            pathOf.Add(pi);
            indexOnPath.Add(index++);
            paramOf.Add(k + t);
          }
        }
        // An open path also gets its end point (a closed path ends where it starts).
        if (paths[pi].IsOpen && segments.Count > 0)
        {
          Vec2 p = segments[segments.Count - 1].PointAt(1.0);
          long qx = (long)Math.Round(p.X * Snap);
          long qy = (long)Math.Round(p.Y * Snap);
          if (seen.Add((qx << 32) ^ (qy & 0xffffffffL)))
          {
            xs.Add(qx / Snap);
            ys.Add(qy / Snap);
            pathOf.Add(pi);
            indexOnPath.Add(index++);
            paramOf.Add(segments.Count);
          }
        }
        pathCount[pi] = index;
      }
      var pathOpen = new bool[paths.Count];
      for (int pi = 0; pi < paths.Count; pi++)
        pathOpen[pi] = paths[pi].IsOpen;

      var coords = new double[xs.Count * 2];
      for (int i = 0; i < xs.Count; i++)
      {
        coords[2 * i] = xs[i];
        coords[2 * i + 1] = ys[i];
      }

      var delaunay = new Delaunay(coords);
      int[] tri = delaunay.Triangles;
      int nt = tri.Length / 3;
      var inside = new bool[nt];
      var radius = new double[nt];
      var parity = new EvenOdd(coords, pathOf, pathOpen);
      for (int t = 0; t < nt; t++)
      {
        int a = tri[3 * t], b = tri[3 * t + 1], c = tri[3 * t + 2];
        double ax = coords[2 * a], ay = coords[2 * a + 1];
        double bx = coords[2 * b], by = coords[2 * b + 1];
        double cx = coords[2 * c], cy = coords[2 * c + 1];
        double r2 = Delaunay.Circumradius2(ax, ay, bx, by, cx, cy);
        radius[t] = double.IsNaN(r2) ? double.PositiveInfinity : Math.Sqrt(r2);
        // Even-odd test against the sampled paths themselves. A pixel lookup misjudges the thin
        // triangles between neighbouring samples, and every misjudged sliver became a separate
        // "piece" that the automatic alpha then had to bridge.
        inside[t] = parity.Contains((ax + bx + cx) / 3.0, (ay + by + cy) / 3.0);
      }

      return new TriangulatedTrace
      {
        Paths = paths,
        Coords = coords,
        PathOf = pathOf.ToArray(),
        IndexOnPath = indexOnPath.ToArray(),
        ParamOf = paramOf.ToArray(),
        PathSampleCount = pathCount,
        PathOpen = pathOpen,
        Delaunay = delaunay,
        Inside = inside,
        Radius = radius,
        SampleSpacing = spacing,
      };
    }

    /// <summary>
    /// Stops slivers from counting as bridgeable background. Samples along a straight stretch are
    /// almost collinear (they are only snapped apart), so the triangles between them have no width
    /// but can have a small circumradius. Around a traced image they only hug a filled boundary;
    /// along an open stroke they would turn the stroke into a zero-width band that the outline
    /// runs out and back along, and along any straight curve they make the outline hop between
    /// samples. A background triangle thinner than <paramref name="minHeight"/> is never kept.
    /// Used for curves.
    /// </summary>
    public static void IgnorePathSlivers(TriangulatedTrace tt, double minHeight = 0.05)
    {
      int[] tri = tt.Delaunay.Triangles;
      double[] c = tt.Coords;
      for (int t = 0; t < tri.Length / 3; t++)
      {
        if (tt.Inside[t])
          continue;
        int a = tri[3 * t], b = tri[3 * t + 1], d = tri[3 * t + 2];
        double abx = c[2 * b] - c[2 * a], aby = c[2 * b + 1] - c[2 * a + 1];
        double adx = c[2 * d] - c[2 * a], ady = c[2 * d + 1] - c[2 * a + 1];
        double bdx = c[2 * d] - c[2 * b], bdy = c[2 * d + 1] - c[2 * b + 1];
        double longest = Math.Sqrt(Math.Max(abx * abx + aby * aby, Math.Max(adx * adx + ady * ady, bdx * bdx + bdy * bdy)));
        double height = Math.Abs(abx * ady - aby * adx) / Math.Max(longest, 1e-12);
        if (height < minHeight)
          tt.Radius[t] = double.PositiveInfinity;
      }
    }

    /// <summary>
    /// The smallest alpha for which the shape plus the bridged background is a single piece
    /// (triangles joined through edges, not just corners). 0 when the shape is one piece already.
    /// </summary>
    public static double FindAutoAlpha(TriangulatedTrace tt)
    {
      int[] he = tt.Delaunay.Halfedges;
      int nt = tt.Radius.Length;
      if (nt == 0)
        return 0;

      var parent = new int[nt];
      var kept = new bool[nt];
      for (int t = 0; t < nt; t++)
        parent[t] = t;

      int components = 0;
      for (int t = 0; t < nt; t++)
      {
        if (tt.Inside[t])
        {
          kept[t] = true;
          components++;
        }
      }
      for (int t = 0; t < nt; t++)
      {
        if (kept[t])
          components -= JoinNeighbours(t, he, kept, parent);
      }
      if (components <= 1)
        return 0;

      var order = new List<int>();
      for (int t = 0; t < nt; t++)
      {
        if (!kept[t] && !double.IsInfinity(tt.Radius[t]))
          order.Add(t);
      }
      order.Sort((a, b) => tt.Radius[a].CompareTo(tt.Radius[b]));

      foreach (int t in order)
      {
        kept[t] = true;
        components++;
        components -= JoinNeighbours(t, he, kept, parent);
        if (components == 1)
          return tt.Radius[t];
      }
      return order.Count > 0 ? tt.Radius[order[order.Count - 1]] : 0;
    }

    /// <summary>
    /// Auto alpha for curves (paths that are walls, see <see cref="Extract"/>): the smallest alpha
    /// for which the filled region the outline is drawn from is one piece (triangles joined
    /// through edges) and has every sample of every path as a corner, so every curve is wrapped,
    /// strokes included. Found by bisection over the triangle radii.
    /// </summary>
    public static double FindAutoAlphaForCurves(TriangulatedTrace tt)
    {
      int nt = tt.Radius.Length;
      if (nt == 0)
        return 0;
      var radii = new List<double> { 0 };
      for (int t = 0; t < nt; t++)
      {
        if (!tt.Inside[t] && !double.IsInfinity(tt.Radius[t]))
          radii.Add(tt.Radius[t]);
      }
      radii.Sort();

      bool Wraps(double alpha)
      {
        bool[] filled = Filled(tt, alpha);
        int[] tri = tt.Delaunay.Triangles;
        int[] he = tt.Delaunay.Halfedges;
        var corner = new bool[tt.PointCount];
        int first = -1;
        for (int t = 0; t < nt; t++)
        {
          if (!filled[t]) continue;
          if (first < 0) first = t;
          corner[tri[3 * t]] = corner[tri[3 * t + 1]] = corner[tri[3 * t + 2]] = true;
        }
        if (first < 0) return false;
        foreach (bool c in corner)
          if (!c) return false;
        // one piece through edges
        var seen = new bool[nt];
        var queue = new Queue<int>();
        seen[first] = true;
        queue.Enqueue(first);
        int reached = 1;
        while (queue.Count > 0)
        {
          int t = queue.Dequeue();
          for (int e = 3 * t; e < 3 * t + 3; e++)
          {
            int twin = he[e];
            if (twin < 0) continue;
            int u = twin / 3;
            if (!filled[u] || seen[u]) continue;
            seen[u] = true;
            reached++;
            queue.Enqueue(u);
          }
        }
        int total = 0;
        foreach (bool f in filled) if (f) total++;
        return reached == total;
      }

      // Bisection for the first radius that wraps. The upper end always wraps, so the result
      // does too; the test is not strictly monotonic in rare cases, where the result may be a
      // little larger than the smallest radius that wraps.
      int lo = 0, hi = radii.Count - 1;
      if (!Wraps(radii[hi]))
        return radii[hi];
      while (lo < hi)
      {
        int mid = (lo + hi) / 2;
        if (Wraps(radii[mid])) hi = mid;
        else lo = mid + 1;
      }
      return radii[lo];
    }

    static int JoinNeighbours(int t, int[] he, bool[] kept, int[] parent)
    {
      int merged = 0;
      for (int e = 3 * t; e < 3 * t + 3; e++)
      {
        int twin = he[e];
        if (twin < 0) continue;
        int u = twin / 3;
        if (!kept[u]) continue;
        int ra = Find(parent, t), rb = Find(parent, u);
        if (ra == rb) continue;
        parent[ra] = rb;
        merged++;
      }
      return merged;
    }

    static int Find(int[] parent, int x)
    {
      while (parent[x] != x)
      {
        parent[x] = parent[parent[x]];
        x = parent[x];
      }
      return x;
    }

    /// <summary>Boundary loops of the alpha shape, largest first.</summary>
    /// <param name="alpha">Bridging radius in pixels. 0 bridges nothing.</param>
    /// <param name="includeHoles">Also return the loops around enclosed background.</param>
    /// <param name="pathsAreWalls">
    /// The paths are curves the outside cannot cross (OutlineCurve). Open paths enclose no area,
    /// so without this the outside can reach into the shape between two neighbouring samples of
    /// a stroke and cut a hairline notch into the outline. With it, background that the outside
    /// cannot reach without crossing a path counts as enclosed: filled, or a hole.
    /// </param>
    public static List<OutlineLoop> Extract(TriangulatedTrace tt, double alpha, bool includeHoles, bool pathsAreWalls = false)
    {
      int[] tri = tt.Delaunay.Triangles;
      int nt = tri.Length / 3;
      var kept = new bool[nt];
      for (int t = 0; t < nt; t++)
        kept[t] = tt.Inside[t] || tt.Radius[t] <= alpha;
      if (!pathsAreWalls)
        return Loops(tt, kept, includeHoles, true);

      bool[] outside = Outside(tt, kept);
      // Outer outlines: the boundary of everything the outside does not reach.
      var filled = new bool[nt];
      for (int t = 0; t < nt; t++)
        filled[t] = !outside[t];
      List<OutlineLoop> loops = Loops(tt, filled, false, true);
      if (includeHoles)
      {
        // Holes: the enclosed background, as the holes of the region "kept or outside".
        var open = new bool[nt];
        for (int t = 0; t < nt; t++)
          open[t] = kept[t] || outside[t];
        loops.AddRange(Loops(tt, open, true, false));
        loops.Sort((a, b) => Math.Abs(b.SignedArea).CompareTo(Math.Abs(a.SignedArea)));
      }
      return loops;
    }

    // Background reached from the convex hull without crossing a path (paths are walls).
    static bool[] Outside(TriangulatedTrace tt, bool[] kept)
    {
      int[] tri = tt.Delaunay.Triangles;
      int[] he = tt.Delaunay.Halfedges;
      int nt = tri.Length / 3;
      var outside = new bool[nt];
      var queue = new Queue<int>();
      // A hull edge that is itself a path step is a wall too: the outside is beyond it.
      for (int e = 0; e < tri.Length; e++)
      {
        int t = e / 3;
        if (he[e] < 0 && !kept[t] && !outside[t] && StepKind(tt, tri[e], tri[Next(e)]) == 0)
        {
          outside[t] = true;
          queue.Enqueue(t);
        }
      }
      while (queue.Count > 0)
      {
        int t = queue.Dequeue();
        for (int e = 3 * t; e < 3 * t + 3; e++)
        {
          int twin = he[e];
          if (twin < 0) continue;
          int u = twin / 3;
          if (kept[u] || outside[u] || StepKind(tt, tri[e], tri[Next(e)]) != 0) continue;
          outside[u] = true;
          queue.Enqueue(u);
        }
      }
      return outside;
    }

    // Kept triangles plus the background enclosed by them and the paths.
    static bool[] Filled(TriangulatedTrace tt, double alpha)
    {
      int nt = tt.Radius.Length;
      var kept = new bool[nt];
      for (int t = 0; t < nt; t++)
        kept[t] = tt.Inside[t] || tt.Radius[t] <= alpha;
      bool[] outside = Outside(tt, kept);
      var filled = new bool[nt];
      for (int t = 0; t < nt; t++)
        filled[t] = !outside[t];
      return filled;
    }

    // Boundary loops of the region made of the kept triangles.
    static List<OutlineLoop> Loops(TriangulatedTrace tt, bool[] kept, bool holes, bool outer)
    {
      int[] tri = tt.Delaunay.Triangles;
      int[] he = tt.Delaunay.Halfedges;

      bool IsBoundary(int e) => kept[e / 3] && (he[e] < 0 || !kept[he[e] / 3]);

      var loops = new List<OutlineLoop>();
      var used = new bool[tri.Length];
      var vertices = new List<int>();
      for (int start = 0; start < tri.Length; start++)
      {
        if (used[start] || !IsBoundary(start))
          continue;

        vertices.Clear();
        int e = start;
        do
        {
          used[e] = true;
          vertices.Add(tri[e]);
          // Rotate around the end vertex through kept triangles to the next boundary edge.
          int next = Next(e);
          for (int guard = 0; !IsBoundary(next) && guard < 10000; guard++)
            next = Next(he[next]);
          e = next;
        } while (e != start && !used[e]);

        // Where a gap in the region touches its boundary at a single sample (a pinch), the walk
        // passes that sample twice; split it there so every loop is simple.
        foreach (int[] cycle in SimpleCycles(vertices))
        {
          if (cycle.Length < 3)
            continue;
          var loop = new OutlineLoop { Vertices = cycle };
          loop.SignedArea = SignedArea(tt.Coords, loop.Vertices);
          if (loop.SignedArea == 0 || loop.IsHole && !holes || !loop.IsHole && !outer)
            continue;
          loop.Pieces = SplitIntoPieces(tt, loop.Vertices);
          loops.Add(loop);
        }
      }

      loops.Sort((a, b) => Math.Abs(b.SignedArea).CompareTo(Math.Abs(a.SignedArea)));
      return loops;
    }

    static int Next(int e) => e % 3 == 2 ? e - 2 : e + 1;

    // Splits a closed walk that may pass a vertex more than once into simple cycles.
    static List<int[]> SimpleCycles(List<int> walk)
    {
      var cycles = new List<int[]>();
      var stack = new List<int>(walk.Count);
      var at = new Dictionary<int, int>();
      foreach (int v in walk)
      {
        if (at.TryGetValue(v, out int i))
        {
          // stack[i..] is a closed cycle through v; v itself stays for the rest of the walk
          cycles.Add(stack.GetRange(i, stack.Count - i).ToArray());
          for (int k = i + 1; k < stack.Count; k++)
            at.Remove(stack[k]);
          stack.RemoveRange(i + 1, stack.Count - i - 1);
          continue;
        }
        at[v] = stack.Count;
        stack.Add(v);
      }
      cycles.Add(stack.ToArray());
      return cycles;
    }

    static double SignedArea(double[] coords, int[] loop)
    {
      double area = 0;
      for (int i = 0, j = loop.Length - 1; i < loop.Length; j = i++)
      {
        int a = loop[j], b = loop[i];
        area += coords[2 * a] * coords[2 * b + 1] - coords[2 * b] * coords[2 * a + 1];
      }
      return area / 2;
    }

    // Step from sample a to sample b: +1 forward along a path, -1 backward, 0 a bridge.
    static int StepKind(TriangulatedTrace tt, int a, int b)
    {
      int p = tt.PathOf[a];
      if (tt.PathOf[b] != p)
        return 0;
      int n = tt.PathSampleCount[p];
      int ia = tt.IndexOnPath[a], ib = tt.IndexOnPath[b];
      if (tt.PathOpen != null && tt.PathOpen[p])
        return ib == ia + 1 ? 1 : ia == ib + 1 ? -1 : 0;
      if (ib == (ia + 1) % n)
        return 1;
      if (ia == (ib + 1) % n)
        return -1;
      return 0;
    }

    static List<OutlinePiece> SplitIntoPieces(TriangulatedTrace tt, int[] v)
    {
      int m = v.Length;
      var kinds = new int[m];
      for (int i = 0; i < m; i++)
        kinds[i] = StepKind(tt, v[i], v[(i + 1) % m]);

      var pieces = new List<OutlinePiece>();

      // The whole loop runs along a single path.
      bool allSame = true;
      for (int i = 1; i < m; i++)
      {
        if (kinds[i] != kinds[0] || tt.PathOf[v[i]] != tt.PathOf[v[0]])
        {
          allSame = false;
          break;
        }
      }
      bool open = tt.PathOpen != null && tt.PathOpen[tt.PathOf[v[0]]];
      if (allSame && kinds[0] != 0 && m == tt.PathSampleCount[tt.PathOf[v[0]]] && !open)
      {
        pieces.Add(new OutlinePiece
        {
          IsFullPath = true,
          Path = tt.PathOf[v[0]],
          Forward = kinds[0] > 0,
          FromVertex = v[0],
          ToVertex = v[0],
        });
        return pieces;
      }

      // Start at a step whose kind differs from the step before, so no run wraps around.
      int first = 0;
      for (int i = 0; i < m; i++)
      {
        int prev = (i + m - 1) % m;
        if (kinds[i] == 0 || kinds[i] != kinds[prev] || tt.PathOf[v[i]] != tt.PathOf[v[prev]])
        {
          first = i;
          break;
        }
      }

      for (int s = 0; s < m;)
      {
        int i = (first + s) % m;
        int kind = kinds[i];
        if (kind == 0)
        {
          pieces.Add(new OutlinePiece { IsBridge = true, FromVertex = v[i], ToVertex = v[(i + 1) % m] });
          s++;
          continue;
        }
        int path = tt.PathOf[v[i]];
        int len = 1;
        while (s + len < m)
        {
          int j = (first + s + len) % m;
          if (kinds[j] != kind || tt.PathOf[v[j]] != path)
            break;
          len++;
        }
        pieces.Add(new OutlinePiece
        {
          Path = path,
          Forward = kind > 0,
          FromVertex = v[i],
          ToVertex = v[(first + s + len) % m],
        });
        s += len;
      }
      return pieces;
    }
  }
}

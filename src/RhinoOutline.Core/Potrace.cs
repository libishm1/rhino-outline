// RhinoOutline - image to outline curves for Rhino.
//
// C# port of Potrace (Copyright (C) 2001-2013 Peter Selinger, GPL), by way of potrace-js by
// kilobtye (GPL) and the C# port inside the Vectorize plug-in for Rhino by Dale Fugier (GPL v2).
// This file was recovered from Vectorize 7.0.8438 and checked line by line against potrace-js.
// Differences from that binary, all of them bugs:
//
//  1. calcLon tested "all four directions seen" with ct[k] == 1. The first run always counts the
//     first direction twice, so the test almost never passed. Potrace tests ct[k] != 0.
//  2. calcLon used truncating integer division for the pivot step; Potrace uses floor division.
//  3. A corner segment is two lines (previous end -> vertex -> segment end). Vectorize emitted
//     only the second line and PolyCurve.Append then cut every corner off.
//  4. Thresholding ignored alpha, so transparent pixels stored as RGB(0,0,0) traced as black.
//     Thresholding now lives in RasterImage.cs.
//
// Licensed under the GNU GPL v2, see LICENSE.

using System;
using System.Collections.Generic;

namespace RhinoOutline.Core
{
  public enum TurnPolicy
  {
    Minority,
    Majority,
    Right,
    Black,
    White,
  }

  public sealed class PotraceSettings
  {
    public TurnPolicy TurnPolicy = TurnPolicy.Minority;

    /// <summary>Paths enclosing this many pixels or fewer are dropped as speckles.</summary>
    public int TurdSize = 2;

    /// <summary>Corner threshold: 0 keeps every corner (polygon), 4/3 or more smooths them all.</summary>
    public double AlphaMax = 1.0;

    public bool OptiCurve = true;

    public double OptTolerance = 0.2;
  }

  public sealed class TraceResult
  {
    public List<TracePath> Paths;

    /// <summary>The traced mask with the dropped speckles removed, so it matches the paths.</summary>
    public BinaryImage Shape;
  }

  public sealed class Potrace
  {
    const int Corner = 1;
    const int CurveTo = 2;

    struct IPoint
    {
      public int X;
      public int Y;

      public IPoint(int x, int y)
      {
        X = x;
        Y = y;
      }
    }

    sealed class Bitmap
    {
      public readonly int W;
      public readonly int H;
      public readonly byte[] Data;

      public Bitmap(int w, int h, byte[] data)
      {
        W = w;
        H = h;
        Data = data;
      }

      public int Size => W * H;

      public bool At(int x, int y) => x >= 0 && x < W && y >= 0 && y < H && Data[W * y + x] == 1;

      public IPoint Index(int i)
      {
        int y = i / W;
        return new IPoint(i - y * W, y);
      }

      public void Flip(int x, int y)
      {
        int i = W * y + x;
        Data[i] = (byte)(Data[i] == 1 ? 0 : 1);
      }

      public Bitmap Copy() => new Bitmap(W, H, (byte[])Data.Clone());
    }

    struct Sum
    {
      public double X, Y, XY, X2, Y2;

      public Sum(double x, double y, double xy, double x2, double y2)
      {
        X = x;
        Y = y;
        XY = xy;
        X2 = x2;
        Y2 = y2;
      }
    }

    sealed class Path
    {
      public int Area;
      public int Len;
      public char Sign = '?';
      public readonly List<IPoint> Pt = new List<IPoint>();
      public int MinX = int.MaxValue, MinY = int.MaxValue, MaxX = -1, MaxY = -1;
      public double X0, Y0;
      public Sum[] Sums;
      public int[] Lon;
      public int M;
      public int[] Po;
      public PrivCurve Curve;
    }

    sealed class PrivCurve
    {
      public readonly int N;
      public readonly int[] Tag;
      public readonly Vec2[] Vertex;
      public readonly Vec2[] C;
      public readonly double[] Alpha;
      public readonly double[] Alpha0;
      public readonly double[] Beta;

      public PrivCurve(int n)
      {
        N = n;
        Tag = new int[n];
        Vertex = new Vec2[n];
        C = new Vec2[n * 3];
        Alpha = new double[n];
        Alpha0 = new double[n];
        Beta = new double[n];
      }
    }

    sealed class Quad
    {
      public readonly double[] Data = new double[9];

      public double At(int x, int y) => Data[x * 3 + y];
    }

    sealed class Opti
    {
      public double Pen;
      public Vec2 C0;
      public Vec2 C1;
      public double T;
      public double S;
      public double Alpha;
    }

    readonly PotraceSettings info;
    readonly Bitmap bm;   // the image being traced
    readonly Bitmap bm1;  // working copy, each path is xor-ed out once found
    readonly Bitmap kept; // the image with dropped speckles xor-ed out
    readonly List<Path> pathlist = new List<Path>();

    Potrace(BinaryImage image, PotraceSettings settings)
    {
      info = settings;
      bm = new Bitmap(image.Width, image.Height, image.Data);
      bm1 = bm.Copy();
      kept = bm.Copy();
    }

    public static TraceResult Trace(BinaryImage image, PotraceSettings settings = null)
    {
      if (image == null)
        throw new ArgumentNullException(nameof(image));
      var potrace = new Potrace(image, settings ?? new PotraceSettings());
      potrace.BmToPathlist();
      foreach (Path path in potrace.pathlist)
      {
        CalcSums(path);
        CalcLon(path);
        BestPolygon(path);
        AdjustVertices(path);
        if (path.Sign == '-')
          Reverse(path);
        potrace.Smooth(path);
        if (potrace.info.OptiCurve)
          potrace.OptiCurve(path);
      }
      return new TraceResult
      {
        Paths = potrace.ToTracePaths(),
        Shape = new BinaryImage(image.Width, image.Height, potrace.kept.Data),
      };
    }

    #region auxiliary functions

    static Vec2 Interval(double lambda, Vec2 a, Vec2 b) =>
      new Vec2(a.X + lambda * (b.X - a.X), a.Y + lambda * (b.Y - a.Y));

    static Vec2 DorthInfty(Vec2 p0, Vec2 p2) => new Vec2(-Sign(p2.Y - p0.Y), Sign(p2.X - p0.X));

    static double DDenom(Vec2 p0, Vec2 p2)
    {
      Vec2 r = DorthInfty(p0, p2);
      return r.Y * (p2.X - p0.X) - r.X * (p2.Y - p0.Y);
    }

    static double DPara(Vec2 p0, Vec2 p1, Vec2 p2)
    {
      double x1 = p1.X - p0.X, y1 = p1.Y - p0.Y;
      double x2 = p2.X - p0.X, y2 = p2.Y - p0.Y;
      return x1 * y2 - x2 * y1;
    }

    static double Cprod(Vec2 p0, Vec2 p1, Vec2 p2, Vec2 p3)
    {
      double x1 = p1.X - p0.X, y1 = p1.Y - p0.Y;
      double x2 = p3.X - p2.X, y2 = p3.Y - p2.Y;
      return x1 * y2 - x2 * y1;
    }

    static double Iprod(Vec2 p0, Vec2 p1, Vec2 p2)
    {
      double x1 = p1.X - p0.X, y1 = p1.Y - p0.Y;
      double x2 = p2.X - p0.X, y2 = p2.Y - p0.Y;
      return x1 * x2 + y1 * y2;
    }

    static double Iprod1(Vec2 p0, Vec2 p1, Vec2 p2, Vec2 p3)
    {
      double x1 = p1.X - p0.X, y1 = p1.Y - p0.Y;
      double x2 = p3.X - p2.X, y2 = p3.Y - p2.Y;
      return x1 * x2 + y1 * y2;
    }

    static double DDist(Vec2 p, Vec2 q) => Math.Sqrt((p.X - q.X) * (p.X - q.X) + (p.Y - q.Y) * (p.Y - q.Y));

    static int Xprod(IPoint p1, IPoint p2) => p1.X * p2.Y - p1.Y * p2.X;

    static bool Cyclic(int a, int b, int c)
    {
      if (a <= c)
        return a <= b && b < c;
      return a <= b || b < c;
    }

    static int Sign(double x) => x > 0 ? 1 : (x < 0 ? -1 : 0);

    static double QuadForm(Quad q, Vec2 w)
    {
      double[] v = { w.X, w.Y, 1.0 };
      double sum = 0.0;
      for (int i = 0; i < 3; i++)
      {
        for (int j = 0; j < 3; j++)
          sum += v[i] * q.At(i, j) * v[j];
      }
      return sum;
    }

    static Vec2 Bezier(double t, Vec2 p0, Vec2 p1, Vec2 p2, Vec2 p3)
    {
      double s = 1 - t;
      return new Vec2(
        s * s * s * p0.X + 3 * (s * s * t) * p1.X + 3 * (t * t * s) * p2.X + t * t * t * p3.X,
        s * s * s * p0.Y + 3 * (s * s * t) * p1.Y + 3 * (t * t * s) * p2.Y + t * t * t * p3.Y);
    }

    static double Tangent(Vec2 p0, Vec2 p1, Vec2 p2, Vec2 p3, Vec2 q0, Vec2 q1)
    {
      double A = Cprod(p0, p1, q0, q1);
      double B = Cprod(p1, p2, q0, q1);
      double C = Cprod(p2, p3, q0, q1);
      double a = A - 2 * B + C;
      double b = -2 * A + 2 * B;
      double c = A;
      double d = b * b - 4 * a * c;
      if (a == 0 || d < 0)
        return -1.0;
      double s = Math.Sqrt(d);
      double r1 = (-b + s) / (2 * a);
      double r2 = (-b - s) / (2 * a);
      if (r1 >= 0 && r1 <= 1)
        return r1;
      if (r2 >= 0 && r2 <= 1)
        return r2;
      return -1.0;
    }

    static int Mod(int a, int n) => a >= n ? a % n : a >= 0 ? a : n - 1 - (-1 - a) % n;

    static int FloorDiv(int a, int n) => a >= 0 ? a / n : -1 - (-1 - a) / n;

    #endregion

    #region decomposition into paths

    void BmToPathlist()
    {
      var current = new IPoint(0, 0);
      while (FindNext(ref current))
      {
        Path path = FindPath(current);
        XorPath(bm1, path);
        if (path.Area > info.TurdSize)
          pathlist.Add(path);
        else
          XorPath(kept, path);
      }
    }

    bool FindNext(ref IPoint point)
    {
      int i = bm1.W * point.Y + point.X;
      while (i < bm1.Size && bm1.Data[i] != 1)
        i++;
      if (i >= bm1.Size)
        return false;
      point = bm1.Index(i);
      return true;
    }

    bool Majority(int x, int y)
    {
      for (int i = 2; i < 5; i++)
      {
        int ct = 0;
        for (int a = -i + 1; a <= i - 1; a++)
        {
          ct += bm1.At(x + a, y + i - 1) ? 1 : -1;
          ct += bm1.At(x + i - 1, y + a - 1) ? 1 : -1;
          ct += bm1.At(x + a - 1, y - i) ? 1 : -1;
          ct += bm1.At(x - i, y + a) ? 1 : -1;
        }
        if (ct > 0)
          return true;
        if (ct < 0)
          return false;
      }
      return false;
    }

    Path FindPath(IPoint point)
    {
      var path = new Path();
      int x = point.X, y = point.Y;
      int dirx = 0, diry = 1, tmp;
      path.Sign = bm.At(point.X, point.Y) ? '+' : '-';

      while (true)
      {
        path.Pt.Add(new IPoint(x, y));
        if (x > path.MaxX) path.MaxX = x;
        if (x < path.MinX) path.MinX = x;
        if (y > path.MaxY) path.MaxY = y;
        if (y < path.MinY) path.MinY = y;
        path.Len++;

        x += dirx;
        y += diry;
        path.Area -= x * diry;

        if (x == point.X && y == point.Y)
          break;

        // Every numerator below is even, so integer division is exact.
        bool l = bm1.At(x + (dirx + diry - 1) / 2, y + (diry - dirx - 1) / 2);
        bool r = bm1.At(x + (dirx - diry - 1) / 2, y + (diry + dirx - 1) / 2);

        if (r && !l)
        {
          if (info.TurnPolicy == TurnPolicy.Right ||
              (info.TurnPolicy == TurnPolicy.Black && path.Sign == '+') ||
              (info.TurnPolicy == TurnPolicy.White && path.Sign == '-') ||
              (info.TurnPolicy == TurnPolicy.Majority && Majority(x, y)) ||
              (info.TurnPolicy == TurnPolicy.Minority && !Majority(x, y)))
          {
            tmp = dirx;
            dirx = -diry;
            diry = tmp;
          }
          else
          {
            tmp = dirx;
            dirx = diry;
            diry = -tmp;
          }
        }
        else if (r)
        {
          tmp = dirx;
          dirx = -diry;
          diry = tmp;
        }
        else if (!l)
        {
          tmp = dirx;
          dirx = diry;
          diry = -tmp;
        }
      }
      return path;
    }

    static void XorPath(Bitmap target, Path path)
    {
      int y1 = path.Pt[0].Y;
      int len = path.Len;
      for (int i = 1; i < len; i++)
      {
        int x = path.Pt[i].X;
        int y = path.Pt[i].Y;
        if (y != y1)
        {
          int minY = y1 < y ? y1 : y;
          int maxX = path.MaxX;
          for (int j = x; j < maxX; j++)
            target.Flip(j, minY);
          y1 = y;
        }
      }
    }

    #endregion

    #region polygon

    static void CalcSums(Path path)
    {
      path.X0 = path.Pt[0].X;
      path.Y0 = path.Pt[0].Y;
      var sums = new Sum[path.Len + 1];
      for (int i = 0; i < path.Len; i++)
      {
        double x = path.Pt[i].X - path.X0;
        double y = path.Pt[i].Y - path.Y0;
        sums[i + 1] = new Sum(sums[i].X + x, sums[i].Y + y, sums[i].XY + x * y, sums[i].X2 + x * x, sums[i].Y2 + y * y);
      }
      path.Sums = sums;
    }

    static void CalcLon(Path path)
    {
      int n = path.Len;
      List<IPoint> pt = path.Pt;
      var pivk = new int[n];
      var nc = new int[n];
      var ct = new int[4];
      path.Lon = new int[n];

      var constraint = new IPoint[2];
      IPoint cur, off, dk;
      int i, j, k1, a, b, c, d, dir;
      int k = 0;
      bool foundk;

      for (i = n - 1; i >= 0; i--)
      {
        if (pt[i].X != pt[k].X && pt[i].Y != pt[k].Y)
          k = i + 1;
        nc[i] = k;
      }

      for (i = n - 1; i >= 0; i--)
      {
        ct[0] = ct[1] = ct[2] = ct[3] = 0;
        dir = (3 + 3 * (pt[Mod(i + 1, n)].X - pt[i].X) + (pt[Mod(i + 1, n)].Y - pt[i].Y)) / 2;
        ct[dir]++;

        constraint[0] = new IPoint(0, 0);
        constraint[1] = new IPoint(0, 0);

        k = nc[i];
        k1 = i;
        while (true)
        {
          foundk = false;
          dir = (3 + 3 * Sign(pt[k].X - pt[k1].X) + Sign(pt[k].Y - pt[k1].Y)) / 2;
          ct[dir]++;

          // Fix 1: all four directions seen (Vectorize tested ct[x] == 1).
          if (ct[0] != 0 && ct[1] != 0 && ct[2] != 0 && ct[3] != 0)
          {
            pivk[i] = k1;
            foundk = true;
            break;
          }

          cur = new IPoint(pt[k].X - pt[i].X, pt[k].Y - pt[i].Y);

          if (Xprod(constraint[0], cur) < 0 || Xprod(constraint[1], cur) > 0)
            break;

          if (Math.Abs(cur.X) > 1 || Math.Abs(cur.Y) > 1)
          {
            off = new IPoint(cur.X + ((cur.Y >= 0 && (cur.Y > 0 || cur.X < 0)) ? 1 : -1),
                             cur.Y + ((cur.X <= 0 && (cur.X < 0 || cur.Y < 0)) ? 1 : -1));
            if (Xprod(constraint[0], off) >= 0)
              constraint[0] = off;

            off = new IPoint(cur.X + ((cur.Y <= 0 && (cur.Y < 0 || cur.X < 0)) ? 1 : -1),
                             cur.Y + ((cur.X >= 0 && (cur.X > 0 || cur.Y < 0)) ? 1 : -1));
            if (Xprod(constraint[1], off) <= 0)
              constraint[1] = off;
          }

          k1 = k;
          k = nc[k1];
          if (!Cyclic(k, i, k1))
            break;
        }

        if (!foundk)
        {
          dk = new IPoint(Sign(pt[k].X - pt[k1].X), Sign(pt[k].Y - pt[k1].Y));
          cur = new IPoint(pt[k1].X - pt[i].X, pt[k1].Y - pt[i].Y);

          a = Xprod(constraint[0], cur);
          b = Xprod(constraint[0], dk);
          c = Xprod(constraint[1], cur);
          d = Xprod(constraint[1], dk);

          // Fix 2: floor division (Vectorize truncated toward zero).
          j = 10000000;
          if (b < 0)
            j = FloorDiv(a, -b);
          if (d > 0)
            j = Math.Min(j, FloorDiv(-c, d));
          pivk[i] = Mod(k1 + j, n);
        }
      }

      j = pivk[n - 1];
      path.Lon[n - 1] = j;
      for (i = n - 2; i >= 0; i--)
      {
        if (Cyclic(i + 1, pivk[i], j))
          j = pivk[i];
        path.Lon[i] = j;
      }

      for (i = n - 1; Cyclic(Mod(i + 1, n), j, path.Lon[i]); i--)
        path.Lon[i] = j;
    }

    static double Penalty3(Path path, int i, int j)
    {
      int n = path.Len;
      List<IPoint> pt = path.Pt;
      Sum[] sums = path.Sums;
      double x, y, xy, x2, y2;
      int k;
      int r = 0;
      if (j >= n)
      {
        j -= n;
        r = 1;
      }

      if (r == 0)
      {
        x = sums[j + 1].X - sums[i].X;
        y = sums[j + 1].Y - sums[i].Y;
        x2 = sums[j + 1].X2 - sums[i].X2;
        xy = sums[j + 1].XY - sums[i].XY;
        y2 = sums[j + 1].Y2 - sums[i].Y2;
        k = j + 1 - i;
      }
      else
      {
        x = sums[j + 1].X - sums[i].X + sums[n].X;
        y = sums[j + 1].Y - sums[i].Y + sums[n].Y;
        x2 = sums[j + 1].X2 - sums[i].X2 + sums[n].X2;
        xy = sums[j + 1].XY - sums[i].XY + sums[n].XY;
        y2 = sums[j + 1].Y2 - sums[i].Y2 + sums[n].Y2;
        k = j + 1 - i + n;
      }

      double px = (pt[i].X + pt[j].X) / 2.0 - pt[0].X;
      double py = (pt[i].Y + pt[j].Y) / 2.0 - pt[0].Y;
      double ey = pt[j].X - pt[i].X;
      double ex = -(pt[j].Y - pt[i].Y);

      double a = (x2 - 2 * x * px) / k + px * px;
      double b = (xy - x * py - y * px) / k + px * py;
      double c = (y2 - 2 * y * py) / k + py * py;

      double s = ex * ex * a + 2 * ex * ey * b + ey * ey * c;
      return Math.Sqrt(s);
    }

    static void BestPolygon(Path path)
    {
      int n = path.Len;
      var pen = new double[n + 1];
      var prev = new int[n + 1];
      var clip0 = new int[n];
      var clip1 = new int[n + 1];
      var seg0 = new int[n + 1];
      var seg1 = new int[n + 1];
      int i, j, k, m, c;

      for (i = 0; i < n; i++)
      {
        c = Mod(path.Lon[Mod(i - 1, n)] - 1, n);
        if (c == i)
          c = Mod(i + 1, n);
        clip0[i] = c < i ? n : c;
      }

      j = 1;
      for (i = 0; i < n; i++)
      {
        while (j <= clip0[i])
        {
          clip1[j] = i;
          j++;
        }
      }

      i = 0;
      for (j = 0; i < n; j++)
      {
        seg0[j] = i;
        i = clip0[i];
      }
      seg0[j] = n;
      m = j;

      i = n;
      for (j = m; j > 0; j--)
      {
        seg1[j] = i;
        i = clip1[i];
      }
      seg1[0] = 0;

      pen[0] = 0;
      for (j = 1; j <= m; j++)
      {
        for (i = seg1[j]; i <= seg0[j]; i++)
        {
          double best = -1;
          for (k = seg0[j - 1]; k >= clip1[i]; k--)
          {
            double thispen = Penalty3(path, k, i) + pen[k];
            if (best < 0 || thispen < best)
            {
              prev[i] = k;
              best = thispen;
            }
          }
          pen[i] = best;
        }
      }

      path.M = m;
      path.Po = new int[m];
      for (i = n, j = m - 1; i > 0; j--)
      {
        i = prev[i];
        path.Po[j] = i;
      }
    }

    #endregion

    #region vertex adjustment and smoothing

    static void PointSlope(Path path, int i, int j, out Vec2 ctr, out Vec2 dir)
    {
      int n = path.Len;
      Sum[] sums = path.Sums;
      int r = 0;

      while (j >= n) { j -= n; r += 1; }
      while (i >= n) { i -= n; r -= 1; }
      while (j < 0) { j += n; r -= 1; }
      while (i < 0) { i += n; r += 1; }

      double x = sums[j + 1].X - sums[i].X + r * sums[n].X;
      double y = sums[j + 1].Y - sums[i].Y + r * sums[n].Y;
      double x2 = sums[j + 1].X2 - sums[i].X2 + r * sums[n].X2;
      double xy = sums[j + 1].XY - sums[i].XY + r * sums[n].XY;
      double y2 = sums[j + 1].Y2 - sums[i].Y2 + r * sums[n].Y2;
      double k = j + 1 - i + r * n;

      ctr = new Vec2(x / k, y / k);

      double a = (x2 - x * x / k) / k;
      double b = (xy - x * y / k) / k;
      double c = (y2 - y * y / k) / k;

      double lambda2 = (a + c + Math.Sqrt((a - c) * (a - c) + 4 * b * b)) / 2;

      a -= lambda2;
      c -= lambda2;

      double l;
      dir = new Vec2(0, 0);
      if (Math.Abs(a) >= Math.Abs(c))
      {
        l = Math.Sqrt(a * a + b * b);
        if (l != 0)
          dir = new Vec2(-b / l, a / l);
      }
      else
      {
        l = Math.Sqrt(c * c + b * b);
        if (l != 0)
          dir = new Vec2(-c / l, b / l);
      }
      if (l == 0)
        dir = new Vec2(0, 0);
    }

    static void AdjustVertices(Path path)
    {
      int m = path.M;
      int[] po = path.Po;
      int n = path.Len;
      List<IPoint> pt = path.Pt;
      double x0 = path.X0, y0 = path.Y0;

      var ctr = new Vec2[m];
      var dir = new Vec2[m];
      var q = new Quad[m];
      var v = new double[3];

      path.Curve = new PrivCurve(m);

      for (int i = 0; i < m; i++)
      {
        int j = po[Mod(i + 1, m)];
        j = Mod(j - po[i], n) + po[i];
        PointSlope(path, po[i], j, out ctr[i], out dir[i]);
      }

      for (int i = 0; i < m; i++)
      {
        q[i] = new Quad();
        double d = dir[i].X * dir[i].X + dir[i].Y * dir[i].Y;
        if (d == 0.0)
          continue;
        v[0] = dir[i].Y;
        v[1] = -dir[i].X;
        v[2] = -v[1] * ctr[i].Y - v[0] * ctr[i].X;
        for (int l = 0; l < 3; l++)
        {
          for (int k = 0; k < 3; k++)
            q[i].Data[l * 3 + k] = v[l] * v[k] / d;
        }
      }

      for (int i = 0; i < m; i++)
      {
        var Q = new Quad();
        var s = new Vec2(pt[po[i]].X - x0, pt[po[i]].Y - y0);
        int j = Mod(i - 1, m);

        for (int l = 0; l < 3; l++)
        {
          for (int k = 0; k < 3; k++)
            Q.Data[l * 3 + k] = q[j].At(l, k) + q[i].At(l, k);
        }

        Vec2 w;
        while (true)
        {
          double det = Q.At(0, 0) * Q.At(1, 1) - Q.At(0, 1) * Q.At(1, 0);
          if (det != 0.0)
          {
            w = new Vec2((-Q.At(0, 2) * Q.At(1, 1) + Q.At(1, 2) * Q.At(0, 1)) / det,
                         (Q.At(0, 2) * Q.At(1, 0) - Q.At(1, 2) * Q.At(0, 0)) / det);
            break;
          }

          if (Q.At(0, 0) > Q.At(1, 1))
          {
            v[0] = -Q.At(0, 1);
            v[1] = Q.At(0, 0);
          }
          else if (Q.At(1, 1) != 0.0)
          {
            v[0] = -Q.At(1, 1);
            v[1] = Q.At(1, 0);
          }
          else
          {
            v[0] = 1;
            v[1] = 0;
          }
          double dd = v[0] * v[0] + v[1] * v[1];
          v[2] = -v[1] * s.Y - v[0] * s.X;
          for (int l = 0; l < 3; l++)
          {
            for (int k = 0; k < 3; k++)
              Q.Data[l * 3 + k] += v[l] * v[k] / dd;
          }
        }

        double dx = Math.Abs(w.X - s.X);
        double dy = Math.Abs(w.Y - s.Y);
        if (dx <= 0.5 && dy <= 0.5)
        {
          path.Curve.Vertex[i] = new Vec2(w.X + x0, w.Y + y0);
          continue;
        }

        double min = QuadForm(Q, s);
        double xmin = s.X, ymin = s.Y;

        if (Q.At(0, 0) != 0.0)
        {
          for (int z = 0; z < 2; z++)
          {
            double wy = s.Y - 0.5 + z;
            double wx = -(Q.At(0, 1) * wy + Q.At(0, 2)) / Q.At(0, 0);
            dx = Math.Abs(wx - s.X);
            double cand = QuadForm(Q, new Vec2(wx, wy));
            if (dx <= 0.5 && cand < min)
            {
              min = cand;
              xmin = wx;
              ymin = wy;
            }
          }
        }

        if (Q.At(1, 1) != 0.0)
        {
          for (int z = 0; z < 2; z++)
          {
            double wx = s.X - 0.5 + z;
            double wy = -(Q.At(1, 0) * wx + Q.At(1, 2)) / Q.At(1, 1);
            dy = Math.Abs(wy - s.Y);
            double cand = QuadForm(Q, new Vec2(wx, wy));
            if (dy <= 0.5 && cand < min)
            {
              min = cand;
              xmin = wx;
              ymin = wy;
            }
          }
        }

        for (int l = 0; l < 2; l++)
        {
          for (int k = 0; k < 2; k++)
          {
            var cw = new Vec2(s.X - 0.5 + l, s.Y - 0.5 + k);
            double cand = QuadForm(Q, cw);
            if (cand < min)
            {
              min = cand;
              xmin = cw.X;
              ymin = cw.Y;
            }
          }
        }

        path.Curve.Vertex[i] = new Vec2(xmin + x0, ymin + y0);
      }
    }

    static void Reverse(Path path)
    {
      Vec2[] v = path.Curve.Vertex;
      for (int i = 0, j = path.Curve.N - 1; i < j; i++, j--)
      {
        Vec2 tmp = v[i];
        v[i] = v[j];
        v[j] = tmp;
      }
    }

    void Smooth(Path path)
    {
      PrivCurve curve = path.Curve;
      int m = curve.N;

      for (int i = 0; i < m; i++)
      {
        int j = Mod(i + 1, m);
        int k = Mod(i + 2, m);
        Vec2 p4 = Interval(0.5, curve.Vertex[k], curve.Vertex[j]);

        double alpha;
        double denom = DDenom(curve.Vertex[i], curve.Vertex[k]);
        if (denom != 0.0)
        {
          double dd = Math.Abs(DPara(curve.Vertex[i], curve.Vertex[j], curve.Vertex[k]) / denom);
          alpha = dd > 1 ? (1 - 1.0 / dd) : 0;
          alpha /= 0.75;
        }
        else
        {
          alpha = 4 / 3.0;
        }
        curve.Alpha0[j] = alpha;

        if (alpha >= info.AlphaMax)
        {
          curve.Tag[j] = Corner;
          curve.C[3 * j + 1] = curve.Vertex[j];
          curve.C[3 * j + 2] = p4;
        }
        else
        {
          if (alpha < 0.55)
            alpha = 0.55;
          else if (alpha > 1)
            alpha = 1;
          curve.Tag[j] = CurveTo;
          curve.C[3 * j + 0] = Interval(0.5 + 0.5 * alpha, curve.Vertex[i], curve.Vertex[j]);
          curve.C[3 * j + 1] = Interval(0.5 + 0.5 * alpha, curve.Vertex[k], curve.Vertex[j]);
          curve.C[3 * j + 2] = p4;
        }
        curve.Alpha[j] = alpha;
        curve.Beta[j] = 0.5;
      }
    }

    #endregion

    #region curve optimization

    static bool OptiPenalty(Path path, int i, int j, Opti res, double opttolerance, int[] convc, double[] areac)
    {
      PrivCurve curve = path.Curve;
      int m = curve.N;
      Vec2[] vertex = curve.Vertex;
      int k, k1, k2;

      if (i == j)
        return false;

      k = i;
      int i1 = Mod(i + 1, m);
      k1 = Mod(k + 1, m);
      int conv = convc[k1];
      if (conv == 0)
        return false;
      double d = DDist(vertex[i], vertex[i1]);
      for (k = k1; k != j; k = k1)
      {
        k1 = Mod(k + 1, m);
        k2 = Mod(k + 2, m);
        if (convc[k1] != conv)
          return false;
        if (Sign(Cprod(vertex[i], vertex[i1], vertex[k1], vertex[k2])) != conv)
          return false;
        if (Iprod1(vertex[i], vertex[i1], vertex[k1], vertex[k2]) < d * DDist(vertex[k1], vertex[k2]) * -0.999847695156)
          return false;
      }

      Vec2 p0 = curve.C[Mod(i, m) * 3 + 2];
      Vec2 p1 = vertex[Mod(i + 1, m)];
      Vec2 p2 = vertex[Mod(j, m)];
      Vec2 p3 = curve.C[Mod(j, m) * 3 + 2];

      double area = areac[j] - areac[i];
      area -= DPara(vertex[0], curve.C[i * 3 + 2], curve.C[j * 3 + 2]) / 2;
      if (i >= j)
        area += areac[m];

      double A1 = DPara(p0, p1, p2);
      double A2 = DPara(p0, p1, p3);
      double A3 = DPara(p0, p2, p3);
      double A4 = A1 + A3 - A2;

      if (A2 == A1)
        return false;

      double t = A3 / (A3 - A4);
      double s = A2 / (A2 - A1);
      double A = A2 * t / 2.0;

      if (A == 0.0)
        return false;

      double R = area / A;
      double alpha = 2 - Math.Sqrt(4 - R / 0.3);

      res.C0 = Interval(t * alpha, p0, p1);
      res.C1 = Interval(s * alpha, p3, p2);
      res.Alpha = alpha;
      res.T = t;
      res.S = s;

      p1 = res.C0;
      p2 = res.C1;

      res.Pen = 0;

      for (k = Mod(i + 1, m); k != j; k = k1)
      {
        k1 = Mod(k + 1, m);
        t = Tangent(p0, p1, p2, p3, vertex[k], vertex[k1]);
        if (t < -0.5)
          return false;
        Vec2 pt = Bezier(t, p0, p1, p2, p3);
        d = DDist(vertex[k], vertex[k1]);
        if (d == 0.0)
          return false;
        double d1 = DPara(vertex[k], vertex[k1], pt) / d;
        if (Math.Abs(d1) > opttolerance)
          return false;
        if (Iprod(vertex[k], vertex[k1], pt) < 0 || Iprod(vertex[k1], vertex[k], pt) < 0)
          return false;
        res.Pen += d1 * d1;
      }

      for (k = i; k != j; k = k1)
      {
        k1 = Mod(k + 1, m);
        t = Tangent(p0, p1, p2, p3, curve.C[k * 3 + 2], curve.C[k1 * 3 + 2]);
        if (t < -0.5)
          return false;
        Vec2 pt = Bezier(t, p0, p1, p2, p3);
        d = DDist(curve.C[k * 3 + 2], curve.C[k1 * 3 + 2]);
        if (d == 0.0)
          return false;
        double d1 = DPara(curve.C[k * 3 + 2], curve.C[k1 * 3 + 2], pt) / d;
        double d2 = DPara(curve.C[k * 3 + 2], curve.C[k1 * 3 + 2], vertex[k1]) / d;
        d2 *= 0.75 * curve.Alpha[k1];
        if (d2 < 0)
        {
          d1 = -d1;
          d2 = -d2;
        }
        if (d1 < d2 - opttolerance)
          return false;
        if (d1 < d2)
          res.Pen += (d1 - d2) * (d1 - d2);
      }

      return true;
    }

    void OptiCurve(Path path)
    {
      PrivCurve curve = path.Curve;
      int m = curve.N;
      Vec2[] vert = curve.Vertex;
      var pt = new int[m + 1];
      var pen = new double[m + 1];
      var len = new int[m + 1];
      var opt = new Opti[m + 1];
      var o = new Opti();
      var convc = new int[m];
      var areac = new double[m + 1];
      int i, j;

      for (i = 0; i < m; i++)
      {
        if (curve.Tag[i] == CurveTo)
          convc[i] = Sign(DPara(vert[Mod(i - 1, m)], vert[i], vert[Mod(i + 1, m)]));
        else
          convc[i] = 0;
      }

      double area = 0.0;
      areac[0] = 0.0;
      Vec2 p0 = curve.Vertex[0];
      for (i = 0; i < m; i++)
      {
        int i1 = Mod(i + 1, m);
        if (curve.Tag[i1] == CurveTo)
        {
          double alpha = curve.Alpha[i1];
          area += 0.3 * alpha * (4 - alpha) * DPara(curve.C[i * 3 + 2], vert[i1], curve.C[i1 * 3 + 2]) / 2;
          area += DPara(p0, curve.C[i * 3 + 2], curve.C[i1 * 3 + 2]) / 2;
        }
        areac[i + 1] = area;
      }

      pt[0] = -1;
      pen[0] = 0;
      len[0] = 0;

      for (j = 1; j <= m; j++)
      {
        pt[j] = j - 1;
        pen[j] = pen[j - 1];
        len[j] = len[j - 1] + 1;

        for (i = j - 2; i >= 0; i--)
        {
          if (!OptiPenalty(path, i, Mod(j, m), o, info.OptTolerance, convc, areac))
            break;
          if (len[j] > len[i] + 1 || (len[j] == len[i] + 1 && pen[j] > pen[i] + o.Pen))
          {
            pt[j] = i;
            pen[j] = pen[i] + o.Pen;
            len[j] = len[i] + 1;
            opt[j] = o;
            o = new Opti();
          }
        }
      }

      int om = len[m];
      var ocurve = new PrivCurve(om);
      var s = new double[om];
      var t = new double[om];

      j = m;
      for (i = om - 1; i >= 0; i--)
      {
        if (pt[j] == j - 1)
        {
          ocurve.Tag[i] = curve.Tag[Mod(j, m)];
          ocurve.C[i * 3 + 0] = curve.C[Mod(j, m) * 3 + 0];
          ocurve.C[i * 3 + 1] = curve.C[Mod(j, m) * 3 + 1];
          ocurve.C[i * 3 + 2] = curve.C[Mod(j, m) * 3 + 2];
          ocurve.Vertex[i] = curve.Vertex[Mod(j, m)];
          ocurve.Alpha[i] = curve.Alpha[Mod(j, m)];
          ocurve.Alpha0[i] = curve.Alpha0[Mod(j, m)];
          ocurve.Beta[i] = curve.Beta[Mod(j, m)];
          s[i] = t[i] = 1.0;
        }
        else
        {
          ocurve.Tag[i] = CurveTo;
          ocurve.C[i * 3 + 0] = opt[j].C0;
          ocurve.C[i * 3 + 1] = opt[j].C1;
          ocurve.C[i * 3 + 2] = curve.C[Mod(j, m) * 3 + 2];
          ocurve.Vertex[i] = Interval(opt[j].S, curve.C[Mod(j, m) * 3 + 2], vert[Mod(j, m)]);
          ocurve.Alpha[i] = opt[j].Alpha;
          ocurve.Alpha0[i] = opt[j].Alpha;
          s[i] = opt[j].S;
          t[i] = opt[j].T;
        }
        j = pt[j];
      }

      for (i = 0; i < om; i++)
      {
        int i1 = Mod(i + 1, om);
        ocurve.Beta[i] = s[i] / (s[i] + t[i1]);
      }
      path.Curve = ocurve;
    }

    #endregion

    List<TracePath> ToTracePaths()
    {
      var result = new List<TracePath>(pathlist.Count);
      foreach (Path path in pathlist)
      {
        PrivCurve curve = path.Curve;
        int n = curve.N;
        var tp = new TracePath { IsHole = path.Sign == '-', Area = path.Area };
        Vec2 a = curve.C[(n - 1) * 3 + 2];
        for (int j = 0; j < n; j++)
        {
          if (curve.Tag[j] == Corner)
          {
            // Fix 3: a corner is previous end -> vertex -> segment end.
            Vec2 vertex = curve.C[j * 3 + 1];
            Vec2 end = curve.C[j * 3 + 2];
            if (a.DistanceTo(vertex) > 1e-9)
              tp.Segments.Add(TraceSegment.Line(a, vertex));
            if (vertex.DistanceTo(end) > 1e-9)
              tp.Segments.Add(TraceSegment.Line(vertex, end));
          }
          else
          {
            tp.Segments.Add(TraceSegment.Bezier(a, curve.C[j * 3], curve.C[j * 3 + 1], curve.C[j * 3 + 2]));
          }
          a = curve.C[j * 3 + 2];
        }
        if (tp.Segments.Count > 0)
          result.Add(tp);
      }
      return result;
    }
  }
}

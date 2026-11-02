// RhinoOutline - image to outline curves for Rhino.
// Copyright (C) 2026 RhinoOutline contributors. Licensed under the GNU GPL v2, see LICENSE.

using System;
using System.Collections.Generic;

namespace RhinoOutline.Core
{
  /// <summary>
  /// Background subtraction that ignores cast shadows (Detect=Shadows). A pixel is background
  /// when its colour matches the background, and shadow when it is darker than the background
  /// by about the same factor in R, G and B (a shadow keeps the colour of the surface it falls
  /// on). Everything else is the shape. The background is tested in two ways: a smooth estimate
  /// interpolated in from the image border (follows gradients and vignetting), and each main
  /// colour of the border (a floor below a wall, a two-tone backdrop).
  /// See research/segmentation for the comparison this comes from.
  /// </summary>
  public static class ShadowMask
  {
    const double ShadowMin = 0.12;      // darker than 12 % of the background is not a shadow
    const int ModelSize = 96;           // background estimate is solved on a grid this size
    const int ModelIterations = 400;

    static readonly float[] ToLinear = BuildLinear();

    static float[] BuildLinear()
    {
      var lut = new float[256];
      for (int i = 0; i < 256; i++)
      {
        double x = i / 255.0;
        lut[i] = (float)(x <= 0.04045 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4));
      }
      return lut;
    }

    /// <param name="threshold">0 to 1; higher puts more pixels into the shape.</param>
    public static BinaryImage Build(RgbaImage image, double threshold)
    {
      int w = image.Width, h = image.Height, n = w * h;
      double colorTol = 0.44 * (1.0 - threshold);
      double chromaTol = 0.20 * (1.0 - threshold);
      const double eps = 2e-3;

      // log of the linear colour of every pixel (composited over white)
      var lr = new float[n];
      var lg = new float[n];
      var lb = new float[n];
      byte[] px = image.Pixels;
      for (int i = 0; i < n; i++)
      {
        int a = px[4 * i + 3], inv = 255 - a;
        int r = (px[4 * i] * a + 255 * inv + 127) / 255;
        int g = (px[4 * i + 1] * a + 255 * inv + 127) / 255;
        int b = (px[4 * i + 2] * a + 255 * inv + 127) / 255;
        lr[i] = (float)Math.Log(ToLinear[r] + eps);
        lg[i] = (float)Math.Log(ToLinear[g] + eps);
        lb[i] = (float)Math.Log(ToLinear[b] + eps);
      }

      var candidates = new List<Func<int, (double, double, double)>>();
      float[][] model = BackgroundModel(image, out int mw, out int mh);
      candidates.Add(i => SampleLog(model, mw, mh, w, h, i % w, i / w, eps));
      List<(double, double, double)> palette = BorderPalette(lr, lg, lb, w, h);
      foreach (var c in palette)
      {
        var cc = c;
        candidates.Add(i => cc);
      }

      bool Matches(int i, double cr, double cg, double cb)
      {
        double dr = lr[i] - cr, dg = lg[i] - cg, db = lb[i] - cb;
        double mean = (dr + dg + db) / 3.0;
        double chroma = Math.Max(Math.Abs(dr - mean), Math.Max(Math.Abs(dg - mean), Math.Abs(db - mean)));
        return Math.Abs(mean) < colorTol && chroma < chromaTol * 1.5;
      }

      // Pixels on the edge between two background colours (the line where a floor meets a wall)
      // are a blend of the two: next to both colours, they are compared with the nearest blend.
      var near = new byte[palette.Count][];
      for (int c = 0; c < palette.Count; c++)
      {
        var m = new byte[n];
        var (cr, cg, cb) = palette[c];
        for (int i = 0; i < n; i++)
          m[i] = (byte)(Matches(i, cr, cg, cb) ? 1 : 0);
        near[c] = Dilate(m, w, h, 3);
      }

      var shape = new byte[n];
      for (int i = 0; i < n; i++)
      {
        bool background = false, shadow = false;
        foreach (var cand in candidates)
        {
          var (cr, cg, cb) = cand(i);
          double dr = lr[i] - cr, dg = lg[i] - cg, db = lb[i] - cb;
          double mean = (dr + dg + db) / 3.0;
          double chroma = Math.Max(Math.Abs(dr - mean), Math.Max(Math.Abs(dg - mean), Math.Abs(db - mean)));
          double lum = Math.Exp(mean);
          if (Math.Abs(mean) < colorTol && chroma < chromaTol * 1.5)
            background = true;
          // skylight shadows are a little bluish: allow more chroma change as the shadow deepens
          if (lum < 1.0 && lum > ShadowMin && chroma < chromaTol + 0.25 * (1.0 - lum))
            shadow = true;
          if (background || shadow)
            break;
        }
        for (int a = 0; a < palette.Count && !background && !shadow; a++)
        {
          for (int b = a + 1; b < palette.Count && !background; b++)
          {
            if (near[a][i] == 0 || near[b][i] == 0)
              continue;
            // nearest point on the segment between the two colours, in linear RGB
            var (ar, ag, ab) = palette[a];
            var (br, bg, bb) = palette[b];
            double[] pa = { Math.Exp(ar) - eps, Math.Exp(ag) - eps, Math.Exp(ab) - eps };
            double[] pb = { Math.Exp(br) - eps, Math.Exp(bg) - eps, Math.Exp(bb) - eps };
            double[] p = { Math.Exp(lr[i]) - eps, Math.Exp(lg[i]) - eps, Math.Exp(lb[i]) - eps };
            double dot = 0, len = 0;
            for (int k = 0; k < 3; k++) { dot += (p[k] - pa[k]) * (pb[k] - pa[k]); len += (pb[k] - pa[k]) * (pb[k] - pa[k]); }
            double t = Math.Max(0, Math.Min(1, dot / Math.Max(len, 1e-9)));
            background = Matches(i, Math.Log(pa[0] + t * (pb[0] - pa[0]) + eps), Math.Log(pa[1] + t * (pb[1] - pa[1]) + eps),
                                 Math.Log(pa[2] + t * (pb[2] - pa[2]) + eps));
          }
        }
        shape[i] = (byte)(background || shadow ? 0 : 1);
      }

      shape = Majority(shape, w, h, 2);                 // 5 x 5 median of a binary image
      shape = Dilate(Erode(shape, w, h, 1), w, h, 1);    // open 3 x 3
      shape = Erode(Dilate(shape, w, h, 2), w, h, 2);    // close 5 x 5
      RemoveSmall(shape, w, h, (int)(0.0008 * n));

      var mask = new BinaryImage(w, h);
      Array.Copy(shape, mask.Data, n);
      return mask;
    }

    // ------------------------------------------------------------------ background estimate

    /// <summary>
    /// Smooth estimate of the empty background on a coarse grid (linear RGB): border cells whose
    /// colour is typical for the border are kept and harmonically interpolated inwards.
    /// </summary>
    static float[][] BackgroundModel(RgbaImage image, out int mw, out int mh)
    {
      int w = image.Width, h = image.Height;
      double s = (double)ModelSize / Math.Max(w, h);
      mw = Math.Max(8, (int)Math.Round(w * s));
      mh = Math.Max(8, (int)Math.Round(h * s));
      var grid = new float[3][];
      for (int c = 0; c < 3; c++)
        grid[c] = new float[mw * mh];

      // area average of the linear colour into the grid
      var count = new int[mw * mh];
      byte[] px = image.Pixels;
      for (int y = 0; y < h; y++)
      {
        int gy = Math.Min(mh - 1, y * mh / h);
        for (int x = 0; x < w; x++)
        {
          int gx = Math.Min(mw - 1, x * mw / w);
          int i = y * w + x, k = gy * mw + gx;
          int a = px[4 * i + 3], inv = 255 - a;
          grid[0][k] += ToLinear[(px[4 * i] * a + 255 * inv + 127) / 255];
          grid[1][k] += ToLinear[(px[4 * i + 1] * a + 255 * inv + 127) / 255];
          grid[2][k] += ToLinear[(px[4 * i + 2] * a + 255 * inv + 127) / 255];
          count[k]++;
        }
      }
      for (int k = 0; k < mw * mh; k++)
        for (int c = 0; c < 3; c++)
          grid[c][k] /= Math.Max(1, count[k]);

      // border cells, without those unlike the rest of the border (an object touching the edge)
      var known = new bool[mw * mh];
      var ring = new List<int>();
      for (int y = 0; y < mh; y++)
        for (int x = 0; x < mw; x++)
          if (x == 0 || y == 0 || x == mw - 1 || y == mh - 1)
            ring.Add(y * mw + x);
      var med = new double[3];
      for (int c = 0; c < 3; c++)
      {
        var v = new List<float>();
        foreach (int k in ring) v.Add(grid[c][k]);
        v.Sort();
        med[c] = v[v.Count / 2];
      }
      var dev = new double[ring.Count];
      for (int j = 0; j < ring.Count; j++)
      {
        double d = 0;
        for (int c = 0; c < 3; c++)
          d = Math.Max(d, Math.Abs(Math.Log((grid[c][ring[j]] + 1e-3) / (med[c] + 1e-3))));
        dev[j] = d;
      }
      var sorted = (double[])dev.Clone();
      Array.Sort(sorted);
      double limit = Math.Max(0.35, sorted[(int)(0.75 * (sorted.Length - 1))] * 2.5);
      for (int j = 0; j < ring.Count; j++)
        known[ring[j]] = dev[j] <= limit;

      var est = new float[3][];
      for (int c = 0; c < 3; c++)
      {
        est[c] = new float[mw * mh];
        for (int k = 0; k < mw * mh; k++)
          est[c][k] = known[k] ? grid[c][k] : (float)med[c];
      }
      var next = new float[mw * mh];
      for (int c = 0; c < 3; c++)
      {
        float[] e = est[c];
        for (int it = 0; it < ModelIterations; it++)
        {
          for (int y = 0; y < mh; y++)
          {
            for (int x = 0; x < mw; x++)
            {
              int k = y * mw + x;
              if (known[k]) { next[k] = grid[c][k]; continue; }
              // wraps at the edges like numpy.roll; edge cells are always known
              float up = e[((y + mh - 1) % mh) * mw + x], down = e[((y + 1) % mh) * mw + x];
              float left = e[y * mw + (x + mw - 1) % mw], right = e[y * mw + (x + 1) % mw];
              next[k] = 0.25f * (up + down + left + right);
            }
          }
          Array.Copy(next, e, next.Length);
        }
      }
      return est;
    }

    // log colour of the background estimate at image pixel (x, y), bilinear in the grid
    static (double, double, double) SampleLog(float[][] model, int mw, int mh, int w, int h, int x, int y, double eps)
    {
      double gx = (x + 0.5) * mw / w - 0.5, gy = (y + 0.5) * mh / h - 0.5;
      int x0 = Math.Max(0, Math.Min(mw - 1, (int)Math.Floor(gx))), y0 = Math.Max(0, Math.Min(mh - 1, (int)Math.Floor(gy)));
      int x1 = Math.Min(mw - 1, x0 + 1), y1 = Math.Min(mh - 1, y0 + 1);
      double fx = Math.Max(0, Math.Min(1, gx - x0)), fy = Math.Max(0, Math.Min(1, gy - y0));
      double Bi(float[] g) =>
        (g[y0 * mw + x0] * (1 - fx) + g[y0 * mw + x1] * fx) * (1 - fy) + (g[y1 * mw + x0] * (1 - fx) + g[y1 * mw + x1] * fx) * fy;
      return (Math.Log(Bi(model[0]) + eps), Math.Log(Bi(model[1]) + eps), Math.Log(Bi(model[2]) + eps));
    }

    /// <summary>Main colours (log linear RGB) of a 3 pixel wide image border: k-means, k = 4,
    /// clusters holding at least 8 % of the border.</summary>
    static List<(double, double, double)> BorderPalette(float[] lr, float[] lg, float[] lb, int w, int h)
    {
      var idx = new List<int>();
      int band = Math.Min(3, Math.Min(w, h) / 2);
      for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
          if (x < band || y < band || x >= w - band || y >= h - band)
            idx.Add(y * w + x);
      int step = Math.Max(1, idx.Count / 6000);
      var pts = new List<double[]>();
      for (int j = 0; j < idx.Count; j += step)
        pts.Add(new double[] { lr[idx[j]], lg[idx[j]], lb[idx[j]] });

      const int k = 4;
      int m = pts.Count;
      var centres = new double[k][];
      // farthest point initialisation from the first point: deterministic
      centres[0] = (double[])pts[0].Clone();
      var dmin = new double[m];
      for (int j = 0; j < m; j++) dmin[j] = Dist2(pts[j], centres[0]);
      for (int c = 1; c < k; c++)
      {
        int far = 0;
        for (int j = 1; j < m; j++) if (dmin[j] > dmin[far]) far = j;
        centres[c] = (double[])pts[far].Clone();
        for (int j = 0; j < m; j++) dmin[j] = Math.Min(dmin[j], Dist2(pts[j], centres[c]));
      }
      var label = new int[m];
      for (int it = 0; it < 30; it++)
      {
        for (int j = 0; j < m; j++)
        {
          int best = 0;
          for (int c = 1; c < k; c++) if (Dist2(pts[j], centres[c]) < Dist2(pts[j], centres[best])) best = c;
          label[j] = best;
        }
        for (int c = 0; c < k; c++)
        {
          var sum = new double[3];
          int cnt = 0;
          for (int j = 0; j < m; j++)
            if (label[j] == c) { sum[0] += pts[j][0]; sum[1] += pts[j][1]; sum[2] += pts[j][2]; cnt++; }
          if (cnt > 0) centres[c] = new[] { sum[0] / cnt, sum[1] / cnt, sum[2] / cnt };
        }
      }
      var result = new List<(double, double, double)>();
      for (int c = 0; c < k; c++)
      {
        int cnt = 0;
        for (int j = 0; j < m; j++) if (label[j] == c) cnt++;
        if (cnt >= 0.08 * m)
          result.Add((centres[c][0], centres[c][1], centres[c][2]));
      }
      return result;
    }

    static double Dist2(double[] a, double[] b)
    {
      double dx = a[0] - b[0], dy = a[1] - b[1], dz = a[2] - b[2];
      return dx * dx + dy * dy + dz * dz;
    }

    // ------------------------------------------------------------------ binary clean up

    // 1 where more than half of the (2r+1)^2 window is 1 (a median filter on a binary image)
    static byte[] Majority(byte[] src, int w, int h, int r)
    {
      var integral = new int[(w + 1) * (h + 1)];
      for (int y = 0; y < h; y++)
      {
        int row = 0;
        for (int x = 0; x < w; x++)
        {
          row += src[y * w + x];
          integral[(y + 1) * (w + 1) + x + 1] = integral[y * (w + 1) + x + 1] + row;
        }
      }
      var dst = new byte[w * h];
      for (int y = 0; y < h; y++)
      {
        int y0 = Math.Max(0, y - r), y1 = Math.Min(h, y + r + 1);
        for (int x = 0; x < w; x++)
        {
          int x0 = Math.Max(0, x - r), x1 = Math.Min(w, x + r + 1);
          int sum = integral[y1 * (w + 1) + x1] - integral[y0 * (w + 1) + x1] - integral[y1 * (w + 1) + x0] + integral[y0 * (w + 1) + x0];
          dst[y * w + x] = (byte)(2 * sum > (x1 - x0) * (y1 - y0) ? 1 : 0);
        }
      }
      return dst;
    }

    static byte[] Erode(byte[] src, int w, int h, int r) => Morph(src, w, h, r, true);

    static byte[] Dilate(byte[] src, int w, int h, int r) => Morph(src, w, h, r, false);

    // square structuring element, separable; pixels outside the image do not count
    static byte[] Morph(byte[] src, int w, int h, int r, bool erode)
    {
      var tmp = new byte[w * h];
      var dst = new byte[w * h];
      for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
          byte v = erode ? (byte)1 : (byte)0;
          for (int d = -r; d <= r; d++)
          {
            int xx = x + d;
            if (xx < 0 || xx >= w) continue;
            byte s = src[y * w + xx];
            if (erode ? s == 0 : s != 0) { v = s; break; }
          }
          tmp[y * w + x] = v;
        }
      for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
          byte v = erode ? (byte)1 : (byte)0;
          for (int d = -r; d <= r; d++)
          {
            int yy = y + d;
            if (yy < 0 || yy >= h) continue;
            byte s = tmp[yy * w + x];
            if (erode ? s == 0 : s != 0) { v = s; break; }
          }
          dst[y * w + x] = v;
        }
      return dst;
    }

    // clears 8-connected pieces of the shape smaller than minArea pixels
    static void RemoveSmall(byte[] m, int w, int h, int minArea)
    {
      var seen = new bool[w * h];
      var stack = new Stack<int>();
      var piece = new List<int>();
      for (int s = 0; s < w * h; s++)
      {
        if (m[s] == 0 || seen[s]) continue;
        piece.Clear();
        stack.Push(s);
        seen[s] = true;
        while (stack.Count > 0)
        {
          int p = stack.Pop();
          piece.Add(p);
          int px = p % w, py = p / w;
          for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
              int x = px + dx, y = py + dy;
              if (x < 0 || y < 0 || x >= w || y >= h) continue;
              int q = y * w + x;
              if (m[q] != 0 && !seen[q]) { seen[q] = true; stack.Push(q); }
            }
        }
        if (piece.Count < minArea)
          foreach (int p in piece) m[p] = 0;
      }
    }
  }
}

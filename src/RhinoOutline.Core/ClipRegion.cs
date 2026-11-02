// RhinoOutline - image to outline curves for Rhino.
// Copyright (C) 2026 RhinoOutline contributors. Licensed under the GNU GPL v2, see LICENSE.

using System;
using System.Collections.Generic;

namespace RhinoOutline.Core
{
  /// <summary>
  /// The visible part of a cropped picture in image space (pixels, y down): closed polygons
  /// filled with the even-odd rule, so a polygon inside another one is a hole.
  /// </summary>
  public sealed class ClipRegion
  {
    public readonly List<Vec2[]> Polygons = new List<Vec2[]>();

    /// <summary>
    /// Keeps only the shape pixels inside the region. The region is grown by one pixel so a
    /// shape cut by the crop edge reaches just past it; the caller trims the curves back to the
    /// exact edge.
    /// </summary>
    public void Apply(BinaryImage mask)
    {
      byte[] inside = Grow(Rasterize(mask.Width, mask.Height), mask.Width, mask.Height);
      byte[] data = mask.Data;
      for (int i = 0; i < data.Length; i++)
        data[i] &= inside[i];
    }

    // 1 for the pixels whose centre lies in the region.
    byte[] Rasterize(int width, int height)
    {
      var inside = new byte[width * height];
      var xs = new List<double>();
      for (int y = 0; y < height; y++)
      {
        double cy = y + 0.5;
        xs.Clear();
        foreach (Vec2[] poly in Polygons)
        {
          for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
          {
            Vec2 a = poly[j], b = poly[i];
            if ((a.Y > cy) != (b.Y > cy))
              xs.Add(a.X + (cy - a.Y) / (b.Y - a.Y) * (b.X - a.X));
          }
        }
        xs.Sort();
        for (int k = 0; k + 1 < xs.Count; k += 2)
        {
          int x0 = Math.Max(0, (int)Math.Ceiling(xs[k] - 0.5));
          int x1 = Math.Min(width - 1, (int)Math.Floor(xs[k + 1] - 0.5));
          for (int x = x0; x <= x1; x++)
            inside[y * width + x] = 1;
        }
      }
      return inside;
    }

    // Adds every pixel that touches the region, corners included.
    static byte[] Grow(byte[] src, int width, int height)
    {
      var dst = new byte[src.Length];
      for (int y = 0; y < height; y++)
      {
        for (int x = 0; x < width; x++)
        {
          if (src[y * width + x] == 0)
            continue;
          for (int dy = -1; dy <= 1; dy++)
          {
            int yy = y + dy;
            if ((uint)yy >= (uint)height)
              continue;
            for (int dx = -1; dx <= 1; dx++)
            {
              int xx = x + dx;
              if ((uint)xx < (uint)width)
                dst[yy * width + xx] = 1;
            }
          }
        }
      }
      return dst;
    }
  }
}

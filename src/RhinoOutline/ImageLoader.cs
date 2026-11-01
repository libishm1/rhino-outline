// RhinoOutline - image to outline curves for Rhino.
// Copyright (C) 2026 RhinoOutline contributors. Licensed under the GNU GPL v2, see LICENSE.

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using RhinoOutline.Core;

namespace RhinoOutline
{
  static class ImageLoader
  {
    /// <summary>Reads PNG, JPG, BMP, GIF or TIFF into straight RGBA pixels.</summary>
    /// <param name="dpi">Horizontal resolution stored in the file, 96 when it has none.</param>
    public static RgbaImage Load(string path, out double dpi)
    {
      if (Rhino.Runtime.HostUtils.RunningOnWindows)
        return LoadGdi(path, out dpi);
      return LoadEto(path, out dpi);
    }

    static RgbaImage LoadGdi(string path, out double dpi)
    {
      using (var bmp = new Bitmap(path))
      {
        int w = bmp.Width, h = bmp.Height;
        dpi = bmp.HorizontalResolution > 1 ? bmp.HorizontalResolution : 96;
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
          var px = new byte[4L * w * h];
          var row = new byte[4 * w];
          for (int y = 0; y < h; y++)
          {
            Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, 4 * w);
            for (int x = 0, i = 4 * y * w; x < w; x++, i += 4)
            {
              // GDI+ rows are BGRA.
              px[i] = row[4 * x + 2];
              px[i + 1] = row[4 * x + 1];
              px[i + 2] = row[4 * x];
              px[i + 3] = row[4 * x + 3];
            }
          }
          return new RgbaImage(w, h, px);
        }
        finally
        {
          bmp.UnlockBits(data);
        }
      }
    }

    // macOS: System.Drawing is not available on .NET 8 there, Eto is.
    static RgbaImage LoadEto(string path, out double dpi)
    {
      dpi = 96;
      using (var bmp = new Eto.Drawing.Bitmap(path))
      using (var data = bmp.Lock())
      {
        int w = bmp.Width, h = bmp.Height;
        var px = new byte[4L * w * h];
        for (int y = 0; y < h; y++)
        {
          for (int x = 0; x < w; x++)
          {
            Eto.Drawing.Color c = data.GetPixel(x, y);
            int i = 4 * (y * w + x);
            px[i] = (byte)c.Rb;
            px[i + 1] = (byte)c.Gb;
            px[i + 2] = (byte)c.Bb;
            px[i + 3] = (byte)c.Ab;
          }
        }
        return new RgbaImage(w, h, px);
      }
    }
  }
}

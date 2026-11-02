// RhinoOutline - image to outline curves for Rhino.
// Copyright (C) 2026 RhinoOutline contributors. Licensed under the GNU GPL v2, see LICENSE.

using System;

namespace RhinoOutline.Core
{
  /// <summary>How the shape is told apart from the background.</summary>
  public enum ShapeDetect
  {
    /// <summary>Transparency if the image has any, else a uniform border colour, else dark pixels.</summary>
    Auto,
    /// <summary>Opaque pixels are the shape.</summary>
    Transparency,
    /// <summary>Pixels that differ from the border colour are the shape.</summary>
    Background,
    /// <summary>Dark pixels are the shape (classic black on white tracing).</summary>
    Dark,
    /// <summary>Pixels unlike the background are the shape, and cast shadows are not (photos of objects).</summary>
    Shadows,
    /// <summary>A segmentation network decides (the plug-in supplies its probability per pixel).</summary>
    AI,
  }

  /// <summary>Straight (not premultiplied) RGBA pixels, four bytes per pixel, top row first.</summary>
  public sealed class RgbaImage
  {
    public readonly int Width;
    public readonly int Height;
    public readonly byte[] Pixels;

    public RgbaImage(int width, int height, byte[] pixels)
    {
      if (width <= 0 || height <= 0)
        throw new ArgumentOutOfRangeException(nameof(width), "The image is empty.");
      if (pixels == null || pixels.Length < 4L * width * height)
        throw new ArgumentException("The pixel buffer is too small.", nameof(pixels));
      Width = width;
      Height = height;
      Pixels = pixels;
    }
  }

  /// <summary>One byte per pixel, 1 = shape and 0 = background, top row first.</summary>
  public sealed class BinaryImage
  {
    public readonly int Width;
    public readonly int Height;
    public readonly byte[] Data;

    public BinaryImage(int width, int height) : this(width, height, new byte[width * height]) { }

    public BinaryImage(int width, int height, byte[] data)
    {
      Width = width;
      Height = height;
      Data = data;
    }

    public bool At(int x, int y) => (uint)x < (uint)Width && (uint)y < (uint)Height && Data[y * Width + x] != 0;

    public int CountShapePixels()
    {
      int count = 0;
      foreach (byte b in Data)
        count += b;
      return count;
    }
  }

  public sealed class MaskResult
  {
    public BinaryImage Mask;

    /// <summary>The mode that was applied; never <see cref="ShapeDetect.Auto"/>.</summary>
    public ShapeDetect Mode;

    public byte BackgroundR = 255;
    public byte BackgroundG = 255;
    public byte BackgroundB = 255;

    public string Description;
  }

  public static class ShapeMask
  {
    // Border pixels within this distance of the median border colour count as background.
    const int BorderTolerance = 25;

    /// <summary>Splits an image into shape and background pixels.</summary>
    /// <param name="threshold">0 to 1. In every mode a higher value puts more pixels into the shape.</param>
    /// <param name="probability">For <see cref="ShapeDetect.AI"/>: object probability per pixel, 0 to 255.</param>
    public static MaskResult Build(RgbaImage image, ShapeDetect mode, double threshold, bool invert, byte[] probability = null)
    {
      var result = new MaskResult();
      if (mode == ShapeDetect.AI && (probability == null || probability.Length != image.Width * image.Height))
        mode = ShapeDetect.Auto;
      byte br = 255, bg = 255, bb = 255;
      if (mode == ShapeDetect.Auto)
      {
        if (HasTransparency(image))
          mode = ShapeDetect.Transparency;
        else if (BorderColor(image, out br, out bg, out bb))
          mode = ShapeDetect.Background;
        else
          mode = ShapeDetect.Dark;
      }
      else if (mode == ShapeDetect.Background)
      {
        BorderColor(image, out br, out bg, out bb);
      }

      if (double.IsNaN(threshold)) threshold = 0.5;
      threshold = Math.Max(0.0, Math.Min(1.0, threshold));

      var mask = new BinaryImage(image.Width, image.Height);
      byte[] px = image.Pixels;
      byte[] data = mask.Data;
      int count = image.Width * image.Height;

      switch (mode)
      {
        case ShapeDetect.AI:
        {
          double cut = (1.0 - threshold) * 255.0;
          for (int i = 0; i < count; i++)
            data[i] = (byte)(probability[i] > cut ? 1 : 0);
          result.Description = "the segmentation network decides the shape";
          break;
        }
        case ShapeDetect.Shadows:
        {
          Array.Copy(ShadowMask.Build(image, threshold).Data, data, count);
          result.Description = "pixels unlike the background are the shape, cast shadows are not";
          break;
        }
        case ShapeDetect.Transparency:
        {
          double cut = (1.0 - threshold) * 255.0;
          for (int i = 0; i < count; i++)
            data[i] = (byte)(px[4 * i + 3] > cut ? 1 : 0);
          result.Description = "opaque pixels are the shape";
          break;
        }
        case ShapeDetect.Dark:
        {
          double cut = threshold * 255.0;
          for (int i = 0; i < count; i++)
          {
            OverWhite(px, i, out int r, out int g, out int b);
            data[i] = (byte)(0.299 * r + 0.587 * g + 0.114 * b < cut ? 1 : 0);
          }
          result.Description = "dark pixels are the shape";
          break;
        }
        default:
        {
          double cut = (1.0 - threshold) * 0.2 * 255.0;
          for (int i = 0; i < count; i++)
          {
            OverWhite(px, i, out int r, out int g, out int b);
            int diff = Math.Max(Math.Abs(r - br), Math.Max(Math.Abs(g - bg), Math.Abs(b - bb)));
            data[i] = (byte)(diff > cut ? 1 : 0);
          }
          result.Description = $"pixels unlike the background colour RGB({br},{bg},{bb}) are the shape";
          break;
        }
      }

      if (invert)
      {
        for (int i = 0; i < count; i++)
          data[i] ^= 1;
        result.Description += " (inverted)";
      }

      result.Mask = mask;
      result.Mode = mode;
      result.BackgroundR = br;
      result.BackgroundG = bg;
      result.BackgroundB = bb;
      return result;
    }

    /// <summary>True when at least 0.1% of the pixels are more than half transparent.</summary>
    public static bool HasTransparency(RgbaImage image)
    {
      byte[] px = image.Pixels;
      long count = (long)image.Width * image.Height;
      long clear = 0;
      for (long i = 0; i < count; i++)
      {
        if (px[4 * i + 3] < 128)
          clear++;
      }
      return clear > 0 && clear * 1000 >= count;
    }

    /// <summary>
    /// Median colour of the image border. Returns true when at least 90% of the border is
    /// close to that colour, which means the picture sits on a plain background.
    /// </summary>
    public static bool BorderColor(RgbaImage image, out byte r, out byte g, out byte b)
    {
      int w = image.Width, h = image.Height;
      var hr = new int[256];
      var hg = new int[256];
      var hb = new int[256];
      int total = 0;
      foreach (int i in BorderPixels(w, h))
      {
        OverWhite(image.Pixels, i, out int pr, out int pg, out int pb);
        hr[pr]++;
        hg[pg]++;
        hb[pb]++;
        total++;
      }
      r = Median(hr, total);
      g = Median(hg, total);
      b = Median(hb, total);

      int close = 0;
      foreach (int i in BorderPixels(w, h))
      {
        OverWhite(image.Pixels, i, out int pr, out int pg, out int pb);
        if (Math.Abs(pr - r) <= BorderTolerance && Math.Abs(pg - g) <= BorderTolerance && Math.Abs(pb - b) <= BorderTolerance)
          close++;
      }
      return close * 10 >= total * 9;
    }

    static System.Collections.Generic.IEnumerable<int> BorderPixels(int w, int h)
    {
      for (int x = 0; x < w; x++)
      {
        yield return x;
        if (h > 1) yield return (h - 1) * w + x;
      }
      for (int y = 1; y < h - 1; y++)
      {
        yield return y * w;
        if (w > 1) yield return y * w + w - 1;
      }
    }

    static byte Median(int[] histogram, int total)
    {
      int seen = 0;
      for (int v = 0; v < 256; v++)
      {
        seen += histogram[v];
        if (2 * seen >= total)
          return (byte)v;
      }
      return 255;
    }

    // Composites pixel i over white so half transparent pixels get a sensible colour.
    static void OverWhite(byte[] px, int i, out int r, out int g, out int b)
    {
      int a = px[4 * i + 3];
      if (a == 255)
      {
        r = px[4 * i];
        g = px[4 * i + 1];
        b = px[4 * i + 2];
        return;
      }
      int inv = 255 - a;
      r = (px[4 * i] * a + 255 * inv + 127) / 255;
      g = (px[4 * i + 1] * a + 255 * inv + 127) / 255;
      b = (px[4 * i + 2] * a + 255 * inv + 127) / 255;
    }
  }
}

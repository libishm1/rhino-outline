// RhinoOutline - image to outline curves for Rhino.
// Copyright (C) 2026 RhinoOutline contributors. Licensed under the GNU GPL v2, see LICENSE.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using RhinoOutline.Core;

namespace RhinoOutline.Segmentation
{
  /// <summary>
  /// Runs the segmentation networks with ONNX Runtime on the CPU. The pre and post processing
  /// follows research/segmentation/methods.py, where the networks were compared.
  /// </summary>
  static class NeuralSegmenter
  {
    static readonly Dictionary<string, InferenceSession> sessions = new Dictionary<string, InferenceSession>();

    static InferenceSession Session(string path)
    {
      if (!sessions.TryGetValue(path, out InferenceSession s))
      {
        NeuralModels.LoadNativeRuntime();
        var options = new SessionOptions { LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR };
        s = new InferenceSession(path, options);
        sessions[path] = s;
      }
      return s;
    }

    static readonly float[] ImageNetMean = { 0.485f, 0.456f, 0.406f };
    static readonly float[] ImageNetStd = { 0.229f, 0.224f, 0.225f };

    /// <summary>
    /// Object probability per pixel (0 to 255) from a salient object network, or null when the
    /// model is not available (the user declined the download).
    /// </summary>
    public static byte[] Probability(RgbaImage image, AIModel model)
    {
      ModelFile file = NeuralModels.For(model);
      string path = NeuralModels.Ensure(file);
      if (path == null)
        return null;

      int size = model == AIModel.Fast ? 320 : 1024;
      float[] mean = model == AIModel.Accurate ? new[] { 0.5f, 0.5f, 0.5f } : ImageNetMean;
      float[] std = model == AIModel.Accurate ? new[] { 1f, 1f, 1f } : ImageNetStd;
      float[] rgb = Resize(image, size, size);   // 0 to 255, interleaved
      float scale = 1f / 255f;
      if (model == AIModel.Fast)
      {
        // U^2-Net divides by the brightest value of the image instead of 255
        float max = 1e-6f;
        foreach (float v in rgb) max = Math.Max(max, v);
        scale = 1f / max;
      }
      var input = new float[3 * size * size];
      for (int i = 0; i < size * size; i++)
        for (int c = 0; c < 3; c++)
          input[c * size * size + i] = (rgb[3 * i + c] * scale - mean[c]) / std[c];

      InferenceSession s = Session(path);
      float[] output;
      var tensor = new DenseTensor<float>(input, new[] { 1, 3, size, size });
      using (var results = s.Run(new[] { NamedOnnxValue.CreateFromTensor(s.InputMetadata.Keys.First(), tensor) }))
        output = results.First().AsEnumerable<float>().Take(size * size).ToArray();

      if (model == AIModel.Best)
        for (int i = 0; i < output.Length; i++)
          output[i] = 1f / (1f + (float)Math.Exp(-output[i]));
      float lo = output.Min(), hi = output.Max();
      float range = Math.Max(hi - lo, 1e-6f);
      for (int i = 0; i < output.Length; i++)
        output[i] = (output[i] - lo) / range;
      return ToBytes(ResizeChannel(output, size, size, image.Width, image.Height));
    }

    // ------------------------------------------------------------------ MobileSAM

    static RgbaImage samImage;
    static DenseTensor<float> samEmbedding;

    /// <summary>
    /// MobileSAM: candidate masks (0 or 255 per pixel) for the object under the given points
    /// (image pixels, y down), smallest first, and in <paramref name="pick"/> the one to show
    /// first. Null when the model is not available. The image embedding is kept, so more clicks
    /// on the same image are fast.
    /// </summary>
    public static List<byte[]> ObjectAt(RgbaImage image, IList<(double X, double Y)> points, out int pick)
    {
      pick = 0;
      string encoderPath = NeuralModels.Ensure(NeuralModels.MobileSamEncoder, NeuralModels.MobileSamDecoder);
      if (encoderPath == null)
        return null;
      string decoderPath = NeuralModels.PathOf(NeuralModels.MobileSamDecoder);

      int w = image.Width, h = image.Height;
      double s = 1024.0 / Math.Max(w, h);
      if (!ReferenceEquals(samImage, image))
      {
        int rw = (int)Math.Round(w * s), rh = (int)Math.Round(h * s);
        float[] rgb = Resize(image, rw, rh);    // the encoder takes H x W x 3 in 0 to 255
        InferenceSession enc = Session(encoderPath);
        using (var results = enc.Run(new[] { NamedOnnxValue.CreateFromTensor("input_image", new DenseTensor<float>(rgb, new[] { rh, rw, 3 })) }))
        {
          var e = results.First().AsTensor<float>();
          samEmbedding = new DenseTensor<float>(e.ToArray(), e.Dimensions.ToArray());
        }
        samImage = image;
      }

      int n = points.Count;
      var coords = new float[2 * (n + 1)];
      var labels = new float[n + 1];
      for (int i = 0; i < n; i++)
      {
        coords[2 * i] = (float)(points[i].X * s);
        coords[2 * i + 1] = (float)(points[i].Y * s);
        labels[i] = 1;
      }
      labels[n] = -1;    // padding point: there is no box

      InferenceSession dec = Session(decoderPath);
      var feeds = new List<NamedOnnxValue>
      {
        NamedOnnxValue.CreateFromTensor("image_embeddings", samEmbedding),
        NamedOnnxValue.CreateFromTensor("point_coords", new DenseTensor<float>(coords, new[] { 1, n + 1, 2 })),
        NamedOnnxValue.CreateFromTensor("point_labels", new DenseTensor<float>(labels, new[] { 1, n + 1 })),
        NamedOnnxValue.CreateFromTensor("mask_input", new DenseTensor<float>(new float[256 * 256], new[] { 1, 1, 256, 256 })),
        NamedOnnxValue.CreateFromTensor("has_mask_input", new DenseTensor<float>(new float[1], new[] { 1 })),
        NamedOnnxValue.CreateFromTensor("orig_im_size", new DenseTensor<float>(new float[] { h, w }, new[] { 2 })),
      };
      using (var results = dec.Run(feeds))
      {
        var masks = (DenseTensor<float>)results.First(r => r.Name == "masks").AsTensor<float>();
        var iou = results.First(r => r.Name == "iou_predictions").AsTensor<float>();
        int count = masks.Dimensions[1];
        Span<float> m = masks.Buffer.Span;
        int plane = w * h;
        // One click: the largest candidate that does not run into the image border more than the
        // tightest one does. The candidates are parts of the object, the object, and often the
        // object with the floor or wall around it, which touches much more of the border.
        // More clicks: the candidate the network rates best.
        int best = 0;
        if (n == 1)
        {
          var area = new long[count];
          var border = new double[count];
          for (int k = 0; k < count; k++)
          {
            for (int i = 0; i < plane; i++)
              if (m[k * plane + i] > 0) area[k]++;
            long touching = 0;
            for (int x = 0; x < w; x++)
            {
              if (m[k * plane + x] > 0) touching++;
              if (m[k * plane + (h - 1) * w + x] > 0) touching++;
            }
            for (int y = 0; y < h; y++)
            {
              if (m[k * plane + y * w] > 0) touching++;
              if (m[k * plane + y * w + w - 1] > 0) touching++;
            }
            border[k] = touching / (2.0 * (w + h));
          }
          double tightest = border.Min();
          best = -1;
          for (int k = 0; k < count; k++)
            if (border[k] <= tightest + 0.05 && (best < 0 || area[k] > area[best]))
              best = k;
        }
        else
        {
          for (int k = 1; k < count; k++)
            if (iou[0, k] > iou[0, best]) best = k;
        }
        // all candidates, smallest first; the user can step to a larger or smaller one
        var areas = new long[count];
        for (int k = 0; k < count; k++)
          areas[k] = Area(m, k, plane);
        var order = Enumerable.Range(0, count).OrderBy(k => areas[k]).ToList();
        var list = new List<byte[]>();
        foreach (int k in order)
        {
          var prob = new byte[plane];
          for (int i = 0; i < plane; i++)
            prob[i] = (byte)(m[k * plane + i] > 0 ? 255 : 0);
          list.Add(prob);
        }
        pick = order.IndexOf(best);
        return list;
      }
    }

    static long Area(Span<float> m, int k, int plane)
    {
      long a = 0;
      for (int i = 0; i < plane; i++)
        if (m[k * plane + i] > 0) a++;
      return a;
    }

    // ------------------------------------------------------------------ resampling

    // Image composited over white and resampled to tw x th (box filter when shrinking), RGB 0 to 255.
    static float[] Resize(RgbaImage image, int tw, int th)
    {
      int w = image.Width, h = image.Height;
      byte[] px = image.Pixels;
      var dst = new float[3 * tw * th];
      double sx = (double)w / tw, sy = (double)h / th;
      for (int y = 0; y < th; y++)
      {
        int y0 = (int)Math.Floor(y * sy), y1 = Math.Max(y0 + 1, Math.Min(h, (int)Math.Ceiling((y + 1) * sy)));
        for (int x = 0; x < tw; x++)
        {
          int x0 = (int)Math.Floor(x * sx), x1 = Math.Max(x0 + 1, Math.Min(w, (int)Math.Ceiling((x + 1) * sx)));
          if (sx < 1 || sy < 1)
          {
            // enlarging: nearest source pixel
            x0 = Math.Min(w - 1, (int)((x + 0.5) * sx)); x1 = x0 + 1;
            y0 = Math.Min(h - 1, (int)((y + 0.5) * sy)); y1 = y0 + 1;
          }
          double r = 0, g = 0, b = 0;
          int cnt = 0;
          for (int yy = y0; yy < y1; yy++)
            for (int xx = x0; xx < x1; xx++)
            {
              int i = 4 * (yy * w + xx);
              int a = px[i + 3], inv = 255 - a;
              r += (px[i] * a + 255 * inv) / 255.0;
              g += (px[i + 1] * a + 255 * inv) / 255.0;
              b += (px[i + 2] * a + 255 * inv) / 255.0;
              cnt++;
            }
          int o = 3 * (y * tw + x);
          dst[o] = (float)(r / cnt);
          dst[o + 1] = (float)(g / cnt);
          dst[o + 2] = (float)(b / cnt);
        }
      }
      return dst;
    }

    // Bilinear resampling of one channel.
    static float[] ResizeChannel(float[] src, int sw, int sh, int tw, int th)
    {
      var dst = new float[tw * th];
      for (int y = 0; y < th; y++)
      {
        double fy = Math.Max(0, Math.Min(sh - 1, (y + 0.5) * sh / th - 0.5));
        int y0 = (int)fy, y1 = Math.Min(sh - 1, y0 + 1);
        double ty = fy - y0;
        for (int x = 0; x < tw; x++)
        {
          double fx = Math.Max(0, Math.Min(sw - 1, (x + 0.5) * sw / tw - 0.5));
          int x0 = (int)fx, x1 = Math.Min(sw - 1, x0 + 1);
          double tx = fx - x0;
          dst[y * tw + x] = (float)((src[y0 * sw + x0] * (1 - tx) + src[y0 * sw + x1] * tx) * (1 - ty)
                                  + (src[y1 * sw + x0] * (1 - tx) + src[y1 * sw + x1] * tx) * ty);
        }
      }
      return dst;
    }

    static byte[] ToBytes(float[] p)
    {
      var b = new byte[p.Length];
      for (int i = 0; i < p.Length; i++)
        b[i] = (byte)Math.Max(0, Math.Min(255, Math.Round(p[i] * 255)));
      return b;
    }
  }
}

// RhinoOutline - image to outline curves for Rhino.
// Copyright (C) 2026 RhinoOutline contributors. Licensed under the GNU GPL v2, see LICENSE.

using System.Collections.Generic;
using System.Diagnostics;

namespace RhinoOutline.Core
{
  public sealed class OutlineSettings
  {
    public ShapeDetect Detect = ShapeDetect.Auto;

    /// <summary>0 to 1; higher puts more pixels into the shape.</summary>
    public double Threshold = 0.5;

    public bool Invert;

    /// <summary>Speckles of this many pixels or fewer are ignored.</summary>
    public int FilterSize = 2;

    /// <summary>Potrace alphamax: 0 keeps sharp corners, 1.334 rounds everything.</summary>
    public double CornerRounding = 1.0;

    public bool OptimizeCurves = true;

    public double OptimizeTolerance = 0.2;

    public TurnPolicy TurnPolicy = TurnPolicy.Minority;

    /// <summary>Alpha shape radius in pixels; 0 picks the smallest value giving one outline.</summary>
    public double AlphaPixels;

    public bool KeepHoles;

    public OutlineSettings Clone() => (OutlineSettings)MemberwiseClone();
  }

  /// <summary>Runs image to mask to trace to alpha shape, redoing only what a change affects.</summary>
  public sealed class OutlineEngine
  {
    readonly RgbaImage image;
    OutlineSettings last;

    public OutlineEngine(RgbaImage image)
    {
      this.image = image;
    }

    public RgbaImage Image => image;
    public MaskResult Mask { get; private set; }
    public TraceResult Trace { get; private set; }
    public TriangulatedTrace Triangulation { get; private set; }

    /// <summary>The automatic alpha in pixels (computed whenever the trace changes).</summary>
    public double AutoAlpha { get; private set; }

    /// <summary>The alpha used for <see cref="Loops"/>, in pixels.</summary>
    public double AlphaUsed { get; private set; }

    public List<OutlineLoop> Loops { get; private set; } = new List<OutlineLoop>();

    public long TraceMilliseconds { get; private set; }
    public long OutlineMilliseconds { get; private set; }

    public void Update(OutlineSettings s)
    {
      bool maskDirty = last == null || Mask == null
        || s.Detect != last.Detect || s.Threshold != last.Threshold || s.Invert != last.Invert;
      bool traceDirty = maskDirty || Trace == null
        || s.FilterSize != last.FilterSize || s.CornerRounding != last.CornerRounding
        || s.OptimizeCurves != last.OptimizeCurves || s.OptimizeTolerance != last.OptimizeTolerance
        || s.TurnPolicy != last.TurnPolicy;

      var watch = Stopwatch.StartNew();
      if (maskDirty)
        Mask = ShapeMask.Build(image, s.Detect, s.Threshold, s.Invert);
      if (traceDirty)
      {
        var ps = new PotraceSettings
        {
          TurdSize = s.FilterSize,
          AlphaMax = s.CornerRounding,
          OptiCurve = s.OptimizeCurves,
          OptTolerance = s.OptimizeTolerance,
          TurnPolicy = s.TurnPolicy,
        };
        Trace = Potrace.Trace(Mask.Mask, ps);
        TraceMilliseconds = watch.ElapsedMilliseconds;
        watch.Restart();
        Triangulation = AlphaShape.Prepare(Trace.Paths);
        AutoAlpha = AlphaShape.FindAutoAlpha(Triangulation);
      }

      double alpha = s.AlphaPixels > 0 ? s.AlphaPixels : AutoAlpha;
      if (traceDirty || alpha != AlphaUsed || s.KeepHoles != last.KeepHoles)
      {
        Loops = AlphaShape.Extract(Triangulation, alpha, s.KeepHoles);
        AlphaUsed = alpha;
        OutlineMilliseconds = watch.ElapsedMilliseconds;
      }
      last = s.Clone();
    }
  }
}

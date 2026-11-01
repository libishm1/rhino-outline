// RhinoOutline - image to outline curves for Rhino.
// Copyright (C) 2026 RhinoOutline contributors. Licensed under the GNU GPL v2, see LICENSE.

using System;
using System.Collections.Generic;

namespace RhinoOutline.Core
{
  /// <summary>A point or vector in image space (pixels, x to the right, y down).</summary>
  public struct Vec2
  {
    public double X;
    public double Y;

    public Vec2(double x, double y)
    {
      X = x;
      Y = y;
    }

    public static Vec2 Lerp(Vec2 a, Vec2 b, double t) => new Vec2(a.X + t * (b.X - a.X), a.Y + t * (b.Y - a.Y));

    public double DistanceTo(Vec2 other)
    {
      double dx = X - other.X, dy = Y - other.Y;
      return Math.Sqrt(dx * dx + dy * dy);
    }

    public override string ToString() => $"({X:0.###}, {Y:0.###})";
  }

  public enum SegmentKind
  {
    Line,
    Bezier,
  }

  /// <summary>One piece of a traced path. Lines use P0 and P3, cubic Béziers use all four points.</summary>
  public struct TraceSegment
  {
    public SegmentKind Kind;
    public Vec2 P0;
    public Vec2 P1;
    public Vec2 P2;
    public Vec2 P3;

    public static TraceSegment Line(Vec2 a, Vec2 b) =>
      new TraceSegment { Kind = SegmentKind.Line, P0 = a, P1 = a, P2 = b, P3 = b };

    public static TraceSegment Bezier(Vec2 a, Vec2 b, Vec2 c, Vec2 d) =>
      new TraceSegment { Kind = SegmentKind.Bezier, P0 = a, P1 = b, P2 = c, P3 = d };

    public Vec2 PointAt(double t)
    {
      if (Kind == SegmentKind.Line)
        return Vec2.Lerp(P0, P3, t);
      double s = 1.0 - t;
      double a = s * s * s, b = 3.0 * s * s * t, c = 3.0 * s * t * t, d = t * t * t;
      return new Vec2(a * P0.X + b * P1.X + c * P2.X + d * P3.X,
                      a * P0.Y + b * P1.Y + c * P2.Y + d * P3.Y);
    }

    /// <summary>Length of the control polygon, an upper bound of the arc length.</summary>
    public double ApproxLength => Kind == SegmentKind.Line
      ? P0.DistanceTo(P3)
      : P0.DistanceTo(P1) + P1.DistanceTo(P2) + P2.DistanceTo(P3);
  }

  /// <summary>
  /// A closed traced boundary in image space. Segment k spans the path parameters [k, k+1],
  /// which is also how the Rhino curves built from it are parameterized.
  /// </summary>
  public sealed class TracePath
  {
    public readonly List<TraceSegment> Segments = new List<TraceSegment>();

    /// <summary>True when the path bounds a hole (background enclosed by the shape).</summary>
    public bool IsHole;

    /// <summary>Number of pixels enclosed by the raw, unsmoothed path.</summary>
    public int Area;

    public Vec2 PointAt(double parameter)
    {
      int n = Segments.Count;
      int k = (int)Math.Floor(parameter);
      double t = parameter - k;
      if (k >= n) { k = n - 1; t = 1.0; }
      if (k < 0) { k = 0; t = 0.0; }
      return Segments[k].PointAt(t);
    }
  }
}

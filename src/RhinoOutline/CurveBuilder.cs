// RhinoOutline - image to outline curves for Rhino.
// Copyright (C) 2026 RhinoOutline contributors. Licensed under the GNU GPL v2, see LICENSE.

using System.Collections.Generic;
using System.Linq;
using Rhino.Geometry;
using RhinoOutline.Core;

namespace RhinoOutline
{
  /// <summary>Turns traced paths and alpha shape loops into Rhino curves.</summary>
  static class CurveBuilder
  {
    // Join tolerance in pixels; pieces meet at sample points that agree to about 1e-3 px.
    const double PixelTolerance = 0.01;

    /// <summary>Image space (pixels, y down) to the XY plane, lower left corner at the origin.</summary>
    public static Transform ImageToWorld(int imageHeight, double unitsPerPixel)
    {
      var xf = Transform.Identity;
      xf.M00 = unitsPerPixel;
      xf.M11 = -unitsPerPixel;
      xf.M13 = imageHeight * unitsPerPixel;
      return xf;
    }

    /// <summary>
    /// A closed curve in image space, parameterized so segment k spans [k, k+1], the same
    /// parameters the core uses for its samples.
    /// </summary>
    public static Curve PathCurve(TracePath path)
    {
      if (path.Segments.All(s => s.Kind == SegmentKind.Line))
      {
        var pts = new List<Point3d>(path.Segments.Count + 1);
        foreach (TraceSegment s in path.Segments)
          pts.Add(P(s.P0));
        pts.Add(pts[0]);
        return new PolylineCurve(pts);
      }

      var poly = new PolyCurve();
      foreach (TraceSegment s in path.Segments)
      {
        Curve piece = s.Kind == SegmentKind.Line
          ? (Curve)new LineCurve(P(s.P0), P(s.P3))
          : new BezierCurve(new[] { P(s.P0), P(s.P1), P(s.P2), P(s.P3) }).ToNurbsCurve();
        piece.Domain = new Interval(0, 1);
        poly.Append(piece);
      }
      return poly;
    }

    /// <summary>One closed outline curve for a loop, in image space.</summary>
    public static Curve LoopCurve(OutlineLoop loop, TriangulatedTrace tt, IList<Curve> pathCurves)
    {
      var pieces = new List<Curve>();
      foreach (OutlinePiece piece in loop.Pieces)
      {
        if (piece.IsFullPath)
          return pathCurves[piece.Path].DuplicateCurve();

        if (piece.IsBridge)
        {
          Point3d a = SamplePoint(tt, pathCurves, piece.FromVertex);
          Point3d b = SamplePoint(tt, pathCurves, piece.ToVertex);
          if (a.DistanceTo(b) > 1e-9)
            pieces.Add(new LineCurve(a, b));
          continue;
        }

        Curve path = pathCurves[piece.Path];
        double from = tt.ParamOf[piece.FromVertex];
        double to = tt.ParamOf[piece.ToVertex];
        Curve run = piece.Forward ? path.Trim(from, to) : path.Trim(to, from);
        if (run == null)
        {
          pieces.Add(new LineCurve(SamplePoint(tt, pathCurves, piece.FromVertex), SamplePoint(tt, pathCurves, piece.ToVertex)));
          continue;
        }
        if (!piece.Forward)
          run.Reverse();
        pieces.Add(run);
      }

      Curve[] joined = Curve.JoinCurves(pieces, PixelTolerance);
      if (joined != null && joined.Length == 1 && joined[0].IsClosed)
        return joined[0];

      // Fall back to the sampled polygon, which is always closed.
      var pts = loop.Vertices.Select(i => new Point3d(tt.Coords[2 * i], tt.Coords[2 * i + 1], 0)).ToList();
      pts.Add(pts[0]);
      return new PolylineCurve(pts);
    }

    static Point3d SamplePoint(TriangulatedTrace tt, IList<Curve> pathCurves, int sample) =>
      pathCurves[tt.PathOf[sample]].PointAt(tt.ParamOf[sample]);

    static Point3d P(Vec2 v) => new Point3d(v.X, v.Y, 0);
  }
}

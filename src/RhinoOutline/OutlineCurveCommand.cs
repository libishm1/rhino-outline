// RhinoOutline - image to outline curves for Rhino.
// Copyright (C) 2026 RhinoOutline contributors. Licensed under the GNU GPL v2, see LICENSE.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using RhinoOutline.Core;

namespace RhinoOutline
{
  /// <summary>
  /// OutlineCurve: the outline of curves already in the model. The selected curves are projected
  /// onto the active construction plane (in memory only; nothing but the outline is added), closed
  /// curves count as filled regions and open curves as strokes, and the alpha shape wraps them all
  /// into closed outlines on the construction plane. Where the outline follows a curve it is that
  /// (projected) curve; between curves it is a straight bridge.
  /// </summary>
  public class OutlineCurveCommand : Command
  {
    public override string EnglishName => "OutlineCurve";

    // The projected curves are scaled so their extents span this many units of the core's
    // "pixel" space, where it samples every 0.75 units.
    const double Frame = 2000.0;

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      var settings = OutlinePlugIn.Instance.Settings;
      double alpha = settings.GetDouble("CurveAlpha", 0.0);
      bool holes = settings.GetBool("CurveHoles", false);

      var go = new GetObject();
      go.SetCommandPrompt("Select curves to outline");
      go.GeometryFilter = ObjectType.Curve;
      go.GroupSelect = true;
      go.EnablePreSelect(true, true);
      while (true)
      {
        go.ClearCommandOptions();
        var optAlphaValue = new OptionDouble(alpha, true, 0.0);
        int optAlpha = go.AddOptionDouble("Alpha", ref optAlphaValue, "Alpha radius in model units, 0 = automatic");
        var optHolesValue = new OptionToggle(holes, "No", "Yes");
        int optHoles = go.AddOptionToggle("Holes", ref optHolesValue);
        GetResult res = go.GetMultiple(1, 0);
        if (res == GetResult.Option)
        {
          if (go.Option().Index == optAlpha) alpha = optAlphaValue.CurrentValue;
          else if (go.Option().Index == optHoles) holes = optHolesValue.CurrentValue;
          go.EnablePreSelect(false, true);
          continue;
        }
        if (res != GetResult.Object)
          return go.CommandResult();
        break;
      }
      settings.SetDouble("CurveAlpha", alpha);
      settings.SetBool("CurveHoles", holes);

      Plane plane = doc.Views.ActiveView?.ActiveViewport.ConstructionPlane() ?? Plane.WorldXY;
      double tolerance = doc.ModelAbsoluteTolerance;

      // Project onto the construction plane and express in its coordinates (z = 0). Only these
      // in-memory copies are used; the document is not changed.
      Transform toPlaneCoords = Transform.PlaneToPlane(plane, Plane.WorldXY);
      var flat = new List<Curve>();
      int skipped = 0;
      for (int i = 0; i < go.ObjectCount; i++)
      {
        Curve c = go.Object(i).Curve();
        Curve p = c == null ? null : Curve.ProjectToPlane(c, plane);
        if (p == null || !p.IsValid || p.GetLength() <= tolerance)
        {
          skipped++;
          continue;
        }
        p.Transform(toPlaneCoords);
        flat.Add(p);
      }
      if (skipped > 0)
        RhinoApp.WriteLine($"OutlineCurve: {skipped} curve(s) are perpendicular to the construction plane and were left out.");
      if (flat.Count == 0)
        return Result.Nothing;

      // Into the core's working space: the larger side of the extents becomes Frame units.
      var box = BoundingBox.Empty;
      foreach (Curve c in flat)
        box.Union(c.GetBoundingBox(true));
      double size = Math.Max(box.Max.X - box.Min.X, box.Max.Y - box.Min.Y);
      if (size <= tolerance)
      {
        RhinoApp.WriteLine("OutlineCurve: the curves have no extent on the construction plane.");
        return Result.Nothing;
      }
      double scale = Frame / size;
      Transform toWork = Transform.Scale(Point3d.Origin, scale) * Transform.Translation(-box.Min.X, -box.Min.Y, 0);
      foreach (Curve c in flat)
        c.Transform(toWork);

      var paths = new List<TracePath>();
      var curveParams = new List<double[]>();
      foreach (Curve c in flat)
      {
        double[] t = PolylineParameters(c);
        var path = new TracePath { IsOpen = !c.IsClosed };
        for (int k = 0; k + 1 < t.Length; k++)
        {
          Point3d a = c.PointAt(t[k]), b = c.PointAt(t[k + 1]);
          path.Segments.Add(TraceSegment.Line(new Vec2(a.X, a.Y), new Vec2(b.X, b.Y)));
        }
        paths.Add(path);
        curveParams.Add(t);
      }

      // Path parameter (segment k spans [k, k+1]) to curve parameter.
      double ToCurveParam(int path, double p)
      {
        double[] t = curveParams[path];
        int k = Math.Max(0, Math.Min(t.Length - 2, (int)Math.Floor(p)));
        return t[k] + (p - k) * (t[k + 1] - t[k]);
      }

      TriangulatedTrace tt = AlphaShape.Prepare(paths);
      AlphaShape.IgnorePathSlivers(tt);
      double autoAlpha = AlphaShape.FindAutoAlphaForCurves(tt);
      double alphaWork = alpha > 0 ? alpha * scale : autoAlpha;
      List<OutlineLoop> loops = AlphaShape.Extract(tt, alphaWork, holes, pathsAreWalls: true);
      if (loops.Count == 0)
      {
        RhinoApp.WriteLine("OutlineCurve: the curves enclose no area (a single straight line, or collinear lines). Try a larger Alpha.");
        return Result.Nothing;
      }

      Transform back = Transform.PlaneToPlane(Plane.WorldXY, plane) * Transform.Translation(box.Min.X, box.Min.Y, 0) * Transform.Scale(Point3d.Origin, 1.0 / scale);
      var attr = new ObjectAttributes { LayerIndex = OutlineCommand.Layer(doc, OutlineCommand.OutlineLayer, Color.FromArgb(220, 30, 40)), Name = "curve outline" };
      doc.Objects.UnselectAll();
      int added = 0;
      foreach (OutlineLoop loop in loops)
      {
        Curve c = CurveBuilder.LoopCurve(loop, tt, flat, ToCurveParam);
        c.Transform(back);
        // Rhino convention: outer boundaries counter-clockwise, holes clockwise.
        CurveOrientation want = loop.IsHole ? CurveOrientation.Clockwise : CurveOrientation.CounterClockwise;
        if (c.ClosedCurveOrientation(plane.ZAxis) != want)
          c.Reverse();
        Guid id = doc.Objects.AddCurve(c, attr);
        if (id != Guid.Empty)
        {
          doc.Objects.Select(id);
          added++;
        }
      }
      doc.Views.Redraw();

      string units = doc.GetUnitSystemName(true, false, true, false);
      string alphaText = alpha > 0 ? OutlineCommand.Format(alpha) : "Auto (" + OutlineCommand.Format(autoAlpha / scale) + ")";
      RhinoApp.WriteLine($"OutlineCurve: {flat.Count} curve(s) projected to the construction plane, {added} outline(s), Alpha={alphaText} {units}.");
      return added > 0 ? Result.Success : Result.Failure;
    }

    /// <summary>
    /// Curve parameters of a polyline that follows the curve closely: about every 2 units of the
    /// working space, plus every kink, so corners are kept. For a closed curve the last parameter
    /// is the end of the domain (the same point as the start).
    /// </summary>
    static double[] PolylineParameters(Curve c)
    {
      Interval d = c.Domain;
      int n = (int)Math.Min(20000, Math.Max(8, Math.Ceiling(c.GetLength() / 2.0)));
      var t = new List<double>(c.DivideByCount(n, true) ?? new[] { d.Min });
      double s = d.Min;
      while (c.GetNextDiscontinuity(Continuity.C1_locus_continuous, s, d.Max, out double k))
      {
        t.Add(k);
        s = k;
      }
      t.Add(d.Min);
      t.Add(d.Max);
      t.Sort();
      var unique = new List<double>();
      double eps = 1e-9 * Math.Max(1.0, d.Length);
      foreach (double v in t)
      {
        if (v < d.Min || v > d.Max) continue;
        if (unique.Count == 0 || v - unique[unique.Count - 1] > eps)
          unique.Add(v);
      }
      return unique.ToArray();
    }
  }
}

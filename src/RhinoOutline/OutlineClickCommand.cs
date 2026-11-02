// RhinoOutline - image to outline curves for Rhino.
// Copyright (C) 2026 RhinoOutline contributors. Licensed under the GNU GPL v2, see LICENSE.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using RhinoOutline.Core;
using RhinoOutline.Segmentation;

namespace RhinoOutline
{
  /// <summary>
  /// OutlineClick: select a picture, click on the object in it, and MobileSAM (a small Segment
  /// Anything model) finds the object under the click. More clicks add to it; Larger and Smaller
  /// step through the other objects the network sees under one click (a part of the object, the
  /// whole object, the object with what it stands on). Works on photos where the background,
  /// shadows or clutter defeat the automatic modes.
  /// </summary>
  public class OutlineClickCommand : Command
  {
    public override string EnglishName => "OutlineClick";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      var go = new GetObject();
      go.SetCommandPrompt("Select a picture");
      go.GeometryFilter = ObjectType.Surface | ObjectType.Brep;
      go.SetCustomGeometryFilter((o, g, ci) => OutlinePictureCommand.TryGetFrame(doc, o, out _, out _));
      go.SubObjectSelect = false;
      if (go.Get() != GetResult.Object)
        return go.CommandResult();
      RhinoObject obj = go.Object(0).Object();
      if (!OutlinePictureCommand.TryGetFrame(doc, obj, out OutlinePictureCommand.PictureFrame frame, out string problem))
      {
        RhinoApp.WriteLine($"OutlineClick: {problem}.");
        return Result.Failure;
      }

      RgbaImage image;
      try
      {
        image = ImageLoader.Load(frame.ImagePath, out _);
      }
      catch (Exception ex)
      {
        RhinoApp.WriteLine($"OutlineClick: cannot read \"{Path.GetFileName(frame.ImagePath)}\" ({ex.Message}).");
        return Result.Failure;
      }

      Transform xf = OutlinePictureCommand.PixelToWorld(frame, image, out double unitsPerPixel);
      if (!xf.TryGetInverse(out Transform toPixels))
        return Result.Failure;
      var plane = new Plane(frame.Origin, frame.Right, frame.Up);

      ClipRegion clip = null;
      List<Curve> outerLoops = null, innerLoops = null;
      if (frame.Cropped && OutlinePictureCommand.CropLoops(frame.Face, out outerLoops, out innerLoops))
      {
        clip = new ClipRegion();
        foreach (Curve loop in outerLoops.Concat(innerLoops))
          clip.Polygons.Add(OutlinePictureCommand.PixelPolygon(loop, toPixels));
      }

      OutlineOptions options = OutlineOptions.Load();
      options.Output = OutputKind.Outline;
      var engine = new OutlineEngine(image);
      var clicks = new List<Point3d>();
      var pixels = new List<(double X, double Y)>();
      List<Curve> outline = new List<Curve>();
      List<byte[]> candidates = null;
      int current = 0;
      var conduit = new ClickConduit();
      string name = string.IsNullOrEmpty(obj.Attributes.Name) ? Path.GetFileNameWithoutExtension(frame.ImagePath) : obj.Attributes.Name;
      doc.Objects.UnselectAll();
      doc.Views.Redraw();

      try
      {
        conduit.Enabled = true;
        while (true)
        {
          var gp = new GetPoint();
          gp.SetCommandPrompt(clicks.Count == 0
            ? "Click on the object in the picture"
            : "Click again to add more of the object. Press Enter to keep the outline");
          gp.Constrain(plane, false);
          gp.AcceptNothing(true);
          int optUndo = clicks.Count > 0 ? gp.AddOption("Undo") : -1;
          int optLarger = candidates != null && current < candidates.Count - 1 ? gp.AddOption("Larger") : -1;
          int optSmaller = candidates != null && current > 0 ? gp.AddOption("Smaller") : -1;
          GetResult res = gp.Get();
          bool rerun = true;

          if (res == GetResult.Nothing)
          {
            if (clicks.Count == 0 || outline.Count == 0)
              return Result.Cancel;
            break;
          }
          if (res == GetResult.Option && gp.Option().Index == optUndo)
          {
            clicks.RemoveAt(clicks.Count - 1);
            pixels.RemoveAt(pixels.Count - 1);
          }
          else if (res == GetResult.Option && (gp.Option().Index == optLarger || gp.Option().Index == optSmaller))
          {
            current += gp.Option().Index == optLarger ? 1 : -1;
            rerun = false;
          }
          else if (res == GetResult.Point)
          {
            Point3d p = gp.Point();
            Point3d q = toPixels * p;
            if (q.X < 0 || q.Y < 0 || q.X >= image.Width || q.Y >= image.Height)
            {
              RhinoApp.WriteLine("OutlineClick: click inside the picture.");
              continue;
            }
            clicks.Add(p);
            pixels.Add((q.X, q.Y));
          }
          else
            return Result.Cancel;

          outline = new List<Curve>();
          if (pixels.Count == 0)
            candidates = null;
          else
          {
            if (rerun)
            {
              try
              {
                candidates = NeuralSegmenter.ObjectAt(image, pixels, out current);
              }
              catch (Exception ex)
              {
                RhinoApp.WriteLine($"OutlineClick: the segmentation network failed ({ex.Message}).");
                return Result.Failure;
              }
              if (candidates == null)
                return Result.Cancel;
            }
            options.Core.Detect = ShapeDetect.AI;
            options.Core.Probability = candidates[current];
            options.Core.Clip = clip;
            OutlineCommand.Compute(engine, options, xf, unitsPerPixel, out List<Curve> trace, out outline);
            if (clip != null)
              outline = OutlinePictureCommand.ClipToCrop(outline, outerLoops[0], innerLoops, plane, doc.ModelAbsoluteTolerance);
            RhinoApp.WriteLine($"OutlineClick: {clicks.Count} click(s), object {current + 1} of {candidates.Count} (smallest first), {outline.Count} outline(s).");
          }
          conduit.Set(outline, clicks);
          doc.Views.Redraw();
        }
      }
      finally
      {
        conduit.Enabled = false;
        doc.Views.Redraw();
      }

      var attr = new ObjectAttributes { LayerIndex = OutlineCommand.Layer(doc, OutlineCommand.OutlineLayer, Color.FromArgb(220, 30, 40)), Name = name + " outline" };
      foreach (Curve c in outline)
        doc.Objects.Select(doc.Objects.AddCurve(c, attr));
      doc.Views.Redraw();
      return Result.Success;
    }

    sealed class ClickConduit : DisplayConduit
    {
      List<Curve> outline = new List<Curve>();
      List<Point3d> clicks = new List<Point3d>();

      public void Set(List<Curve> curves, List<Point3d> points)
      {
        outline = curves;
        clicks = new List<Point3d>(points);
      }

      protected override void CalculateBoundingBox(CalculateBoundingBoxEventArgs e)
      {
        foreach (Curve c in outline)
          e.IncludeBoundingBox(c.GetBoundingBox(false));
      }

      protected override void DrawOverlay(DrawEventArgs e)
      {
        foreach (Curve c in outline)
          e.Display.DrawCurve(c, Color.FromArgb(220, 30, 40), 3);
        foreach (Point3d p in clicks)
          e.Display.DrawPoint(p, PointStyle.RoundSimple, 7, Color.FromArgb(255, 200, 0));
      }
    }
  }
}

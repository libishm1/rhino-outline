// RhinoOutline - image to outline curves for Rhino.
// Copyright (C) 2026 RhinoOutline contributors. Licensed under the GNU GPL v2, see LICENSE.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
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
  /// OutlinePicture: outline pictures that are already in the model (made with the Picture
  /// command). The outline lands on each picture frame, following its position, rotation and size.
  /// </summary>
  public class OutlinePictureCommand : Command
  {
    public override string EnglishName => "OutlinePicture";

    /// <summary>Where a picture's image sits in the world: lower left corner and the two edges.</summary>
    struct PictureFrame
    {
      public string ImagePath;
      public Point3d Origin;   // lower left of the image
      public Vector3d Right;   // bottom edge, full image width
      public Vector3d Up;      // left edge, full image height
    }

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      OutlineOptions options = OutlineOptions.Load();

      var go = new GetObject();
      go.SetCommandPrompt("Select pictures to outline");
      go.GeometryFilter = ObjectType.Surface | ObjectType.Brep;
      go.SetCustomGeometryFilter((obj, geom, ci) => TryGetFrame(doc, obj, out _, out _));
      go.GroupSelect = true;
      go.SubObjectSelect = false;
      go.EnablePreSelect(true, true);

      while (true)
      {
        go.ClearCommandOptions();
        var alpha = new OptionDouble(options.Alpha, true, 0.0);
        int optAlpha = go.AddOptionDouble("Alpha", ref alpha, "Alpha radius in model units, 0 = automatic");
        var holes = new OptionToggle(options.Core.KeepHoles, "No", "Yes");
        int optHoles = go.AddOptionToggle("Holes", ref holes);
        int optOutput = go.AddOptionEnumList("Output", options.Output);

        GetResult res = go.GetMultiple(1, 0);
        if (res == GetResult.Option)
        {
          int index = go.Option().Index;
          if (index == optAlpha) options.Alpha = alpha.CurrentValue;
          else if (index == optHoles) options.Core.KeepHoles = holes.CurrentValue;
          else if (index == optOutput) options.Output = (OutputKind)go.Option().CurrentListOptionIndex;
          go.EnablePreSelect(false, true);
          continue;
        }
        if (res != GetResult.Object)
          return go.CommandResult();
        break;
      }

      var created = new List<Guid>();
      int done = 0;
      for (int i = 0; i < go.ObjectCount; i++)
      {
        RhinoObject obj = go.Object(i).Object();
        if (!TryGetFrame(doc, obj, out PictureFrame frame, out string problem))
        {
          RhinoApp.WriteLine($"OutlinePicture: skipped an object, {problem}.");
          continue;
        }

        RgbaImage image;
        try
        {
          image = ImageLoader.Load(frame.ImagePath, out _);
        }
        catch (Exception ex)
        {
          RhinoApp.WriteLine($"OutlinePicture: cannot read \"{Path.GetFileName(frame.ImagePath)}\" ({ex.Message}).");
          continue;
        }

        // Pixel (x, y) with y down maps to Origin + x/W * Right + (H - y)/H * Up.
        Vector3d px = frame.Right / image.Width;
        Vector3d py = -frame.Up / image.Height;
        Vector3d normal = Vector3d.CrossProduct(frame.Right, frame.Up);
        normal.Unitize();
        Point3d topLeft = frame.Origin + frame.Up;
        var xf = Transform.Identity;
        xf.M00 = px.X; xf.M01 = py.X; xf.M02 = normal.X; xf.M03 = topLeft.X;
        xf.M10 = px.Y; xf.M11 = py.Y; xf.M12 = normal.Y; xf.M13 = topLeft.Y;
        xf.M20 = px.Z; xf.M21 = py.Z; xf.M22 = normal.Z; xf.M23 = topLeft.Z;

        var engine = new OutlineEngine(image);
        OutlineCommand.Compute(engine, options, xf, px.Length, out List<Curve> trace, out List<Curve> outline);

        string name = string.IsNullOrEmpty(obj.Attributes.Name) ? Path.GetFileNameWithoutExtension(frame.ImagePath) : obj.Attributes.Name;
        if (options.Output != OutputKind.Outline)
        {
          var attr = new ObjectAttributes { LayerIndex = OutlineCommand.Layer(doc, OutlineCommand.TraceLayer, Color.FromArgb(90, 90, 90)), Name = name + " trace" };
          attr.AddToGroup(doc.Groups.Add());
          foreach (Curve c in trace)
            doc.Objects.AddCurve(c, attr);
        }
        if (options.Output != OutputKind.Trace)
        {
          var attr = new ObjectAttributes { LayerIndex = OutlineCommand.Layer(doc, OutlineCommand.OutlineLayer, Color.FromArgb(220, 30, 40)), Name = name + " outline" };
          foreach (Curve c in outline)
            created.Add(doc.Objects.AddCurve(c, attr));
        }

        string alphaText = options.Alpha > 0
          ? OutlineCommand.Format(options.Alpha)
          : "Auto (" + OutlineCommand.Format(engine.AutoAlpha * px.Length) + ")";
        RhinoApp.WriteLine($"OutlinePicture: {Path.GetFileName(frame.ImagePath)}: {engine.Mask.Description}; " +
                           $"{trace.Count} traced curve(s), {outline.Count} outline(s), Alpha={alphaText}.");
        done++;
      }

      options.Save();
      doc.Objects.UnselectAll();
      foreach (Guid id in created)
        doc.Objects.Select(id);
      doc.Views.Redraw();
      return done > 0 ? Result.Success : Result.Nothing;
    }

    /// <summary>
    /// Finds the image of a picture frame and where it sits. Works for any planar, untrimmed
    /// single face object whose material has a bitmap texture, so moved, rotated, scaled and
    /// embedded pictures all work.
    /// </summary>
    static bool TryGetFrame(RhinoDoc doc, RhinoObject obj, out PictureFrame frame, out string problem)
    {
      frame = default(PictureFrame);
      problem = null;

      Brep brep = obj?.Geometry as Brep ?? (obj?.Geometry as Surface)?.ToBrep();
      if (brep == null || brep.Faces.Count != 1)
      {
        problem = "it is not a picture";
        return false;
      }

      string file = obj.GetMaterial(true)?.GetTexture(TextureType.Bitmap)?.FileName;
      if (string.IsNullOrEmpty(file))
      {
        problem = "it has no picture texture";
        return false;
      }

      // Finds the file next to the model too, and unpacks pictures embedded in the .3dm.
      string found = Rhino.Render.Utilities.FindFile(doc, file, true);
      if (string.IsNullOrEmpty(found) || !File.Exists(found))
      {
        problem = $"its image \"{Path.GetFileName(file)}\" was not found";
        return false;
      }

      BrepFace face = brep.Faces[0];
      if (!face.IsPlanar(doc.ModelAbsoluteTolerance) || !face.IsSurface)
      {
        problem = "the picture is not flat or is trimmed";
        return false;
      }

      Interval u = face.Domain(0), v = face.Domain(1);
      Point3d p00 = face.PointAt(u.Min, v.Min);
      Point3d p10 = face.PointAt(u.Max, v.Min);
      Point3d p01 = face.PointAt(u.Min, v.Max);
      Point3d p11 = face.PointAt(u.Max, v.Max);
      if ((p10 + (p01 - p00)).DistanceTo(p11) > 1e-6 * Math.Max(1.0, p00.DistanceTo(p11)))
      {
        problem = "the picture frame is not a parallelogram";
        return false;
      }

      frame.ImagePath = found;
      frame.Origin = p00;
      frame.Right = p10 - p00;
      frame.Up = p01 - p00;
      return true;
    }
  }
}

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
  public enum OutputKind
  {
    Outline,
    Trace,
    Both,
  }

  /// <summary>
  /// Outline: pick an image, get its outline. -Outline: type a file name and adjust the options
  /// on the command line with a live preview.
  /// </summary>
  public class OutlineCommand : Command
  {
    internal const string OutlineLayer = "Outline";
    internal const string TraceLayer = "Outline Trace";
    const string ImageLayer = "Outline Image";

    public override string EnglishName => "Outline";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      string path = mode == RunMode.Interactive ? PickFile() : AskFile();
      if (path == null)
        return Result.Cancel;

      RgbaImage image;
      double dpi;
      try
      {
        image = ImageLoader.Load(path, out dpi);
      }
      catch (Exception ex)
      {
        RhinoApp.WriteLine($"Outline: cannot read \"{Path.GetFileName(path)}\" ({ex.Message}).");
        return Result.Failure;
      }

      OutlineOptions options = OutlineOptions.Load();
      var engine = new OutlineEngine(image);
      if (mode != RunMode.Interactive)
      {
        Result result = RunOptionsLoop(doc, engine, options, dpi);
        if (result != Result.Success)
          return result;
      }

      Compute(engine, options, doc, dpi, out List<Curve> trace, out List<Curve> outline);
      AddToDocument(doc, path, image, options, dpi, trace, outline);
      options.Save();
      Report(doc, path, engine, options, dpi, trace.Count, outline.Count);
      if (mode == RunMode.Interactive)
        RhinoApp.WriteLine("Outline: run -Outline to change the detection, alpha, size or output.");
      return Result.Success;
    }

    static string PickFile()
    {
      var dialog = new Rhino.UI.OpenFileDialog
      {
        Title = "Select an image to outline",
        Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All files (*.*)|*.*",
      };
      return dialog.ShowOpenDialog() && File.Exists(dialog.FileName) ? dialog.FileName : null;
    }

    static string AskFile()
    {
      var gs = new GetString();
      gs.SetCommandPrompt("Image file to outline");
      if (gs.Get() != GetResult.String)
        return null;
      string path = gs.StringResult()?.Trim().Trim('"');
      if (string.IsNullOrEmpty(path) || !File.Exists(path))
      {
        RhinoApp.WriteLine("Outline: the file was not found.");
        return null;
      }
      return path;
    }

    Result RunOptionsLoop(RhinoDoc doc, OutlineEngine engine, OutlineOptions o, double dpi)
    {
      var conduit = new PreviewConduit();
      bool zoomed = false;
      try
      {
        while (true)
        {
          Compute(engine, o, doc, dpi, out List<Curve> trace, out List<Curve> outline);
          conduit.Set(o.Output == OutputKind.Outline ? new List<Curve>() : trace,
                      o.Output == OutputKind.Trace ? new List<Curve>() : outline,
                      ImageFrame(engine.Image, UnitsPerPixel(doc, engine.Image, o, dpi)));
          conduit.Enabled = true;
          if (!zoomed)
          {
            doc.Views.ActiveView?.ActiveViewport.ZoomBoundingBox(conduit.Bounds);
            zoomed = true;
          }
          doc.Views.Redraw();
          RhinoApp.WriteLine(Summary(doc, engine, o, dpi, trace.Count, outline.Count));

          var go = new GetOption();
          go.SetCommandPrompt("Outline options. Press Enter when done");
          go.AcceptNothing(true);
          int optDetect = go.AddOptionEnumList("Detect", o.Core.Detect);
          int optModel = o.Core.Detect == ShapeDetect.AI ? go.AddOptionEnumList("AIModel", o.Model) : -1;
          var threshold = new OptionDouble(o.Core.Threshold, 0.0, 1.0);
          int optThreshold = go.AddOptionDouble("Threshold", ref threshold, "Threshold, higher includes more pixels (0 to 1)");
          var invert = new OptionToggle(o.Core.Invert, "No", "Yes");
          int optInvert = go.AddOptionToggle("Invert", ref invert);
          var filter = new OptionInteger(o.Core.FilterSize, 0, 1000000);
          int optFilter = go.AddOptionInteger("FilterSize", ref filter, "Ignore speckles up to this many pixels");
          var corner = new OptionDouble(o.Core.CornerRounding, 0.0, 1.3334);
          int optCorner = go.AddOptionDouble("CornerRounding", ref corner, "Corner rounding, 0 = sharp polygon");
          var alpha = new OptionDouble(o.Alpha, true, 0.0);
          int optAlpha = go.AddOptionDouble("Alpha", ref alpha, "Alpha radius in model units, 0 = automatic");
          var holes = new OptionToggle(o.Core.KeepHoles, "No", "Yes");
          int optHoles = go.AddOptionToggle("Holes", ref holes);
          int optOutput = go.AddOptionEnumList("Output", o.Output);
          var width = new OptionDouble(o.Width, true, 0.0);
          int optWidth = go.AddOptionDouble("Width", ref width, "Image width in model units, 0 = from the image resolution");
          var picture = new OptionToggle(o.PlaceImage, "No", "Yes");
          int optPicture = go.AddOptionToggle("PlaceImage", ref picture);
          int optDefaults = go.AddOption("RestoreDefaults");

          GetResult res = go.Get();
          if (res == GetResult.Nothing)
            return Result.Success;
          if (res != GetResult.Option)
            return Result.Cancel;

          int index = go.Option().Index;
          if (index == optDetect) o.Core.Detect = (ShapeDetect)go.Option().CurrentListOptionIndex;
          else if (index == optModel) o.Model = (AIModel)go.Option().CurrentListOptionIndex;
          else if (index == optThreshold) o.Core.Threshold = threshold.CurrentValue;
          else if (index == optInvert) o.Core.Invert = invert.CurrentValue;
          else if (index == optFilter) o.Core.FilterSize = filter.CurrentValue;
          else if (index == optCorner) o.Core.CornerRounding = corner.CurrentValue;
          else if (index == optAlpha) o.Alpha = alpha.CurrentValue;
          else if (index == optHoles) o.Core.KeepHoles = holes.CurrentValue;
          else if (index == optOutput) o.Output = (OutputKind)go.Option().CurrentListOptionIndex;
          else if (index == optWidth) o.Width = width.CurrentValue;
          else if (index == optPicture) o.PlaceImage = picture.CurrentValue;
          else if (index == optDefaults) o = CopyInto(o, new OutlineOptions());
        }
      }
      finally
      {
        conduit.Enabled = false;
        doc.Views.Redraw();
      }
    }

    static OutlineOptions CopyInto(OutlineOptions target, OutlineOptions source)
    {
      target.Core = source.Core;
      target.Alpha = source.Alpha;
      target.Width = source.Width;
      target.Output = source.Output;
      target.PlaceImage = source.PlaceImage;
      target.Model = source.Model;
      return target;
    }

    static double UnitsPerPixel(RhinoDoc doc, RgbaImage image, OutlineOptions o, double dpi)
    {
      if (o.Width > 0)
        return o.Width / image.Width;
      return RhinoMath.UnitScale(UnitSystem.Inches, doc.ModelUnitSystem) / dpi;
    }

    // Runs the engine and builds world space curves.
    static void Compute(OutlineEngine engine, OutlineOptions o, RhinoDoc doc, double dpi, out List<Curve> trace, out List<Curve> outline)
    {
      double scale = UnitsPerPixel(doc, engine.Image, o, dpi);
      Compute(engine, o, CurveBuilder.ImageToWorld(engine.Image.Height, scale), scale, out trace, out outline);
    }

    /// <summary>Runs the engine and maps the curves from pixels to the world with <paramref name="xf"/>.</summary>
    /// <param name="unitsPerPixel">Model units per pixel, used to convert the alpha option.</param>
    internal static void Compute(OutlineEngine engine, OutlineOptions o, Transform xf, double unitsPerPixel, out List<Curve> trace, out List<Curve> outline)
    {
      o.Core.AlphaPixels = o.Alpha > 0 ? o.Alpha / unitsPerPixel : 0;
      if (o.Core.Detect == ShapeDetect.AI && o.Core.Probability == null)
      {
        o.Core.Probability = AIProbability(engine.Image, o.Model);
        engine.Update(o.Core);
        o.Core.Probability = null;
      }
      else
        engine.Update(o.Core);

      List<Curve> pathCurves = engine.Trace.Paths.Select(CurveBuilder.PathCurve).ToList();

      outline = new List<Curve>();
      foreach (OutlineLoop loop in engine.Loops)
      {
        Curve c = CurveBuilder.LoopCurve(loop, engine.Triangulation, pathCurves);
        c.Transform(xf);
        // Rhino convention: outer boundaries counter-clockwise, holes clockwise.
        CurveOrientation want = loop.IsHole ? CurveOrientation.Clockwise : CurveOrientation.CounterClockwise;
        Vector3d normal = Vector3d.CrossProduct(xf * Vector3d.XAxis, -(xf * Vector3d.YAxis));
        if (c.ClosedCurveOrientation(normal) != want)
          c.Reverse();
        outline.Add(c);
      }

      trace = new List<Curve>(pathCurves.Count);
      foreach (Curve c in pathCurves)
      {
        Curve w = c.DuplicateCurve();
        w.Transform(xf);
        trace.Add(w);
      }
    }

    static RgbaImage aiImage;
    static AIModel aiModel;
    static byte[] aiProbability;

    /// <summary>Object probability from the network, kept for the last image so that changing
    /// other options does not run the network again. Null if the model is not available.</summary>
    static byte[] AIProbability(RgbaImage image, AIModel model)
    {
      if (ReferenceEquals(image, aiImage) && model == aiModel && aiProbability != null)
        return aiProbability;
      RhinoApp.WriteLine($"Outline: running the {model} segmentation network...");
      try
      {
        aiProbability = NeuralSegmenter.Probability(image, model);
      }
      catch (Exception ex)
      {
        RhinoApp.WriteLine($"Outline: the segmentation network failed ({ex.Message}).");
        aiProbability = null;
      }
      if (aiProbability == null)
        RhinoApp.WriteLine("Outline: no segmentation network, using Detect=Auto instead.");
      aiImage = image;
      aiModel = model;
      return aiProbability;
    }

    static Polyline ImageFrame(RgbaImage image, double scale)
    {
      double w = image.Width * scale, h = image.Height * scale;
      return new Polyline(new[] { Point3d.Origin, new Point3d(w, 0, 0), new Point3d(w, h, 0), new Point3d(0, h, 0), Point3d.Origin });
    }

    void AddToDocument(RhinoDoc doc, string path, RgbaImage image, OutlineOptions o, double dpi, List<Curve> trace, List<Curve> outline)
    {
      doc.Objects.UnselectAll();
      string name = Path.GetFileNameWithoutExtension(path);

      if (o.Output != OutputKind.Outline && trace.Count > 0)
      {
        var attr = new ObjectAttributes { LayerIndex = Layer(doc, TraceLayer, Color.FromArgb(90, 90, 90)), Name = name + " trace" };
        attr.AddToGroup(doc.Groups.Add());
        foreach (Curve c in trace)
          doc.Objects.AddCurve(c, attr);
      }

      if (o.Output != OutputKind.Trace)
      {
        var attr = new ObjectAttributes { LayerIndex = Layer(doc, OutlineLayer, Color.FromArgb(220, 30, 40)), Name = name + " outline" };
        foreach (Curve c in outline)
        {
          Guid id = doc.Objects.AddCurve(c, attr);
          doc.Objects.Select(id);
        }
      }

      if (o.PlaceImage)
      {
        double scale = UnitsPerPixel(doc, image, o, dpi);
        int current = doc.Layers.CurrentLayerIndex;
        doc.Layers.SetCurrentLayerIndex(Layer(doc, ImageLayer, Color.FromArgb(160, 160, 160)), true);
        doc.Objects.AddPictureFrame(Plane.WorldXY, path, false, image.Width * scale, image.Height * scale, true, false);
        doc.Layers.SetCurrentLayerIndex(current, true);
      }
      doc.Views.Redraw();
    }

    internal static int Layer(RhinoDoc doc, string name, Color color)
    {
      int index = doc.Layers.FindByFullPath(name, -1);
      if (index >= 0)
        return index;
      return doc.Layers.Add(new Layer { Name = name, Color = color });
    }

    static string Summary(RhinoDoc doc, OutlineEngine engine, OutlineOptions o, double dpi, int traceCount, int outlineCount)
    {
      double scale = UnitsPerPixel(doc, engine.Image, o, dpi);
      string units = doc.GetUnitSystemName(true, false, true, false);
      string alpha = o.Alpha > 0
        ? $"Alpha={Format(o.Alpha)} {units}"
        : $"Alpha=Auto ({Format(engine.AutoAlpha * scale)} {units})";
      return $"Outline: {engine.Mask.Description}; {traceCount} traced curve(s), {outlineCount} outline(s), {alpha}, " +
             $"size {Format(engine.Image.Width * scale)} x {Format(engine.Image.Height * scale)} {units}.";
    }

    void Report(RhinoDoc doc, string path, OutlineEngine engine, OutlineOptions o, double dpi, int traceCount, int outlineCount)
    {
      RhinoApp.WriteLine($"Outline: {Path.GetFileName(path)} ({engine.Image.Width} x {engine.Image.Height} px)");
      RhinoApp.WriteLine(Summary(doc, engine, o, dpi, traceCount, outlineCount));
    }

    internal static string Format(double v) => v.ToString(v >= 100 ? "0.#" : "0.###", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Draws the preview while the -Outline options are being adjusted.</summary>
    sealed class PreviewConduit : DisplayConduit
    {
      List<Curve> trace = new List<Curve>();
      List<Curve> outline = new List<Curve>();
      Polyline frame;

      public BoundingBox Bounds { get; private set; } = BoundingBox.Empty;

      public void Set(List<Curve> traceCurves, List<Curve> outlineCurves, Polyline imageFrame)
      {
        trace = traceCurves;
        outline = outlineCurves;
        frame = imageFrame;
        Bounds = new BoundingBox(imageFrame);
      }

      protected override void CalculateBoundingBox(CalculateBoundingBoxEventArgs e) => e.IncludeBoundingBox(Bounds);

      protected override void DrawOverlay(DrawEventArgs e)
      {
        if (frame != null)
          e.Display.DrawPolyline(frame, Color.FromArgb(170, 170, 170));
        foreach (Curve c in trace)
          e.Display.DrawCurve(c, Color.FromArgb(40, 90, 210), 1);
        foreach (Curve c in outline)
          e.Display.DrawCurve(c, Color.FromArgb(220, 30, 40), 3);
      }
    }
  }
}

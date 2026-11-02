// RhinoOutline - image to outline curves for Rhino.
// Copyright (C) 2026 RhinoOutline contributors. Licensed under the GNU GPL v2, see LICENSE.

using System;
using RhinoOutline.Core;
using RhinoOutline.Segmentation;

namespace RhinoOutline
{
  /// <summary>Options shared by Outline and OutlinePicture, remembered between sessions.</summary>
  sealed class OutlineOptions
  {
    public OutlineSettings Core = new OutlineSettings();

    /// <summary>Alpha radius in model units; 0 picks it automatically.</summary>
    public double Alpha;

    /// <summary>Image width in model units; 0 uses the image resolution. Not used for pictures.</summary>
    public double Width;

    public OutputKind Output = OutputKind.Outline;

    /// <summary>Also place the image as a picture frame. Not used for pictures.</summary>
    public bool PlaceImage;

    /// <summary>The network used by Detect=AI.</summary>
    public AIModel Model = AIModel.Accurate;

    public static OutlineOptions Load()
    {
      var o = new OutlineOptions();
      var s = OutlinePlugIn.Instance.Settings;
      o.Core.Detect = (ShapeDetect)s.GetInteger("Detect", (int)o.Core.Detect);
      o.Core.Threshold = s.GetDouble("Threshold", o.Core.Threshold);
      o.Core.Invert = s.GetBool("Invert", o.Core.Invert);
      o.Core.FilterSize = s.GetInteger("FilterSize", o.Core.FilterSize);
      o.Core.CornerRounding = s.GetDouble("CornerRounding", o.Core.CornerRounding);
      o.Core.KeepHoles = s.GetBool("Holes", o.Core.KeepHoles);
      o.Alpha = s.GetDouble("Alpha", o.Alpha);
      o.Width = s.GetDouble("Width", o.Width);
      o.Output = (OutputKind)s.GetInteger("Output", (int)o.Output);
      o.PlaceImage = s.GetBool("PlaceImage", o.PlaceImage);
      o.Model = (AIModel)s.GetInteger("AIModel", (int)o.Model);
      if (!Enum.IsDefined(typeof(ShapeDetect), o.Core.Detect)) o.Core.Detect = ShapeDetect.Auto;
      if (!Enum.IsDefined(typeof(OutputKind), o.Output)) o.Output = OutputKind.Outline;
      if (!Enum.IsDefined(typeof(AIModel), o.Model)) o.Model = AIModel.Accurate;
      return o;
    }

    public void Save()
    {
      var s = OutlinePlugIn.Instance.Settings;
      s.SetInteger("Detect", (int)Core.Detect);
      s.SetDouble("Threshold", Core.Threshold);
      s.SetBool("Invert", Core.Invert);
      s.SetInteger("FilterSize", Core.FilterSize);
      s.SetDouble("CornerRounding", Core.CornerRounding);
      s.SetBool("Holes", Core.KeepHoles);
      s.SetDouble("Alpha", Alpha);
      s.SetDouble("Width", Width);
      s.SetInteger("Output", (int)Output);
      s.SetBool("PlaceImage", PlaceImage);
      s.SetInteger("AIModel", (int)Model);
    }
  }
}

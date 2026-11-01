// RhinoOutline - image to outline curves for Rhino.
// Copyright (C) 2026 RhinoOutline contributors. Licensed under the GNU GPL v2, see LICENSE.

using Rhino.PlugIns;

namespace RhinoOutline
{
  public class OutlinePlugIn : PlugIn
  {
    public OutlinePlugIn()
    {
      Instance = this;
    }

    public static OutlinePlugIn Instance { get; private set; }

    public override PlugInLoadTime LoadTime => PlugInLoadTime.WhenNeeded;
  }
}

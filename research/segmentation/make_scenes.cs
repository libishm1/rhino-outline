// Renders the "render" test set in Rhino 8 (ScriptEditor, C#): six objects (chair, potted
// plant, teapot, lamp, bicycle, standing figure) on a ground plane with cast shadows, under
// three setups (studio, warm floor with low sun, dim). For every scene it saves
//   <setup>_<object>.png      the Rendered view with the ground plane and its shadows
//   <setup>_<object>_gt.png   the same view without the ground plane, transparent background:
//                             its alpha channel is the true object mask
// Output folder: D:\vectorise\work\seg\data\render (change OutDir below).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Render;

const string OutDir = @"D:\vectorise\work\seg\data\render";
var doc = RhinoDoc.ActiveDoc;
foreach (var o in doc.Objects.GetObjectList(ObjectType.AnyObject)) doc.Objects.Delete(o, true);
double tol = doc.ModelAbsoluteTolerance;

// ------------------------------------------------------------------ geometry
int Mat(string name, Color c, double gloss = 0.1)
{
  var m = new Material { Name = name, DiffuseColor = c, Shine = gloss * Material.MaxShine, ReflectionColor = Color.White };
  return doc.Materials.Add(m);
}
var groups = new Dictionary<string, List<Brep>>();
var mats = new Dictionary<string, int>();
void Add(string obj, Brep b) { if (b == null) return; if (!groups.ContainsKey(obj)) groups[obj] = new List<Brep>(); groups[obj].Add(b); }
Brep Pipe(Point3d a, Point3d b, double r) { var p = Brep.CreatePipe(new LineCurve(a, b), r, false, PipeCapMode.Round, true, tol, 0.1); return p != null && p.Length > 0 ? p[0] : null; }
Brep BoxB(double x0, double y0, double z0, double x1, double y1, double z1) => new Box(new BoundingBox(x0, y0, z0, x1, y1, z1)).ToBrep();
Brep Ell(Point3d c, double rx, double ry, double rz)
{
  var b = new Sphere(Point3d.Origin, 1).ToBrep();
  b.Transform(Transform.Scale(Plane.WorldXY, rx, ry, rz));
  b.Transform(Transform.Translation(c - Point3d.Origin));
  return b;
}
Point3d P(double x, double z) => new Point3d(x, 0, z);

mats["chair"] = Mat("chair", Color.FromArgb(214, 186, 140));
foreach (var (x, y) in new[] { (-20, -20), (20, -20), (-20, 20), (20, 20) }) Add("chair", BoxB(x - 1.6, y - 1.6, 0, x + 1.6, y + 1.6, 44));
Add("chair", BoxB(-22, -22, 44, 22, 22, 47.5));
foreach (var x in new[] { -20, 20 }) Add("chair", BoxB(x - 1.6, 18.4, 47.5, x + 1.6, 21.6, 92));
foreach (var z in new[] { 62.0, 74.0, 86.0 }) Add("chair", BoxB(-20, 18.8, z, 20, 21.2, z + 4.5));
Add("chair", BoxB(-20, -20.8, 14, 20, -19.2, 17));

mats["plant"] = Mat("leaf", Color.FromArgb(52, 120, 48));
mats["pot"] = Mat("pot", Color.FromArgb(178, 86, 52));
var prof = new PolylineCurve(new[] { new Point3d(0, 0, 0), new Point3d(11, 0, 0), new Point3d(15, 0, 24), new Point3d(16.5, 0, 24), new Point3d(16.5, 0, 28), new Point3d(0, 0, 28) });
Add("pot", Brep.CreateFromRevSurface(RevSurface.Create(prof, new Line(Point3d.Origin, new Point3d(0, 0, 1))), true, true));
var rnd = new Random(3);
for (int i = 0; i < 11; i++)
{
  double ang = i * 2 * Math.PI / 11 + rnd.NextDouble() * 0.3, tilt = 0.35 + rnd.NextDouble() * 0.6, len = 22 + rnd.NextDouble() * 14;
  var dir = new Vector3d(Math.Cos(ang) * Math.Sin(tilt), Math.Sin(ang) * Math.Sin(tilt), Math.Cos(tilt));
  var basePt = new Point3d(0, 0, 26);
  var plane = new Plane(basePt + dir * (len * 0.55), dir, Vector3d.CrossProduct(dir, Vector3d.ZAxis));
  var leaf = new Sphere(Point3d.Origin, 1).ToBrep();
  leaf.Transform(Transform.Scale(Plane.WorldXY, len * 0.5, 4.5, 0.6));
  leaf.Transform(Transform.PlaneToPlane(Plane.WorldXY, plane));
  Add("plant", leaf);
  Add("plant", Pipe(basePt, basePt + dir * (len * 0.15), 0.5));
}

mats["teapot"] = Mat("teapot", Color.FromArgb(40, 70, 150), 0.6);
Add("teapot", Ell(new Point3d(0, 0, 13), 16, 16, 13));
Add("teapot", Ell(new Point3d(0, 0, 26), 3, 3, 2.2));
Add("teapot", Pipe(new Point3d(12, 0, 10), new Point3d(26, 0, 24), 2.2));
Add("teapot", new Torus(new Plane(new Point3d(-17, 0, 15), Vector3d.YAxis), 7, 1.6).ToRevSurface().ToBrep());

mats["lamp"] = Mat("lamp", Color.FromArgb(30, 30, 32), 0.4);
Add("lamp", Brep.CreateFromCylinder(new Cylinder(new Circle(Plane.WorldXY, 11), 2.5), true, true));
Add("lamp", Pipe(new Point3d(0, 0, 2), new Point3d(0, 0, 55), 0.9));
Add("lamp", Pipe(new Point3d(0, 0, 55), new Point3d(18, 0, 68), 0.9));
Add("lamp", Brep.CreateFromCone(new Cone(new Plane(new Point3d(24, 0, 54), new Vector3d(-0.5, 0, 1)), 14, 10), true));

mats["bike"] = Mat("bike", Color.FromArgb(190, 30, 30), 0.4);
mats["tyre"] = Mat("tyre", Color.FromArgb(25, 25, 25));
foreach (var x in new[] { -35.0, 35.0 })
{
  Add("tyre", new Torus(new Plane(new Point3d(x, 0, 32), Vector3d.YAxis), 30, 1.8).ToRevSurface().ToBrep());
  for (int k = 0; k < 12; k++) { double a = k * Math.PI / 6; Add("tyre", Pipe(new Point3d(x, 0, 32), new Point3d(x + 29 * Math.Cos(a), 0, 32 + 29 * Math.Sin(a)), 0.25)); }
}
foreach (var (a, b) in new[] { (P(-35, 32), P(0, 30)), (P(0, 30), P(28, 62)), (P(-35, 32), P(-10, 64)), (P(0, 30), P(-10, 64)), (P(-10, 64), P(28, 62)), (P(28, 62), P(35, 32)), (P(-10, 64), P(-12, 72)), (P(28, 62), P(26, 76)) })
  Add("bike", Pipe(a, b, 1.6));
Add("bike", Ell(new Point3d(-12, 0, 73), 8, 4, 1.5));
Add("bike", Pipe(new Point3d(26, -12, 76), new Point3d(26, 12, 76), 1.1));

mats["figure"] = Mat("figure", Color.FromArgb(150, 150, 155));
Add("figure", Pipe(P(-6, 4), P(-5, 80), 4.5)); Add("figure", Pipe(P(6, 4), P(5, 80), 4.5));
Add("figure", Ell(new Point3d(0, 0, 112), 15, 9, 32));
Add("figure", Pipe(P(-15, 136), P(-24, 92), 3.4)); Add("figure", Pipe(P(15, 136), P(26, 98), 3.4));
Add("figure", Pipe(P(0, 140), P(0, 152), 4.5));
Add("figure", new Sphere(new Point3d(0, 0, 162), 10).ToBrep());

var layout = new Dictionary<string, double> { { "chair", 0 }, { "plant", 400 }, { "pot", 400 }, { "teapot", 800 }, { "lamp", 1200 }, { "bike", 1600 }, { "tyre", 1600 }, { "figure", 2000 } };
var layerOf = new Dictionary<string, string> { { "chair", "chair" }, { "plant", "plant" }, { "pot", "plant" }, { "teapot", "teapot" }, { "lamp", "lamp" }, { "bike", "bike" }, { "tyre", "bike" }, { "figure", "figure" } };
foreach (var kv in groups)
{
  string ln = layerOf[kv.Key];
  int li = doc.Layers.FindByFullPath(ln, -1);
  if (li < 0) li = doc.Layers.Add(new Layer { Name = ln });
  foreach (var b in kv.Value)
  {
    b.Transform(Transform.Translation(layout[kv.Key], 0, 0));
    doc.Objects.AddBrep(b, new ObjectAttributes { LayerIndex = li, MaterialIndex = mats[kv.Key], MaterialSource = ObjectMaterialSource.MaterialFromObject });
  }
}

// ------------------------------------------------------------------ rendering
var view = doc.Views.ActiveView;
view.Maximized = true;
var vp = view.ActiveViewport;
vp.ChangeToPerspectiveProjection(true, 50);
vp.DisplayMode = DisplayModeDescription.FindByName("Rendered");
doc.GroundPlane.Enabled = true; doc.GroundPlane.Altitude = 0; doc.GroundPlane.AutoAltitude = false; doc.GroundPlane.ShadowOnly = false;
doc.RenderSettings.BackgroundStyle = BackgroundStyle.SolidColor;
doc.RenderSettings.Skylight.Enabled = true;
Directory.CreateDirectory(OutDir);

var objs = new[] { "chair", "plant", "teapot", "lamp", "bike", "figure" };
var setups = new[] {
  new { name = "studio", floor = Color.FromArgb(205, 203, 198), bg = Color.FromArgb(232, 232, 230), alt = 45.0, az = 210.0, sky = 0.6, cam = new Vector3d(140, -200, 90) },
  new { name = "warmfloor", floor = Color.FromArgb(150, 105, 70), bg = Color.FromArgb(120, 132, 145), alt = 22.0, az = 120.0, sky = 0.4, cam = new Vector3d(-120, -220, 70) },
  new { name = "dim", floor = Color.FromArgb(118, 118, 120), bg = Color.FromArgb(88, 88, 94), alt = 62.0, az = 300.0, sky = 0.9, cam = new Vector3d(60, -240, 110) },
};
var cap = new ViewCapture { Width = 1024, Height = 768, ScaleScreenItems = false, DrawAxes = false, DrawGrid = false, DrawGridAxes = false };
foreach (var s in setups)
{
  var grm = RenderMaterial.CreateBasicMaterial(new Material { Name = "floor_" + s.name, DiffuseColor = s.floor }, doc);
  doc.RenderMaterials.Add(grm);
  doc.GroundPlane.MaterialInstanceId = grm.Id;
  var sun = doc.Lights.Sun; sun.Enabled = true; sun.ManualControl = true; sun.Altitude = s.alt; sun.Azimuth = s.az;
  doc.RenderSettings.BackgroundColorTop = s.bg;
  doc.RenderSettings.Skylight.ShadowIntensity = s.sky;
  foreach (var name in objs)
  {
    foreach (var o in doc.Objects.GetObjectList(ObjectType.Brep)) doc.Objects.Hide(o, true);
    var bb = BoundingBox.Empty;
    foreach (var o in doc.Objects.FindByLayer(name)) { doc.Objects.Show(o, true); bb.Union(o.Geometry.GetBoundingBox(true)); }
    doc.Objects.UnselectAll();
    var c = bb.Center;
    vp.SetCameraLocations(c, c + s.cam * (bb.Diagonal.Length / 100.0));
    vp.ZoomBoundingBox(bb); vp.Magnify(0.72, false);
    doc.GroundPlane.Enabled = true;
    cap.TransparentBackground = false;
    doc.Views.Redraw();
    cap.CaptureToBitmap(view).Save(Path.Combine(OutDir, $"{s.name}_{name}.png"));
    doc.GroundPlane.Enabled = false;
    cap.TransparentBackground = true;
    doc.Views.Redraw();
    cap.CaptureToBitmap(view).Save(Path.Combine(OutDir, $"{s.name}_{name}_gt.png"));
  }
}
doc.GroundPlane.Enabled = true;

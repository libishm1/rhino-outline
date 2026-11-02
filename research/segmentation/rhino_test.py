# Rhino 7 and 8: Detect=AI, Detect=Shadows, OutlinePicture with AI and OutlineClick (explore/segmentation build).
#   _-RunPythonScript D:\vectorise\work\seg\test_seg.py
import Rhino, System, os
import scriptcontext as sc
from Rhino.Geometry import Plane
OUT = os.path.join(r"D:\vectorise\work\seg", "rh%d" % Rhino.RhinoApp.ExeVersion)
PHOTOS = r"D:\vectorise\work\seg\data\photos"
if not os.path.isdir(OUT):
    os.makedirs(OUT)
log = open(os.path.join(OUT, "log.txt"), "w")
def w(s):
    log.write(str(s) + "\n"); log.flush()
def run(cmd):
    Rhino.RhinoApp.ClearCommandHistoryWindow()
    ok = Rhino.RhinoApp.RunScript(cmd, False)
    for l in Rhino.RhinoApp.CommandHistoryWindowText.splitlines():
        if l.startswith("Outline"):
            w("   " + l)
    return ok
def curves():
    return [o for o in doc.Objects if isinstance(o.Geometry, Rhino.Geometry.Curve)]
def areas(cs):
    return [round(Rhino.Geometry.AreaMassProperties.Compute(c.Geometry).Area, 1) for c in cs]
def clear():
    for o in list(doc.Objects):
        doc.Objects.Delete(o, True)
def capture(name, bb):
    v = doc.Views.ActiveView; vp = v.ActiveViewport
    vp.SetProjection(Rhino.Display.DefinedViewportProjection.Top, "Top", True)
    vp.DisplayMode = Rhino.Display.DisplayModeDescription.FindByName("Shaded")
    vp.ZoomBoundingBox(bb)
    doc.Objects.UnselectAll(); doc.Views.Redraw()
    v.CaptureToBitmap(System.Drawing.Size(600, 600)).Save(os.path.join(OUT, name + ".png"))
def all_bb():
    bb = Rhino.Geometry.BoundingBox.Empty
    for o in doc.Objects:
        bb.Union(o.Geometry.GetBoundingBox(True))
    return bb
doc = sc.doc
try:
    w("Rhino " + str(Rhino.RhinoApp.Version))
    w("load: " + str(Rhino.PlugIns.PlugIn.LoadPlugIn(r"D:\vectorise\RhinoOutline\dist-seg\RhinoOutline.rhp")))
    pi = Rhino.PlugIns.PlugIn.Find(System.Guid("6f1b3c2e-8d7a-4e59-9b21-5c3e7a0d4f18"))
    w("plug-in %s from %s" % (pi.Version, pi.Assembly.Location))
    clear()

    w("1. -Outline Detect=AI (Accurate) on a photo")
    ok = run('-_Outline "%s" _Detect=_AI _Width=128 _PlaceImage=_Yes _Enter' % os.path.join(PHOTOS, "04_shoes.jpg"))
    cs = curves()
    w("   ran=%s curves=%d closed=%s" % (ok, len(cs), [c.Geometry.IsClosed for c in cs]))
    capture("ai_shoes", all_bb())
    clear()

    w("2. OutlinePicture with Detect=AI (remembered from step 1)")
    pic = doc.Objects.AddPictureFrame(Plane.WorldXY, os.path.join(PHOTOS, "11_shoes.jpg"), False, 128, 97.3, True, False)
    ok = run("_OutlinePicture _SelID %s _Enter" % pic)
    cs = curves()
    w("   ran=%s curves=%d closed=%s" % (ok, len(cs), [c.Geometry.IsClosed for c in cs]))
    capture("picture_ai_shoe", doc.Objects.FindId(pic).Geometry.GetBoundingBox(True))
    clear()

    w("3. -Outline Detect=Shadows on a render")
    ok = run(r'-_Outline "D:\vectorise\work\seg\data\render_degraded\warmfloor_chair.png" _Detect=_Shadows _Width=128 _PlaceImage=_Yes _Enter')
    cs = curves()
    w("   ran=%s curves=%d closed=%s" % (ok, len(cs), [c.Geometry.IsClosed for c in cs]))
    capture("shadows_chair", all_bb())
    clear()

    w("4. OutlineClick on a picture (one click on the statue, then Larger)")
    W, H = 128.0, 191.7
    pic = doc.Objects.AddPictureFrame(Plane.WorldXY, os.path.join(PHOTOS, "33_shadows.jpg"), False, W, H, True, False)
    x, y = 877 / 1280.0 * W, (1917 - 862) / 1917.0 * H
    ok = run("_OutlineClick _SelID %s %f,%f,0 _Enter" % (pic, x, y))
    cs = curves()
    w("   one click: ran=%s curves=%d closed=%s area=%s" % (ok, len(cs), [c.Geometry.IsClosed for c in cs], areas(cs)))
    for c in cs:
        doc.Objects.Delete(c, True)
    ok = run("_OutlineClick _SelID %s %f,%f,0 _Larger _Enter" % (pic, x, y))
    cs = curves()
    w("   one click + Larger: ran=%s curves=%d closed=%s area=%s" % (ok, len(cs), [c.Geometry.IsClosed for c in cs], areas(cs)))
    capture("click_statue", doc.Objects.FindId(pic).Geometry.GetBoundingBox(True))
    clear()

    # put the remembered options back to the defaults
    run('-_Outline "%s" _RestoreDefaults _Enter' % r"D:\vectorise\RhinoOutline\examples\tree.png")
    clear()
    w("done")
except Exception as e:
    import traceback
    w("ERROR " + str(e)); w(traceback.format_exc())
log.close()
if Rhino.RhinoApp.ExeVersion == 7:
    Rhino.RhinoApp.RunScript("_-Exit _No", False)

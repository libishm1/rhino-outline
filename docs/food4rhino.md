# food4Rhino listing: Rhino Outline AI

Text and steps for the food4Rhino page of the AI edition.

## Publish

1. Push the Yak package (needs a McNeel account, once per version):

   ```
   "C:\Program Files\Rhino 8\System\Yak.exe" login
   "C:\Program Files\Rhino 8\System\Yak.exe" push rhino-outline-ai-1.2.0-rh7_0-win.yak
   ```

   The package is 209 MB. On a slow connection the push can time out; McNeel support can
   take the file directly in that case.
2. On food4rhino.com, *Submit app*, and link the Yak package *rhino-outline-ai* (a plug-in
   with extra files, here ONNX Runtime and the networks, has to come from Yak).
3. Upload the screenshots: `docs/segmentation/rhino8.jpg`, `docs/segmentation/photos-2.jpg`
   (Creative Commons photos: keep the credit line below), `docs/demo.gif`.

## Name

Rhino Outline AI

## Short description

Photo to outline curve in one command: neural segmentation and Segment Anything find the
object, shadows are ignored, Potrace and alpha shapes give one clean closed curve.

## Description

Rhino Outline turns an image into a closed outline curve: type `Outline`, pick a PNG or JPG,
and the outline appears in the model. Or select pictures already placed with Rhino's
`Picture` command, cropped or not, and the outlines land on them at any position, size or tilt.

The AI edition adds three ways to find the object in a photo, for pictures where the background
is not a clean colour:

* **Detect=AI**: a neural network (IS-Net) finds the main object automatically, ignoring
  shadows, walls and rooms. About 2 seconds per photo on the CPU.
* **OutlineClick**: click on the object you want; MobileSAM, a small Segment Anything model,
  finds it. Click again to add more of it, or step to a larger or smaller selection.
* **Detect=Shadows**: classical background removal that does not count cast shadows as part
  of the object, for objects on a plain or two-tone backdrop. No network.

The object is then vectorised with Potrace (an exact C# port, with four bugs of an older port
fixed), and an alpha shape wraps separate pieces such as loose leaves into one outline. The
outline is a smooth, closed, valid curve that you can extrude, offset or laser cut.

Everything runs on your computer with ONNX Runtime: no GPU, no account, no upload, works
offline. The networks are included in the installer.

The methods were chosen by testing 19 options, classical and neural, on scenes with cast
shadows and on real photos; the comparison is published with the source.

## Commands

| Command | What it does |
|---|---|
| `Outline` | Pick an image file; its outline is added. |
| `-Outline` | Same from the command line, with a live preview of the options: `Detect` (Auto, Transparency, Background, Dark, Shadows, AI), `AIModel` (Fast, Accurate, Best), `Threshold`, `Alpha`, `Holes`, `Output`, `Width`, `PlaceImage`. |
| `OutlinePicture` | Outline pictures already in the model. |
| `OutlineClick` | Click on the object in a picture. |

## Requirements

Rhino 7 or Rhino 8 for Windows (64 bit). Installer 209 MB. `AIModel=Best` (BiRefNet lite) is
optional and downloaded on first use (214 MB).

## Licence and source

Free and open source, GNU GPL v2. Source, test data and the method comparison:
https://github.com/libishm1/rhino-outline

Built on Potrace (Peter Selinger), potrace-js (kilobtye) and Vectorize (Dale Fugier).
Neural networks: IS-Net and U²-Net (Xuebin Qin et al., Apache 2.0), MobileSAM (Chaoning Zhang
et al., Apache 2.0) with the Segment Anything decoder (Meta AI, Apache 2.0), ONNX exports from
rembg (Daniel Gatis, MIT) and Acly/MobileSAM (MIT). ONNX Runtime (Microsoft, MIT).

A smaller standard edition without the AI features (40 KB) is on GitHub for classrooms:
https://github.com/libishm1/rhino-outline/releases/latest

## Screenshot credit (photos-2.jpg)

Photos from Wikimedia Commons, CC BY / CC BY-SA, by Maja Dumat, Roger Culos, Evan-Amos,
Frank C. Müller and Giovanni Dall'Orto; full list at
https://github.com/libishm1/rhino-outline/tree/explore/segmentation/research/segmentation#licences

## Tags

outline, image, trace, vectorize, potrace, alpha shape, background removal, segmentation,
segment anything, SAM, AI, photo, curves, laser cutting

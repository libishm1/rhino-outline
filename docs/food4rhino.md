# food4Rhino listing: Rhino Outline AI

Text and steps for the food4Rhino page of the AI edition.

## Publish

1. Done: `rhino-outline-ai` 1.2.0 is on the Yak server (pushed 2026-11-02). The Yak package
   is 46 MB: it includes U²-Net small and MobileSAM, and IS-Net is downloaded on first use.
   A full 210 MB package was dropped by the server after about a minute, twice. A new version
   is pushed with

   ```
   "C:\Program Files\Rhino 8\System\Yak.exe" login
   "C:\Program Files\Rhino 8\System\Yak.exe" push rhino-outline-ai-<version>-rh7_0-win.yak
   ```

   after `build.ps1 -Dist dist-yak -Models <folder with u2netp and the two MobileSAM files>`.
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

Everything runs on your computer with ONNX Runtime: no GPU, no account, no upload. U²-Net small
and MobileSAM are included; IS-Net (171 MB) is downloaded the first time Detect=AI uses it,
after asking. A 209 MB offline installer with all three is on GitHub.

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

Rhino 7 or Rhino 8 for Windows (64 bit). Package 46 MB, plus IS-Net (171 MB) on first use of
Detect=AI. `AIModel=Best` (BiRefNet lite, 214 MB) is optional and also downloaded on first use.

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

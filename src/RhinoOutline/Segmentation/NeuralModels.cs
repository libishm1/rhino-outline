// RhinoOutline - image to outline curves for Rhino.
// Copyright (C) 2026 RhinoOutline contributors. Licensed under the GNU GPL v2, see LICENSE.

using System;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Rhino;
using Rhino.Input;
using Rhino.Input.Custom;

namespace RhinoOutline.Segmentation
{
  /// <summary>The automatic segmentation network used by Detect=AI.</summary>
  public enum AIModel
  {
    /// <summary>U^2-Net small (u2netp): 4.4 MB, a fraction of a second, coarse.</summary>
    Fast,
    /// <summary>IS-Net (DIS) general use: 171 MB, about 2 s on a CPU.</summary>
    Accurate,
    /// <summary>BiRefNet lite: 214 MB, about 15 to 30 s on a CPU, the most reliable.</summary>
    Best,
  }

  /// <summary>One file of a model: where it comes from and its SHA-256.</summary>
  sealed class ModelFile
  {
    public string Name, Url, Sha256;
    public long Bytes;
  }

  /// <summary>
  /// The networks. The AI edition installs u2netp, IS-Net and MobileSAM in a "models" folder
  /// next to the plug-in. A network that is not there (BiRefNet lite, or all of them in a build
  /// without models) is downloaded the first time it is needed, after the user agrees, into
  /// %LOCALAPPDATA%\RhinoOutline\models, and checked against its SHA-256.
  /// </summary>
  static class NeuralModels
  {
    const string Rembg = "https://github.com/danielgatis/rembg/releases/download/v0.0.0/";
    const string MobileSamRepo = "https://huggingface.co/Acly/MobileSAM/resolve/main/";

    public static readonly ModelFile U2netp = new ModelFile
    {
      Name = "u2netp.onnx", Url = Rembg + "u2netp.onnx", Bytes = 4574861,
      Sha256 = "309c8469258dda742793dce0ebea8e6dd393174f89934733ecc8b14c76f4ddd8",
    };
    public static readonly ModelFile IsNet = new ModelFile
    {
      Name = "isnet-general-use.onnx", Url = Rembg + "isnet-general-use.onnx", Bytes = 178648008,
      Sha256 = "60920e99c45464f2ba57bee2ad08c919a52bbf852739e96947fbb4358c0d964a",
    };
    public static readonly ModelFile BiRefNetLite = new ModelFile
    {
      Name = "BiRefNet-general-bb_swin_v1_tiny-epoch_232.onnx", Url = Rembg + "BiRefNet-general-bb_swin_v1_tiny-epoch_232.onnx", Bytes = 224005088,
      Sha256 = "5600024376f572a557870a5eb0afb1e5961636bef4e1e22132025467d0f03333",
    };
    public static readonly ModelFile MobileSamEncoder = new ModelFile
    {
      Name = "mobile_sam_image_encoder.onnx", Url = MobileSamRepo + "mobile_sam_image_encoder.onnx", Bytes = 28157093,
      Sha256 = "580f5fb648ea1062c0aabc26217aed56921985f03f0cbbd852bba81d760cc749",
    };
    public static readonly ModelFile MobileSamDecoder = new ModelFile
    {
      Name = "sam_mask_decoder_multi.onnx", Url = MobileSamRepo + "sam_mask_decoder_multi.onnx", Bytes = 16496559,
      Sha256 = "8976b90a87ba50a6a72217a5ff994f7d25ce16f2229fcc1ed259e1294c622ffe",
    };

    public static ModelFile For(AIModel model)
    {
      switch (model)
      {
        case AIModel.Fast: return U2netp;
        case AIModel.Best: return BiRefNetLite;
        default: return IsNet;
      }
    }

    /// <summary>Where downloaded networks go.</summary>
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RhinoOutline", "models");

    /// <summary>Where the installer puts the bundled networks: "models" next to the plug-in.</summary>
    public static string BundledFolder => Path.Combine(Path.GetDirectoryName(typeof(NeuralModels).Assembly.Location) ?? "", "models");

    /// <summary>The bundled copy if there is one, else the download location (which may not exist yet).</summary>
    public static string PathOf(ModelFile f)
    {
      string bundled = Path.Combine(BundledFolder, f.Name);
      return File.Exists(bundled) ? bundled : Path.Combine(Folder, f.Name);
    }

    /// <summary>Path of the first file, downloading the missing ones first (with the user's
    /// consent). Null when the user declines or the download fails.</summary>
    public static string Ensure(params ModelFile[] files)
    {
      long missing = 0;
      foreach (ModelFile f in files)
        if (!File.Exists(PathOf(f)))
          missing += f.Bytes;
      if (missing > 0)
      {
        var go = new GetOption();
        go.SetCommandPrompt($"The segmentation network ({missing / 1e6:0.#} MB) is not on this computer yet. Download it to {Folder}?");
        int yes = go.AddOption("Yes");
        go.AddOption("No");
        if (go.Get() != GetResult.Option || go.Option().Index != yes)
          return null;
        Directory.CreateDirectory(Folder);
        foreach (ModelFile f in files)
        {
          if (File.Exists(PathOf(f)))
            continue;
          if (!Download(f, Path.Combine(Folder, f.Name)))
            return null;
        }
      }
      return PathOf(files[0]);
    }

    static bool Download(ModelFile f, string path)
    {
      string part = path + ".part";
      ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
      using (var client = new WebClient())
      {
        bool done = false;
        Exception error = null;
        client.DownloadProgressChanged += (s, e) =>
          Rhino.UI.StatusBar.UpdateProgressMeter((int)(e.BytesReceived * 100 / Math.Max(1, f.Bytes)), true);
        client.DownloadFileCompleted += (s, e) => { error = e.Cancelled ? new OperationCanceledException() : e.Error; done = true; };
        Rhino.UI.StatusBar.ShowProgressMeter(0, 100, "Downloading " + f.Name, true, true);
        RhinoApp.WriteLine($"Outline: downloading {f.Name} ({f.Bytes / 1e6:0.#} MB) from {new Uri(f.Url).Host}... press Esc to cancel.");
        bool escape = false;
        EventHandler onEscape = (s, e) => escape = true;
        RhinoApp.EscapeKeyPressed += onEscape;
        try
        {
          client.DownloadFileAsync(new Uri(f.Url), part);
          while (!done)
          {
            RhinoApp.Wait();
            if (escape)
            {
              client.CancelAsync();
              escape = false;
            }
            System.Threading.Thread.Sleep(30);
          }
        }
        finally
        {
          RhinoApp.EscapeKeyPressed -= onEscape;
          Rhino.UI.StatusBar.HideProgressMeter();
        }
        if (error != null)
        {
          RhinoApp.WriteLine($"Outline: the download of {f.Name} failed ({error.Message}).");
          TryDelete(part);
          return false;
        }
      }
      string hash = Sha256(part);
      if (!string.Equals(hash, f.Sha256, StringComparison.OrdinalIgnoreCase))
      {
        RhinoApp.WriteLine($"Outline: {f.Name} did not match its checksum and was deleted.");
        TryDelete(part);
        return false;
      }
      if (File.Exists(path))
        File.Delete(path);
      File.Move(part, path);
      return true;
    }

    static string Sha256(string path)
    {
      using (var sha = SHA256.Create())
      using (var stream = File.OpenRead(path))
        return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }

    static void TryDelete(string path)
    {
      try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr LoadLibrary(string path);

    static bool nativeLoaded;

    /// <summary>
    /// Loads onnxruntime.dll from the plug-in folder. Windows looks for DLLs next to Rhino.exe,
    /// not next to the plug-in, so the managed wrapper would not find it on its own.
    /// </summary>
    public static void LoadNativeRuntime()
    {
      if (nativeLoaded)
        return;
      string dir = Path.GetDirectoryName(typeof(NeuralModels).Assembly.Location);
      string dll = Path.Combine(dir, "onnxruntime.dll");
      if (File.Exists(dll) && LoadLibrary(dll) == IntPtr.Zero)
        throw new Win32Exception(Marshal.GetLastWin32Error(), "cannot load " + dll);
      nativeLoaded = true;
    }
  }
}

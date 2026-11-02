"""Foreground segmentation methods compared for Rhino Outline.

Every method takes an RGB uint8 image (H, W, 3) and returns a float mask (H, W) in [0, 1]
where 1 is the object. Prompted methods (SAM) also take a prompt dict.

Classical methods need only numpy and OpenCV. The neural ones run ONNX models with
onnxruntime on the CPU; see MODELS for where each file comes from.
"""
import os
import time

import cv2
import numpy as np

MODEL_DIR = os.environ.get("SEG_MODELS", r"D:\vectorise\work\seg\models")

# file, source, licence, size in MB (as downloaded)
MODELS = {
    "u2netp": ("u2netp.onnx", "github.com/danielgatis/rembg releases (U^2-Net small)", "Apache-2.0", 4.4),
    "silueta": ("silueta.onnx", "github.com/danielgatis/rembg releases (U^2-Net pruned)", "Apache-2.0", 43),
    "isnet": ("isnet-general-use.onnx", "github.com/danielgatis/rembg releases (DIS IS-Net)", "Apache-2.0", 171),
    "birefnet_lite": ("BiRefNet-general-bb_swin_v1_tiny-epoch_232.onnx", "github.com/danielgatis/rembg releases (BiRefNet lite)", "MIT", 214),
    "mobilesam": (("mobile_sam_image_encoder.onnx", "sam_mask_decoder_single.onnx", "sam_mask_decoder_multi.onnx"),
                  "huggingface.co/Acly/MobileSAM", "MIT (weights Apache-2.0)", 27 + 16),
    "sam21_tiny": (("sam21t/vision_encoder.onnx", "sam21t/prompt_encoder_mask_decoder.onnx"),
                   "huggingface.co/onnx-community/sam2.1-hiera-tiny-ONNX", "Apache-2.0", 128 + 20),
}

_sessions = {}


def session(file):
    import onnxruntime as ort
    if file not in _sessions:
        opts = ort.SessionOptions()
        opts.log_severity_level = 3
        _sessions[file] = ort.InferenceSession(os.path.join(MODEL_DIR, file), opts, providers=["CPUExecutionProvider"])
    return _sessions[file]


# ---------------------------------------------------------------- classical

def border_median(img, threshold=0.5):
    """Rhino Outline 1.1 'Background' mode: pixels that differ from the median border colour."""
    h, w, _ = img.shape
    border = np.concatenate([img[0], img[-1], img[:, 0], img[:, -1]]).astype(np.int32)
    bg = np.median(border, axis=0)
    diff = np.abs(img.astype(np.int32) - bg).max(axis=2)
    return (diff > (1 - threshold) * 0.2 * 255).astype(np.float32)


def _linear(img):
    x = img.astype(np.float32) / 255.0
    return np.where(x <= 0.04045, x / 12.92, ((x + 0.055) / 1.055) ** 2.4)


def background_model(lin, size=96, iterations=400):
    """Smooth estimate of the empty background from the image border: border pixels whose
    colour is typical for the border are kept and harmonically interpolated inwards, so
    gradients, vignetting and a floor-to-wall change are followed. Returns (H, W, 3) linear."""
    h, w, _ = lin.shape
    s = size / max(h, w)
    sh, sw = max(8, int(round(h * s))), max(8, int(round(w * s)))
    small = cv2.resize(lin, (sw, sh), interpolation=cv2.INTER_AREA)
    known = np.zeros((sh, sw), bool)
    known[0, :] = known[-1, :] = known[:, 0] = known[:, -1] = True
    # Drop border pixels unlike their border neighbours (an object touching the edge).
    ring = small[known]
    med = np.median(ring, axis=0)
    dev = np.abs(np.log((ring + 1e-3) / (med + 1e-3))).max(axis=1)
    lim = max(0.35, np.percentile(dev, 75) * 2.5)
    idx = np.argwhere(known)
    for (y, x), d in zip(idx, dev):
        if d > lim:
            known[y, x] = False
    est = np.where(known[..., None], small, med)
    for _ in range(iterations):
        avg = (np.roll(est, 1, 0) + np.roll(est, -1, 0) + np.roll(est, 1, 1) + np.roll(est, -1, 1)) / 4
        est = np.where(known[..., None], small, avg)
    return cv2.resize(est, (w, h), interpolation=cv2.INTER_CUBIC)


def _clean(mask, min_frac=0.0008):
    m = mask.astype(np.uint8)
    m = cv2.morphologyEx(m, cv2.MORPH_OPEN, np.ones((3, 3), np.uint8))
    m = cv2.morphologyEx(m, cv2.MORPH_CLOSE, np.ones((5, 5), np.uint8))
    n, lab, stats, _ = cv2.connectedComponentsWithStats(m, 8)
    keep = np.zeros(n, bool)
    keep[1:] = stats[1:, cv2.CC_STAT_AREA] >= min_frac * m.size
    return keep[lab]


def shadow_aware(img, color_tol=0.22, chroma_tol=0.10, shadow_min=0.12, return_parts=False):
    """Background subtraction against background_model() that does not count cast shadows:
    a pixel darker than the background by a similar factor in R, G and B (its chromaticity
    preserved) is shadow, not object. Works in linear RGB."""
    lin = _linear(img)
    bg = background_model(lin)
    ratio = (lin + 2e-3) / (bg + 2e-3)
    logr = np.log(ratio)
    mean = logr.mean(axis=2)
    chroma = np.abs(logr - mean[..., None]).max(axis=2)
    lum = np.exp(mean)
    is_bg = (np.abs(mean) < color_tol) & (chroma < chroma_tol * 1.5)
    # Skylight shadows are a little bluish: allow more chroma change as the shadow deepens.
    is_shadow = (lum < 1.0) & (lum > shadow_min) & (chroma < chroma_tol + 0.25 * (1 - lum))
    fg = ~(is_bg | is_shadow)
    fg = cv2.medianBlur(fg.astype(np.uint8), 5).astype(bool)
    fg = _clean(fg)
    if return_parts:
        return fg.astype(np.float32), is_shadow & ~fg, bg
    return fg.astype(np.float32)


def border_palette(lin, k=4, min_share=0.08):
    """Main colours of the image border (linear RGB), as k-means centres that hold at least
    min_share of the border pixels."""
    ring = np.concatenate([lin[:3].reshape(-1, 3), lin[-3:].reshape(-1, 3), lin[:, :3].reshape(-1, 3), lin[:, -3:].reshape(-1, 3)])
    ring = ring[:: max(1, len(ring) // 6000)].astype(np.float32)
    _, labels, centres = cv2.kmeans(np.log(ring + 2e-3), k, None,
                                    (cv2.TERM_CRITERIA_EPS + cv2.TERM_CRITERIA_MAX_ITER, 30, 1e-4), 3, cv2.KMEANS_PP_CENTERS)
    share = np.bincount(labels.ravel(), minlength=k) / len(labels)
    return [np.exp(c) - 2e-3 for c, sh in zip(centres, share) if sh >= min_share]


def shadow_palette(img, color_tol=0.22, chroma_tol=0.10, shadow_min=0.12):
    """shadow_aware() tested against several background colours: the smooth background model
    and each main border colour. A floor seen below a wall, or a backdrop with two tones, is
    then background wherever it appears, and a cast shadow on any of them is shadow."""
    lin = _linear(img)
    palette = border_palette(lin)
    candidates = [background_model(lin)] + [np.broadcast_to(c, lin.shape) for c in palette]
    # Pixels on the edge between two background colours (the line where a floor meets a wall)
    # are a blend of the two: next to both colours, compare them with the nearest blend too.
    def matches(bg):
        logr = np.log((lin + 2e-3) / (bg + 2e-3))
        mean = logr.mean(axis=2)
        return (np.abs(mean) < color_tol) & (np.abs(logr - mean[..., None]).max(axis=2) < chroma_tol * 1.5)
    near = [cv2.dilate(matches(np.broadcast_to(c, lin.shape)).astype(np.uint8), np.ones((7, 7), np.uint8)) > 0 for c in palette]
    blends = []
    for i in range(len(palette)):
        for j in range(i + 1, len(palette)):
            a, b = palette[i], palette[j]
            d = b - a
            t = np.clip(((lin - a) @ d) / max(float(d @ d), 1e-9), 0, 1)
            blends.append((a + t[..., None] * d, near[i] & near[j]))
    is_bg = np.zeros(lin.shape[:2], bool)
    is_shadow = np.zeros(lin.shape[:2], bool)
    for bg in candidates:
        logr = np.log((lin + 2e-3) / (bg + 2e-3))
        mean = logr.mean(axis=2)
        chroma = np.abs(logr - mean[..., None]).max(axis=2)
        lum = np.exp(mean)
        is_bg |= (np.abs(mean) < color_tol) & (chroma < chroma_tol * 1.5)
        is_shadow |= (lum < 1.0) & (lum > shadow_min) & (chroma < chroma_tol + 0.25 * (1 - lum))
    for bg, zone in blends:
        is_bg |= matches(bg) & zone
    fg = ~(is_bg | is_shadow)
    fg = cv2.medianBlur(fg.astype(np.uint8), 5).astype(bool)
    return _clean(fg).astype(np.float32)


def _features(img, work=800):
    """Smoothed image at working size as (log chroma r/g, log chroma b/g, log luminance).
    Chroma is damped in very dark pixels, where it is mostly noise."""
    h, w, _ = img.shape
    s = min(1.0, work / max(h, w))
    small = cv2.resize(img, (int(w * s), int(h * s)), interpolation=cv2.INTER_AREA) if s < 1 else img
    small = cv2.bilateralFilter(small, 7, 25, 5)
    lin = _linear(small) + 4e-3
    lg = np.log(lin)
    lum = lg.mean(axis=2)
    conf = np.sqrt(np.clip(np.exp(lum) / 0.03, 0, 1))
    f = np.dstack([(lg[..., 0] - lg[..., 1]) * conf, (lg[..., 2] - lg[..., 1]) * conf, lum]).astype(np.float32)
    return f, s


def flood_shadow(img, chroma_step=0.045, darken_step=0.6, brighten_step=0.05, palette_tol=0.07):
    """Background grown from the image border, pixel to pixel. A step may darken a lot (into a
    cast shadow, which keeps its chromaticity) but may change chromaticity or brighten only a
    little, so it stops at object edges. Background enclosed by the object (between chair
    slats, inside a handle) is then recovered by growing again from pixels that match the
    colours of the border background."""
    h, w, _ = img.shape
    f, s = _features(img)
    sh, sw = f.shape[:2]
    lo = (chroma_step, chroma_step, darken_step)
    up = (chroma_step, chroma_step, brighten_step)
    filled = np.zeros((sh + 2, sw + 2), np.uint8)
    flags = 4 | cv2.FLOODFILL_MASK_ONLY | (1 << 8)
    step = max(1, min(sh, sw) // 200)
    border = [(x, 0) for x in range(0, sw, step)] + [(x, sh - 1) for x in range(0, sw, step)] + \
             [(0, y) for y in range(0, sh, step)] + [(sw - 1, y) for y in range(0, sh, step)]
    for x, y in border:
        if not filled[y + 1, x + 1]:
            cv2.floodFill(f, filled, (x, y), 0, lo, up, flags)
    bg = filled[1:-1, 1:-1] > 0

    # Enclosed background: seeds inside the remaining area that match a border background colour
    # (allowing them to be darker: shadowed), grown with the same rule but kept inside that area.
    sample = f[bg][:: max(1, bg.sum() // 20000)]
    if len(sample) >= 8:
        k = min(6, len(sample))
        _, _, centres = cv2.kmeans(sample, k, None, (cv2.TERM_CRITERIA_EPS + cv2.TERM_CRITERIA_MAX_ITER, 20, 1e-3), 2, cv2.KMEANS_PP_CENTERS)
        rest = ~bg
        fr = f[rest]
        dch = np.min([np.hypot(fr[:, 0] - c[0], fr[:, 1] - c[1]) + np.maximum(0, fr[:, 2] - c[2]) * 2
                      + np.maximum(0, c[2] - fr[:, 2] - 1.2) for c in centres], axis=0)
        seeds = np.zeros((sh, sw), bool)
        seeds[rest] = dch < palette_tol
        # Only seeds that are not a thin rim next to the border background (anti-aliased edges).
        seeds &= cv2.erode(rest.astype(np.uint8), np.ones((5, 5), np.uint8)) > 0
        inner = np.ones((sh + 2, sw + 2), np.uint8)
        inner[1:-1, 1:-1][rest] = 0
        ys, xs = np.nonzero(seeds)
        for y, x in zip(ys[::7], xs[::7]):
            if not inner[y + 1, x + 1]:
                cv2.floodFill(f, inner, (int(x), int(y)), 0, (palette_tol,) * 2 + (darken_step,), (palette_tol,) * 2 + (brighten_step,), flags)
        bg |= (inner[1:-1, 1:-1] > 0) & rest
    fg = cv2.medianBlur((~bg).astype(np.uint8), 5) > 0
    fg = _clean(fg)
    return cv2.resize(fg.astype(np.uint8), (w, h), interpolation=cv2.INTER_NEAREST).astype(np.float32)


def grabcut(img, seed=None, iterations=5):
    """OpenCV GrabCut. Without a seed it starts from a rectangle 3 % inside the image; with a
    seed mask it starts from that mask (probably-foreground inside, probably-background
    outside, sure background far from it)."""
    h, w, _ = img.shape
    bgr = cv2.cvtColor(img, cv2.COLOR_RGB2BGR)
    s = min(1.0, 640 / max(h, w))
    small = cv2.resize(bgr, (int(w * s), int(h * s)), interpolation=cv2.INTER_AREA) if s < 1 else bgr
    sh, sw = small.shape[:2]
    mask = np.zeros((sh, sw), np.uint8)
    bgd, fgd = np.zeros((1, 65), np.float64), np.zeros((1, 65), np.float64)
    if seed is None:
        m = int(0.03 * max(sh, sw))
        cv2.grabCut(small, mask, (m, m, sw - 2 * m, sh - 2 * m), bgd, fgd, iterations, cv2.GC_INIT_WITH_RECT)
    else:
        sd = cv2.resize(seed.astype(np.uint8), (sw, sh), interpolation=cv2.INTER_NEAREST)
        far = cv2.dilate(sd, np.ones((25, 25), np.uint8)) == 0
        core = cv2.erode(sd, np.ones((9, 9), np.uint8)) > 0
        mask[:] = cv2.GC_PR_BGD
        mask[sd > 0] = cv2.GC_PR_FGD
        mask[core] = cv2.GC_FGD
        mask[far] = cv2.GC_BGD
        if (mask == cv2.GC_FGD).sum() == 0 or (mask == cv2.GC_BGD).sum() == 0:
            return seed.astype(np.float32)
        cv2.grabCut(small, mask, None, bgd, fgd, iterations, cv2.GC_INIT_WITH_MASK)
    fg = ((mask == cv2.GC_FGD) | (mask == cv2.GC_PR_FGD)).astype(np.uint8)
    fg = cv2.resize(fg, (w, h), interpolation=cv2.INTER_NEAREST)
    return _clean(fg > 0).astype(np.float32)


def shadow_grabcut(img):
    """shadow_aware() as the seed for GrabCut, which then follows colour edges."""
    return grabcut(img, seed=shadow_aware(img) > 0.5)


# ---------------------------------------------------------------- salient object networks

def _norm_input(img, size, mean, std, scale_by_max=False):
    x = cv2.resize(img, (size, size), interpolation=cv2.INTER_LANCZOS4).astype(np.float32)
    x = x / max(x.max(), 1e-6) if scale_by_max else x / 255.0
    x = (x - np.array(mean, np.float32)) / np.array(std, np.float32)
    return x.transpose(2, 0, 1)[None].astype(np.float32)


def _minmax(p):
    return (p - p.min()) / max(p.max() - p.min(), 1e-6)


def _salient(img, name, size, mean, std, scale_by_max=False, sigmoid=False):
    s = session(MODELS[name][0])
    out = s.run(None, {s.get_inputs()[0].name: _norm_input(img, size, mean, std, scale_by_max)})[0][0, 0]
    if sigmoid:
        out = 1 / (1 + np.exp(-out))
    out = _minmax(out)
    return cv2.resize(out, (img.shape[1], img.shape[0]), interpolation=cv2.INTER_LINEAR)


IMAGENET = ((0.485, 0.456, 0.406), (0.229, 0.224, 0.225))


def u2netp(img):
    return _salient(img, "u2netp", 320, *IMAGENET, scale_by_max=True)


def silueta(img):
    return _salient(img, "silueta", 320, *IMAGENET, scale_by_max=True)


def isnet(img):
    return _salient(img, "isnet", 1024, (0.5, 0.5, 0.5), (1.0, 1.0, 1.0))


def birefnet_lite(img):
    return _salient(img, "birefnet_lite", 1024, *IMAGENET, sigmoid=True)


# ---------------------------------------------------------------- SAM (prompted)

def _box_of(mask, pad=0.02):
    ys, xs = np.nonzero(mask > 0.5)
    if len(xs) == 0:
        h, w = mask.shape
        return [0, 0, w - 1, h - 1]
    h, w = mask.shape
    p = pad * max(h, w)
    return [max(0, xs.min() - p), max(0, ys.min() - p), min(w - 1, xs.max() + p), min(h - 1, ys.max() + p)]


def pick_largest_inside(cands, slack=0.05):
    """Of SAM's candidate masks for one click, the largest that does not run into the image
    border more than the tightest candidate does. The largest candidate is often the object
    together with the floor or wall around it; that one touches much more of the border."""
    def border(m):
        return np.concatenate([m[0], m[-1], m[:, 0], m[:, -1]]).mean()
    b = [border(m) for m in cands]
    ok = [i for i in range(len(cands)) if b[i] <= min(b) + slack]
    return max(ok, key=lambda i: cands[i].sum())


def mobilesam(img, box=None, points=None, labels=None, pick="iou"):
    """MobileSAM: TinyViT encoder + SAM decoder. box = [x0, y0, x1, y1] and/or points
    [[x, y], ...] with labels (1 object, 0 background), all in image pixels."""
    h, w, _ = img.shape
    enc_file, single_file, multi_file = MODELS["mobilesam"][0]
    s = 1024.0 / max(h, w)
    rs = cv2.resize(img, (int(round(w * s)), int(round(h * s))), interpolation=cv2.INTER_LINEAR).astype(np.float32)
    enc = session(enc_file)
    emb = enc.run(None, {"input_image": rs})[0]
    coords, labs = [], []
    if points is not None:
        coords += [[x * s, y * s] for x, y in points]
        labs += list(labels if labels is not None else [1] * len(points))
    if box is not None:
        coords += [[box[0] * s, box[1] * s], [box[2] * s, box[3] * s]]
        labs += [2, 3]
    else:
        coords.append([0, 0])
        labs.append(-1)
    multi = box is None and points is not None and len(points) == 1
    dec = session(multi_file if multi else single_file)
    masks, iou, _ = dec.run(None, {
        "image_embeddings": emb,
        "point_coords": np.array([coords], np.float32),
        "point_labels": np.array([labs], np.float32),
        "mask_input": np.zeros((1, 1, 256, 256), np.float32),
        "has_mask_input": np.zeros(1, np.float32),
        "orig_im_size": np.array([h, w], np.float32),
    })
    if not multi:
        k = 0
    elif pick == "largest":
        k = pick_largest_inside([masks[0, i] > 0 for i in range(masks.shape[1])])
    else:
        k = int(np.argmax(iou[0]))
    return (masks[0, k] > 0).astype(np.float32)


def sam21_tiny(img, box=None, points=None, labels=None, pick="iou"):
    """SAM 2.1 hiera-tiny (transformers ONNX export). Same prompt convention as mobilesam."""
    h, w, _ = img.shape
    enc_file, dec_file = MODELS["sam21_tiny"][0]
    x = cv2.resize(img, (1024, 1024), interpolation=cv2.INTER_LINEAR).astype(np.float32) / 255.0
    x = ((x - np.array(IMAGENET[0], np.float32)) / np.array(IMAGENET[1], np.float32)).transpose(2, 0, 1)[None]
    enc = session(enc_file)
    e0, e1, e2 = enc.run(None, {"pixel_values": x.astype(np.float32)})
    sx, sy = 1024.0 / w, 1024.0 / h
    feeds = {"image_embeddings.0": e0, "image_embeddings.1": e1, "image_embeddings.2": e2}
    if points is not None:
        feeds["input_points"] = np.array([[[[px * sx, py * sy] for px, py in points]]], np.float32)
        feeds["input_labels"] = np.array([[list(labels if labels is not None else [1] * len(points))]], np.int64)
    else:
        feeds["input_points"] = np.zeros((1, 1, 0, 2), np.float32)
        feeds["input_labels"] = np.zeros((1, 1, 0), np.int64)
    feeds["input_boxes"] = (np.array([[[box[0] * sx, box[1] * sy, box[2] * sx, box[3] * sy]]], np.float32)
                            if box is not None else np.zeros((1, 0, 4), np.float32))
    dec = session(dec_file)
    iou, masks, _ = dec.run(None, feeds)
    k = pick_largest_inside([masks[0, 0, i] > 0 for i in range(masks.shape[2])]) if pick == "largest" else int(np.argmax(iou[0, 0]))
    m = cv2.resize(masks[0, 0, k], (w, h), interpolation=cv2.INTER_LINEAR)
    return (m > 0).astype(np.float32)


# ---------------------------------------------------------------- registry

def _auto_box(img):
    return _box_of(shadow_palette(img))


METHODS = {
    # name: (function(img, gt) -> mask, kind)
    "border_median": (lambda img, gt: border_median(img), "classical"),
    "shadow_aware": (lambda img, gt: shadow_aware(img), "classical"),
    "shadow_palette": (lambda img, gt: shadow_palette(img), "classical"),
    "shadow_palette_grabcut": (lambda img, gt: grabcut(img, seed=shadow_palette(img) > 0.5), "classical"),
    "flood_shadow": (lambda img, gt: flood_shadow(img), "classical"),
    "flood_grabcut": (lambda img, gt: grabcut(img, seed=flood_shadow(img) > 0.5), "classical"),
    "grabcut": (lambda img, gt: grabcut(img), "classical"),
    "shadow_grabcut": (lambda img, gt: shadow_grabcut(img), "classical"),
    "u2netp": (lambda img, gt: u2netp(img), "network"),
    "silueta": (lambda img, gt: silueta(img), "network"),
    "isnet": (lambda img, gt: isnet(img), "network"),
    "birefnet_lite": (lambda img, gt: birefnet_lite(img), "network"),
    # SAM with an automatic box: the box around the shadow_palette mask
    "mobilesam_autobox": (lambda img, gt: mobilesam(img, box=_auto_box(img)), "sam"),
    "sam21_tiny_autobox": (lambda img, gt: sam21_tiny(img, box=_auto_box(img)), "sam"),
    # SAM with a box around the u2netp mask (all-network pipeline)
    "mobilesam_u2netp_box": (lambda img, gt: mobilesam(img, box=_box_of(u2netp(img))), "sam"),
    # SAM with one click on the object (what a user would do in Rhino); the click is the
    # point of the true object farthest from its edge, so only scored data sets have it
    "mobilesam_click": (lambda img, gt: mobilesam(img, points=[click_point(gt)]) if gt is not None else None, "sam-click"),
    "sam21_tiny_click": (lambda img, gt: sam21_tiny(img, points=[click_point(gt)]) if gt is not None else None, "sam-click"),
    # same click, but keep the largest of SAM's candidate masks (the whole object, not a part)
    "mobilesam_click_largest": (lambda img, gt: mobilesam(img, points=[click_point(gt)], pick="largest") if gt is not None else None, "sam-click"),
    "sam21_tiny_click_largest": (lambda img, gt: sam21_tiny(img, points=[click_point(gt)], pick="largest") if gt is not None else None, "sam-click"),
}


def click_point(gt):
    d = cv2.distanceTransform((gt > 0.5).astype(np.uint8), cv2.DIST_L2, 5)
    y, x = np.unravel_index(np.argmax(d), d.shape)
    return [float(x), float(y)]


def run(name, img, gt=None):
    t = time.perf_counter()
    m = METHODS[name][0](img, gt)
    return m, time.perf_counter() - t

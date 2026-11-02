"""Benchmark of the segmentation methods in methods.py.

  python bench.py [--only name1,name2] [--sets render,photos]

Data (not in the repository, see README.md):
  render  Rhino 8 scenes with cast shadows, ground truth from an object-only capture.
          Noise and JPEG compression are added so they behave more like photos.
  photos  Wikimedia Commons photos, no ground truth: scored only by eye. Click prompts are
          points a user would pick on the object.

Writes out/results.csv, out/summary.md and one overlay sheet per image in out/sheets.
"""
import argparse
import csv
import json
import os
import sys
import time

import cv2
import numpy as np

import methods as M

DATA = os.environ.get("SEG_DATA", r"D:\vectorise\work\seg\data")
OUT = os.environ.get("SEG_OUT", r"D:\vectorise\work\seg\out")


def degrade(img, seed):
    rng = np.random.default_rng(seed)
    noisy = np.clip(img.astype(np.float32) + rng.normal(0, 2.5, img.shape), 0, 255).astype(np.uint8)
    ok, enc = cv2.imencode(".jpg", cv2.cvtColor(noisy, cv2.COLOR_RGB2BGR), [cv2.IMWRITE_JPEG_QUALITY, 85])
    return cv2.cvtColor(cv2.imdecode(enc, cv2.IMREAD_COLOR), cv2.COLOR_BGR2RGB)


def load_sets(sets):
    items = []
    if "render" in sets:
        d = os.path.join(DATA, "render")
        for i, f in enumerate(sorted(x for x in os.listdir(d) if x.endswith(".png") and not x.endswith("_gt.png"))):
            img = cv2.cvtColor(cv2.imread(os.path.join(d, f)), cv2.COLOR_BGR2RGB)
            gt = cv2.imread(os.path.join(d, f[:-4] + "_gt.png"), cv2.IMREAD_UNCHANGED)[..., 3] > 127
            items.append(dict(set="render", name=f[:-4], img=degrade(img, i), gt=gt.astype(np.float32), click=None))
    if "photos" in sets:
        spec = json.load(open(os.path.join(DATA, "photos", "photos.json")))
        for p in spec:
            img = cv2.cvtColor(cv2.imread(os.path.join(DATA, "photos", p["file"])), cv2.COLOR_BGR2RGB)
            items.append(dict(set="photos", name=p["file"][:-4], img=img, gt=None, click=p.get("click")))
    return items


def fill_holes(m):
    m = m.astype(np.uint8)
    h, w = m.shape
    pad = np.zeros((h + 2, w + 2), np.uint8)
    pad[1:-1, 1:-1] = m
    ff = pad.copy()
    cv2.floodFill(ff, np.zeros((h + 4, w + 4), np.uint8), (0, 0), 1)
    return (pad | (1 - ff))[1:-1, 1:-1] > 0


def iou(a, b):
    u = (a | b).sum()
    return float((a & b).sum() / u) if u else 1.0


def boundary_f(a, b, tol):
    def edge(m):
        m = m.astype(np.uint8)
        return (m - cv2.erode(m, np.ones((3, 3), np.uint8))) > 0
    ea, eb = edge(a), edge(b)
    if ea.sum() == 0 or eb.sum() == 0:
        return 0.0
    da = cv2.distanceTransform((~ea).astype(np.uint8), cv2.DIST_L2, 3)
    db = cv2.distanceTransform((~eb).astype(np.uint8), cv2.DIST_L2, 3)
    p = (db[ea] <= tol).mean()
    r = (da[eb] <= tol).mean()
    return float(2 * p * r / (p + r)) if p + r else 0.0


def overlay(img, mask, label, click=None):
    vis = img.copy()
    m = mask.astype(np.uint8)
    vis[m == 0] = (vis[m == 0] * 0.45 + np.array([255, 255, 255]) * 0.2).astype(np.uint8)
    cs, _ = cv2.findContours(m, cv2.RETR_LIST, cv2.CHAIN_APPROX_NONE)
    cv2.drawContours(vis, cs, -1, (230, 20, 30), max(2, img.shape[1] // 300))
    if click is not None:
        cv2.circle(vis, (int(click[0]), int(click[1])), max(6, img.shape[1] // 80), (255, 230, 0), -1)
    s = 360 / max(vis.shape[:2])
    vis = cv2.resize(vis, (int(vis.shape[1] * s), int(vis.shape[0] * s)), interpolation=cv2.INTER_AREA)
    tile = np.full((400, 380, 3), 255, np.uint8)
    y0, x0 = 32 + (360 - vis.shape[0]) // 2, (380 - vis.shape[1]) // 2
    tile[y0:y0 + vis.shape[0], x0:x0 + vis.shape[1]] = vis
    cv2.putText(tile, label, (6, 22), cv2.FONT_HERSHEY_SIMPLEX, 0.5, (20, 20, 20), 1, cv2.LINE_AA)
    return tile


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--only", default="")
    ap.add_argument("--sets", default="render,photos")
    ap.add_argument("--merge", action="store_true", help="keep results.csv rows of methods not run now")
    args = ap.parse_args()
    names = [n for n in M.METHODS if not args.only or n in args.only.split(",")]
    os.makedirs(os.path.join(OUT, "sheets"), exist_ok=True)
    os.makedirs(os.path.join(OUT, "masks"), exist_ok=True)
    items = load_sets(args.sets.split(","))
    rows = []
    old = []
    if args.merge and os.path.exists(os.path.join(OUT, "results.csv")):
        for r in csv.DictReader(open(os.path.join(OUT, "results.csv"))):
            if r["method"] in names:
                continue
            for k in ("seconds", "iou", "iou_filled", "bf_filled"):
                if r.get(k) not in (None, ""):
                    r[k] = float(r[k])
                else:
                    r.pop(k, None)
            old.append(r)
    for it in items:
        img, gt = it["img"], it["gt"]
        tiles = [overlay(img, gt > 0.5 if gt is not None else np.ones(img.shape[:2], bool), "image" + (" + truth" if gt is not None else ""))]
        for n in names:
            kind = M.METHODS[n][1]
            click = None
            if kind == "sam-click":
                click = M.click_point(gt) if gt is not None else it["click"]
                if click is None:
                    continue
                t = time.perf_counter()
                fn = M.mobilesam if n.startswith("mobilesam") else M.sam21_tiny
                m = fn(img, points=[click], pick="largest" if n.endswith("largest") else "iou")
                dt = time.perf_counter() - t
            else:
                m, dt = M.run(n, img, gt)
            m = m > 0.5
            cv2.imwrite(os.path.join(OUT, "masks", f"{it['name']}__{n}.png"), m.astype(np.uint8) * 255)
            row = dict(set=it["set"], image=it["name"], method=n, kind=kind, seconds=round(dt, 3))
            label = f"{n}  {dt:.1f}s"
            if gt is not None:
                g = gt > 0.5
                fm, fg = fill_holes(m), fill_holes(g)
                tol = max(2.0, img.shape[1] / 512)
                row.update(iou=round(iou(m, g), 4), iou_filled=round(iou(fm, fg), 4), bf_filled=round(boundary_f(fm, fg, tol), 4))
                label += f"  IoU {row['iou']:.2f} / filled {row['iou_filled']:.2f}"
            rows.append(row)
            tiles.append(overlay(img, m, label, click))
            print(f"{it['name']:24s} {label}", flush=True)
        cols = 5
        while len(tiles) % cols:
            tiles.append(np.full_like(tiles[0], 255))
        sheet = np.vstack([np.hstack(tiles[i:i + cols]) for i in range(0, len(tiles), cols)])
        sub = "sheets_" + "_".join(names) if args.only else "sheets"
        os.makedirs(os.path.join(OUT, sub), exist_ok=True)
        cv2.imwrite(os.path.join(OUT, sub, it["name"] + ".jpg"), cv2.cvtColor(sheet, cv2.COLOR_RGB2BGR), [cv2.IMWRITE_JPEG_QUALITY, 85])
    rows = old + rows
    names = [n for n in M.METHODS if any(r["method"] == n for r in rows)]

    with open(os.path.join(OUT, "results.csv"), "w", newline="") as f:
        keys = ["set", "image", "method", "kind", "seconds", "iou", "iou_filled", "bf_filled"]
        w = csv.DictWriter(f, keys)
        w.writeheader()
        w.writerows(rows)

    lines = ["| Method | Kind | Model MB | IoU | IoU, holes filled | Boundary F, holes filled | Worst IoU, filled | Seconds per image (CPU) |",
             "|---|---|---|---|---|---|---|---|"]
    for n in names:
        r = [x for x in rows if x["method"] == n and "iou" in x]
        t = [x["seconds"] for x in rows if x["method"] == n]
        if not t:
            continue
        mb = ""
        for key in ("u2netp", "silueta", "isnet", "birefnet_lite", "mobilesam", "sam21_tiny"):
            if n.startswith(key):
                mb = str(M.MODELS[key][3])
        if n.startswith("mobilesam_u2netp"):
            mb = str(M.MODELS["mobilesam"][3] + M.MODELS["u2netp"][3])
        if r:
            lines.append(f"| {n} | {M.METHODS[n][1]} | {mb or '0'} | {np.mean([x['iou'] for x in r]):.3f} | {np.mean([x['iou_filled'] for x in r]):.3f} | "
                         f"{np.mean([x['bf_filled'] for x in r]):.3f} | {min(x['iou_filled'] for x in r):.3f} | {np.median(t):.2f} |")
        else:
            lines.append(f"| {n} | {M.METHODS[n][1]} | {mb or '0'} | | | | | {np.median(t):.2f} |")
    open(os.path.join(OUT, "summary.md"), "w").write("\n".join(lines) + "\n")
    print("\n".join(lines))


if __name__ == "__main__":
    sys.exit(main())

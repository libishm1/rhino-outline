"""Draws the example images used in the README (needs Pillow: pip install pillow).

tree.png            tree on a transparent background
tree_white.png      the same tree on a white background
tree.jpg            the same tree as a JPG (no transparency, white background)
"""
import math
import random
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

HERE = Path(__file__).parent
W, H = 1400, 1800


def tree() -> Image.Image:
    rnd = random.Random(7)
    img = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    # trunk with two branches
    trunk = [(610, 1780), (650, 1300), (560, 980), (600, 960), (690, 1180), (700, 900),
             (740, 900), (750, 1170), (860, 950), (900, 975), (780, 1310), (820, 1780)]
    d.polygon(trunk, fill=(120, 72, 38, 255))
    for i in range(14):
        x = 640 + i * 11
        d.line([(x, 1760), (x + rnd.randint(-25, 25), 1350)], fill=(95, 55, 28, 255), width=4)

    # foliage: clusters of overlapping blobs
    greens = [(46, 125, 50), (67, 160, 71), (102, 187, 106), (56, 142, 60)]
    centres = [(700, 520, 360), (420, 700, 230), (980, 690, 240), (560, 380, 220), (860, 360, 230),
               (700, 820, 260), (330, 520, 170), (1080, 500, 170)]
    for cx, cy, r in centres:
        for _ in range(26):
            a = rnd.uniform(0, 2 * math.pi)
            dist = rnd.uniform(0, r * 0.8)
            br = rnd.uniform(r * 0.25, r * 0.45)
            x, y = cx + dist * math.cos(a), cy + dist * math.sin(a)
            col = greens[rnd.randrange(len(greens))] + (255,)
            d.ellipse([x - br, y - br, x + br, y + br], fill=col)

    # a few leaves floating free, so the alpha shape has something to bridge
    for x, y in [(240, 960), (1180, 980), (1230, 820)]:
        d.ellipse([x - 28, y - 18, x + 28, y + 18], fill=(67, 160, 71, 255))

    # soft anti-aliased edge like real clip art
    alpha = img.getchannel("A").filter(ImageFilter.GaussianBlur(1.2))
    img.putalpha(alpha)
    return img


def main() -> None:
    t = tree()
    t.save(HERE / "tree.png")
    white = Image.new("RGB", t.size, (255, 255, 255))
    white.paste(t, mask=t.getchannel("A"))
    white.save(HERE / "tree_white.png")
    white.save(HERE / "tree.jpg", quality=85)
    print("wrote tree.png, tree_white.png, tree.jpg")


if __name__ == "__main__":
    main()

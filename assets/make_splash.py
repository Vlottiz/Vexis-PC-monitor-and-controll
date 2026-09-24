"""
Generates splash.gif — the Vexis loading-screen animation.

Edit the settings below and run:   python assets/make_splash.py
(needs Pillow: pip install pillow). The GIF is written to the project root,
where build-installer.bat picks it up. You can also skip this script and
drop in any 440x220 GIF/PNG of your own named splash.gif (or splash.png).
"""
import math, os
from PIL import Image, ImageDraw, ImageFilter, ImageFont

# ── Settings ──────────────────────────────────────────────────────────────────
W, H      = 440, 220
BG        = (10, 8, 8)
GOLD      = (255, 204, 0)
CYAN      = (0, 255, 204)
DIM       = (170, 119, 0)
TITLE     = "VEXIS"
SUBTITLE  = "HARDWARE MONITORING"
FRAMES    = 36          # one loop
FRAME_MS  = 40          # 36 x 40 ms = 1.44 s loop
FONT_DIRS = ["/usr/share/fonts/truetype/dejavu", "C:/Windows/Fonts"]
TITLE_FONT = ["DejaVuSansMono-Bold.ttf", "consolab.ttf"]
SUB_FONT   = ["DejaVuSansMono.ttf", "consola.ttf"]
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "splash.gif")

def font(names, size):
    for d in FONT_DIRS:
        for n in names:
            p = os.path.join(d, n)
            if os.path.exists(p):
                return ImageFont.truetype(p, size)
    return ImageFont.load_default()

f_title, f_sub = font(TITLE_FONT, 58), font(SUB_FONT, 12)

def frame(i):
    t = i / FRAMES                                  # 0..1 around the loop
    img = Image.new("RGB", (W, H), BG)

    # Title with a soft glow that breathes
    glow = Image.new("RGB", (W, H), BG)
    gd = ImageDraw.Draw(glow)
    tw = gd.textlength(TITLE, font=f_title)
    tx, ty = (W - tw) / 2, 38
    pulse = 0.55 + 0.45 * (0.5 - 0.5 * math.cos(t * 2 * math.pi))
    gd.text((tx, ty), TITLE, font=f_title, fill=tuple(int(c * pulse) for c in GOLD))
    img = Image.blend(img, glow.filter(ImageFilter.GaussianBlur(9)), 0.9)
    d = ImageDraw.Draw(img)
    d.text((tx, ty), TITLE, font=f_title, fill=GOLD)

    # Light sweep across the title
    sweep = Image.new("L", (W, H), 0)
    sd = ImageDraw.Draw(sweep)
    sx = -80 + (W + 160) * t
    sd.polygon([(sx, ty - 5), (sx + 26, ty - 5), (sx + 6, ty + 75), (sx - 20, ty + 75)], fill=150)
    sweep = sweep.filter(ImageFilter.GaussianBlur(6))
    mask = Image.new("L", (W, H), 0)
    ImageDraw.Draw(mask).text((tx, ty), TITLE, font=f_title, fill=255)
    from PIL import ImageChops
    img.paste((255, 245, 200), (0, 0), ImageChops.multiply(sweep, mask))

    # Subtitle
    sw = d.textlength(SUBTITLE, font=f_sub)
    d.text(((W - sw) / 2, 108), SUBTITLE, font=f_sub, fill=DIM)

    # Accent line (gold → cyan → gold)
    for x in range(40, W - 40):
        k = (x - 40) / (W - 80)
        fade = math.sin(k * math.pi)
        c = [int((GOLD[j] * (1 - abs(k - .5) * 2) + CYAN[j] * abs(k - .5) * 2) * fade * 0.6) for j in range(3)]
        d.point((x, 132), fill=tuple(c))

    # Loading bar segments that chase along
    n, bw, gap = 12, 14, 5
    total = n * bw + (n - 1) * gap
    x0, y0 = (W - total) / 2, 162
    for k in range(n):
        phase = (t * n - k) % n
        lvl = max(0.12, 1 - phase / 4) if phase < 4 else 0.12
        c = tuple(int(BG[j] + (GOLD[j] - BG[j]) * lvl) for j in range(3))
        d.rounded_rectangle([x0 + k * (bw + gap), y0, x0 + k * (bw + gap) + bw, y0 + 6], radius=2, fill=c)
    return img

frames = [frame(i) for i in range(FRAMES)]
frames[0].save(OUT, save_all=True, append_images=frames[1:], duration=FRAME_MS, loop=0, optimize=True)
print("wrote", os.path.normpath(OUT), os.path.getsize(OUT), "bytes")

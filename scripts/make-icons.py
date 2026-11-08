"""Generate Windows and web icons from one vector drawing. Requires Pillow."""

from pathlib import Path
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[1]
WEB = ROOT / "paperdesk-web" / "public"
DESKTOP = ROOT / "PaperDesk.Desktop"
SVG = '''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 128 128">
  <title>PaperDesk — folded paper</title>
  <rect x="4" y="4" width="120" height="120" rx="27" fill="#242630"/>
  <path d="M24 50 34 103 86 94 76 41Z" fill="#9f89c6"/>
  <path d="M34 39 38 96 98 90 94 33Z" fill="#a8cfb9"/>
  <path d="M45 23H94Q102 23 102 31V71L82 91H45Q37 91 37 83V31Q37 23 45 23Z" fill="#f4dea0"/>
  <path d="M82 91V77Q82 71 88 71H102Z" fill="#c89450"/>
  <path d="M51 44H87M51 57H77" fill="none" stroke="#6c4d32" stroke-width="5" stroke-linecap="round"/>
</svg>
'''

SCALE = 8
image = Image.new("RGBA", (128 * SCALE, 128 * SCALE))
draw = ImageDraw.Draw(image)


def polygon(points, color):
    draw.polygon([(x * SCALE, y * SCALE) for x, y in points], fill=color)


draw.rounded_rectangle((4*SCALE, 4*SCALE, 124*SCALE, 124*SCALE), 27*SCALE, fill="#242630")
polygon([(24, 50), (34, 103), (86, 94), (76, 41)], "#9f89c6")
polygon([(34, 39), (38, 96), (98, 90), (94, 33)], "#a8cfb9")
polygon([(45, 23), (94, 23), (99, 25), (102, 31), (102, 71), (82, 91),
         (45, 91), (40, 89), (37, 83), (37, 31), (40, 25)], "#f4dea0")
polygon([(82, 91), (82, 77), (84, 73), (88, 71), (102, 71)], "#c89450")
for x, y, right in [(51, 44, 87), (51, 57, 77)]:
    draw.rounded_rectangle(((x-2.5)*SCALE, (y-2.5)*SCALE, (right+2.5)*SCALE, (y+2.5)*SCALE),
                           2.5*SCALE, fill="#6c4d32")

WEB.joinpath("icon.svg").write_text(SVG, encoding="utf-8")
for size in (192, 512):
    # PWA maskable icons keep the same mark inside the platform's safe area.
    inset = round(size * .10) if size == 512 else 0
    bg = Image.new("RGBA", (size, size), "#242630" if inset else (0, 0, 0, 0))
    mark = image.resize((size-2*inset, size-2*inset), Image.Resampling.LANCZOS)
    bg.alpha_composite(mark, (inset, inset))
    bg.save(WEB / f"icon-{size}.png", optimize=True)

image.resize((256, 256), Image.Resampling.LANCZOS).save(
    DESKTOP / "paperdesk.ico", sizes=[(s, s) for s in (16, 20, 24, 32, 48, 64, 128, 256)]
)
DESKTOP.joinpath("Assets").mkdir(exist_ok=True)
image.resize((64, 64), Image.Resampling.LANCZOS).save(DESKTOP / "Assets" / "mark.png", optimize=True)

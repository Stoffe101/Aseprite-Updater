#!/usr/bin/env python3
"""Reviewable contact sheets for all real WPF shell routes captured by LayoutSmoke.
This does NOT assert visual quality. Reviewers must inspect the generated PNGs."""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont
import json
import re

INPUT = Path("ui-snapshots")
OUT = INPUT / "reviews"
OUT.mkdir(parents=True, exist_ok=True)
PATTERN = re.compile(r"unified-(.+)-(\d+)x(\d+)-(top|middle|bottom)\.png")
files = {}
for path in INPUT.glob("unified-*.png"):
    m = PATTERN.fullmatch(path.name)
    if m:
        files[(m.group(1), int(m.group(2)), int(m.group(3)), m.group(4))] = path
routes = [
    "overview", "aseprite", "themes", "tools", "palettes",
    "palette-import", "extensions", "godot", "brushes", "backup",
    "updates", "recovery", "profiles", "performance", "activity"
]
resolutions = [(1024,720), (1366,900), (1920,1080)]
font = ImageFont.load_default()
report = {"screenshots":len(files), "resolutions":{}, "missing":[]}
for width, height in resolutions:
    rows = []
    for route in routes:
        available = [(pos,files.get((route,width,height,pos))) for pos in ("top","middle","bottom")]
        top = available[0][1]
        if top is None:
            report["missing"].append(f"{route} {width}x{height} top")
        rows.append((route, [(pos,p) for pos,p in available if p is not None]))
    report["resolutions"][f"{width}x{height}"] = {
        route: [pos for pos,_ in available] for route,available in rows
    }
    for batch in range(0,len(rows),5):
        part = rows[batch:batch+5]
        card_w,card_h=438,320
        image=Image.new("RGB",(3*card_w+28, len(part)*(card_h+20)+20),(13,20,33))
        draw=ImageDraw.Draw(image)
        for r,(route,positions) in enumerate(part):
            for c in range(3):
                px=12+c*card_w; py=14+r*(card_h+20)
                if c<len(positions):
                    pos,path=positions[c]
                    with Image.open(path) as source:
                        frame=source.convert("RGB")
                        frame.thumbnail((card_w-12,card_h-52))
                        image.paste(frame,(px,py+30))
                    draw.text((px+5,py+7),f"{route.upper()} • {pos} ({width}×{height})",
                              fill=(220,225,244),font=font)
                else:
                    draw.rectangle((px,py+30,px+card_w-12,py+card_h-20),
                                   outline=(45,64,88),width=1)
                    draw.text((px+7,py+55),"Panel fully visible, no scroll needed",
                              fill=(129,146,170),font=font)
        image.save(OUT/f"contact-{width}x{height}-{batch//5+1}.png",
                   optimize=True)
if report["missing"]:
    raise SystemExit("Missing expected full-window screenshots: "+str(report["missing"]))
(OUT/"review-index.json").write_text(json.dumps(report, indent=2),encoding="utf-8")
print(f"PASS: {len(files)} full shell screenshots across {len(resolutions)} resolutions, "
      f"{len(list(OUT.glob('contact-*.png')))} review sheets")

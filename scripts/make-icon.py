#!/usr/bin/env python3
"""Vygeneruje Resources/AppIcon.icns (dvoupanelová ikona). Vyžaduje Pillow."""
import io, struct
from PIL import Image, ImageDraw

S = 2048  # kreslí se ve dvojnásobném rozlišení kvůli vyhlazení

def draw():
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    # pozadí: zaoblený čtverec s přechodem
    m = int(S * 0.055)
    grad = Image.new("RGBA", (S, S))
    gp = grad.load()
    for y in range(S):
        t = y / S
        c = (int(38 + 20 * t), int(86 - 26 * t), int(190 - 50 * t), 255)
        for x in range(S):
            gp[x, y] = c
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle([m, m, S - m, S - m], radius=int(S * 0.22), fill=255)
    img.paste(grad, (0, 0), mask)

    d = ImageDraw.Draw(img)
    pw, ph = int(S * 0.32), int(S * 0.58)
    gap = int(S * 0.045)
    x0 = (S - (2 * pw + gap)) // 2
    y0 = int(S * 0.2)
    for i in range(2):
        x = x0 + i * (pw + gap)
        d.rounded_rectangle([x, y0, x + pw, y0 + ph], radius=int(S * 0.04), fill=(247, 249, 252, 255))
        d.rounded_rectangle([x, y0, x + pw, y0 + int(S * 0.07)], radius=int(S * 0.04),
                            fill=(255, 190, 60, 255) if i == 0 else (120, 200, 140, 255))
        d.rectangle([x, y0 + int(S * 0.04), x + pw, y0 + int(S * 0.07)],
                    fill=(255, 190, 60, 255) if i == 0 else (120, 200, 140, 255))
        # řádky seznamu souborů
        for r in range(7):
            ry = y0 + int(S * 0.12) + r * int(S * 0.062)
            w = int(pw * (0.78 - 0.07 * ((r * 3 + i) % 4)))
            col = (60, 110, 210, 255) if r == 2 else (170, 180, 200, 255)
            if r == 2:
                d.rounded_rectangle([x + int(S*0.015), ry - int(S*0.012), x + pw - int(S*0.015), ry + int(S*0.04)],
                                    radius=int(S*0.01), fill=(205, 222, 250, 255))
            d.rounded_rectangle([x + int(S * 0.03), ry, x + int(S * 0.03) + w, ry + int(S * 0.026)],
                                radius=int(S * 0.013), fill=col)
    # šipka mezi panely
    ay = y0 + ph + int(S * 0.055)
    cx = S // 2
    d.polygon([(cx - int(S*0.1), ay), (cx + int(S*0.04), ay), (cx + int(S*0.04), ay - int(S*0.04)),
               (cx + int(S*0.12), ay + int(S*0.025)), (cx + int(S*0.04), ay + int(S*0.09)),
               (cx + int(S*0.04), ay + int(S*0.05)), (cx - int(S*0.1), ay + int(S*0.05))],
              fill=(255, 255, 255, 235))
    return img

base = draw()
types = {"ic07": 128, "ic08": 256, "ic09": 512, "ic10": 1024}
chunks = b""
for code, size in types.items():
    buf = io.BytesIO()
    base.resize((size, size), Image.LANCZOS).save(buf, "PNG")
    data = buf.getvalue()
    chunks += code.encode() + struct.pack(">I", len(data) + 8) + data
with open("Resources/AppIcon.icns", "wb") as f:
    f.write(b"icns" + struct.pack(">I", len(chunks) + 8) + chunks)
base.resize((512, 512), Image.LANCZOS).save("Resources/AppIcon-preview.png")
print("OK")

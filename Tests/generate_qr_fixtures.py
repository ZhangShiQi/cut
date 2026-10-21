"""Independent QR fixtures generated with ReportLab, not the production decoder.
Requires reportlab and Pillow. Run from the repository root.
"""
from pathlib import Path
from PIL import Image, ImageDraw, ImageOps
from reportlab.graphics.barcode.qrencoder import QRCode, QRErrorCorrectLevel

OUT = Path(__file__).parent / "Fixtures"
OUT.mkdir(exist_ok=True)

def qr(text, scale=6):
    code = QRCode(None, QRErrorCorrectLevel.M)
    code.addData(text)
    code.make()
    n = code.getModuleCount()
    image = Image.new("RGB", ((n + 8) * scale, (n + 8) * scale), "white")
    draw = ImageDraw.Draw(image)
    for y in range(n):
        for x in range(n):
            if code.isDark(y, x):
                draw.rectangle(((x + 4) * scale, (y + 4) * scale,
                                (x + 5) * scale - 1, (y + 5) * scale - 1), fill="black")
    return image

url = qr("https://example.com/cut-test?source=qr&value=42")
text = qr("Cut 二维码测试\n第二行：你好，世界！")
url.save(OUT / "url.png")
text.save(OUT / "unicode.png")
url.rotate(90, expand=True).save(OUT / "rotated.png")
ImageOps.invert(url).save(OUT / "inverted.png")
qr("small QR", scale=1).save(OUT / "small.png")
Image.new("RGB", (640, 480), "white").save(OUT / "blank.png")
canvas = Image.new("RGB", (1000, 760), "#eff4f9")
canvas.paste(url, (30, 30))
canvas.paste(text, (530, 30))
canvas.paste(ImageOps.invert(qr("https://example.org/second")), (30, 400))
canvas.paste(url, (530, 400))
canvas.save(OUT / "multiple.png")
print(f"Generated {len(list(OUT.glob('*.png')))} fixtures in {OUT}")

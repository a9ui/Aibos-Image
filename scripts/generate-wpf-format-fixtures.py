from pathlib import Path
import sys

from PIL import Image, features


def main() -> int:
    if len(sys.argv) != 2:
        raise SystemExit("usage: generate-wpf-format-fixtures.py OUTPUT_DIRECTORY")

    output = Path(sys.argv[1]).resolve()
    output.mkdir(parents=True, exist_ok=True)
    if not features.check("webp") or not features.check("avif"):
        raise RuntimeError("Pillow WebP and AVIF encoders are required for the format verifier")

    image = Image.new("RGB", (64, 48), (52, 152, 219))
    fixtures = {
        "format-png.png": "PNG",
        "format-jpeg.jpg": "JPEG",
        "format-webp.webp": "WEBP",
        "format-gif.gif": "GIF",
        "format-avif.avif": "AVIF",
        "format-bmp.bmp": "BMP",
        "format-tiff.tiff": "TIFF",
    }
    for name, image_format in fixtures.items():
        image.save(output / name, format=image_format)
    (output / "unsupported.txt").write_text("not an image", encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

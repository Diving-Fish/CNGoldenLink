"""Generate the Chinese Dialog supplement on Windows using pinned BMFont assets."""
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import unicodedata
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parent.parent
ASSETS = ROOT / "tools/fonts"
OUTPUT = ROOT / "artifacts/fonts"


def required_characters(text):
    return {ord(c) for c in text if unicodedata.category(c) != "Cc" and c != "\ufeff"}


def font_ids(path):
    return {int(c.attrib["id"]) for c in ET.parse(path).findall("chars/char")}


def validate_font(path, required):
    font = ET.parse(path)
    missing = required - font_ids(path)
    if missing:
        raise ValueError("Missing generated glyphs: " + ", ".join(f"U+{c:04X}" for c in sorted(missing)))
    pages = {}
    for page in font.findall("pages/page"):
        name = page.attrib["file"]
        if Path(name).name != name or "/" in name or "\\" in name or not name.endswith(".png"):
            raise ValueError(f"Invalid font page: {name}")
        image = path.parent / name
        if not image.is_file() or image.read_bytes()[:8] != b"\x89PNG\r\n\x1a\n":
            raise ValueError(f"Missing or invalid PNG: {name}")
        pages[int(page.attrib["id"])] = image
    for glyph in font.findall("chars/char"):
        if int(glyph.attrib["page"]) not in pages:
            raise ValueError("Glyph references an unknown page")
        if int(glyph.attrib["id"]) in required and (int(glyph.attrib["width"]) <= 0 or int(glyph.attrib["height"]) <= 0):
            raise ValueError("Empty generated glyph")
    return list(pages.values())


def generate():
    for name, digest in json.loads((ASSETS / "sha256.json").read_text()).items():
        if hashlib.sha256((ASSETS / name).read_bytes()).hexdigest() != digest:
            raise ValueError(f"Font build asset checksum mismatch: {name}")
    required = required_characters((ROOT / "Dialog/Simplified Chinese.txt").read_text(encoding="utf-8-sig"))
    missing = required - font_ids(ASSETS / "chinese.fnt")
    OUTPUT.mkdir(parents=True, exist_ok=True)
    # Only remove this generator's outputs, never the source Dialog directory.
    for old in [OUTPUT / "chinese.fnt", *OUTPUT.glob("cngoldenlink_chinese_*.png")]:
        old.unlink(missing_ok=True)
    if not missing:
        print("Chinese Dialog needs no supplemental glyphs.")
        return
    with tempfile.TemporaryDirectory(prefix="cngoldenlink-font-") as temp:
        work = Path(temp)
        for name in ("chinese.bmfc", "Noto Sans CJK SC Medium.otf"):
            shutil.copyfile(ASSETS / name, work / name)
        (work / "missing.txt").write_text("".join(map(chr, sorted(missing))), encoding="utf-8-sig")
        # Relative arguments also work with BMFont versions whose CLI uses ANSI paths.
        subprocess.run([str(ASSETS / "bmfont64.exe"), "-c", "chinese.bmfc", "-t", "missing.txt",
                        "-o", "cngoldenlink_chinese.fnt"], cwd=work, check=True,
                       timeout=120, creationflags=subprocess.CREATE_NO_WINDOW)
        generated = work / "cngoldenlink_chinese.fnt"
        pages = validate_font(generated, missing)
        shutil.copyfile(generated, OUTPUT / "chinese.fnt")
        for page in pages:
            shutil.copyfile(page, OUTPUT / page.name)
    print(f"Generated {len(missing)} supplemental glyphs in {len(pages)} PNG page(s): {OUTPUT}")


if __name__ == "__main__":
    generate()

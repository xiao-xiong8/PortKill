"""Build the five static embedded faces from official variable font sources.

Requires fonttools==4.67.0. Source binaries live in .local/cache/font-sources and
are verified against Assets/Fonts/font-manifest.json on subsequent runs.
The subset keeps GB2312, interface text, punctuation and OpenType features.
"""
import argparse
import hashlib
import json
from pathlib import Path

from fontTools import subset
from fontTools.ttLib import TTFont
from fontTools.varLib.instancer import instantiateVariableFont

ROOT = Path(__file__).resolve().parents[2]
ASSETS = ROOT / "src/PortKiller.Desktop/Assets/Fonts"
SOURCES = {
    "NotoSansSC-VF.ttf": "https://raw.githubusercontent.com/google/fonts/main/ofl/notosanssc/NotoSansSC%5Bwght%5D.ttf",
    "Inter-VF.ttf": "https://raw.githubusercontent.com/google/fonts/main/ofl/inter/Inter%5Bopsz,wght%5D.ttf",
}


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def characters():
    chars = set(range(0x20, 0x100))
    for high in range(0xA1, 0xF8):
        for low in range(0xA1, 0xFF):
            try:
                chars.update(map(ord, bytes([high, low]).decode("gb2312")))
            except UnicodeDecodeError:
                pass
    # Include all current source text, not just a fragile list of UI labels.
    for directory in ("src", "tests"):
        for path in (ROOT / directory).rglob("*"):
            if path.suffix not in (".cs", ".axaml") or {"obj", "bin"}.intersection(path.parts):
                continue
            chars.update(map(ord, path.read_text(encoding="utf-8-sig")))
    chars.update(range(0x2000, 0x2070))
    chars.update(range(0x2190, 0x2200))
    return chars


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--sources", type=Path, default=ROOT / ".local/cache/font-sources")
    parser.add_argument("--full-faces", type=Path, default=ROOT / ".local/artifacts/font-install")
    args = parser.parse_args()
    ASSETS.mkdir(parents=True, exist_ok=True)
    args.full_faces.mkdir(parents=True, exist_ok=True)
    manifest_path = ASSETS / "font-manifest.json"
    source_hashes = {name: sha(args.sources / name) for name in SOURCES}
    if manifest_path.exists():
        previous = json.loads(manifest_path.read_text(encoding="utf-8"))
        if previous["sourceSha256"] != source_hashes:
            raise SystemExit("Font sources changed; review source versions before rebuilding assets.")

    chars = characters()
    faces = []
    for family, basename, weights in (
        ("Noto Sans SC", "NotoSansSC", ((400, "Regular"), (500, "Medium"), (600, "SemiBold"))),
        ("Inter", "Inter", ((500, "Medium"), (600, "SemiBold"))),
    ):
        for weight, style in weights:
            font = TTFont(args.sources / f"{basename}-VF.ttf", recalcTimestamp=False)
            axes = {axis.axisTag: weight if axis.axisTag == "wght" else axis.defaultValue for axis in font["fvar"].axes}
            font = instantiateVariableFont(font, axes, inplace=True, updateFontNames=True)
            # Keep a single typographic family, using real OS/2 weights.
            for platform, encoding, language in ((3, 1, 0x409), (1, 0, 0)):
                for name_id, value in ((1, family), (2, style), (4, f"{family} {style}"),
                                       (6, f"{basename}-{style}"), (16, family), (17, style)):
                    font["name"].setName(value, name_id, platform, encoding, language)
            filename = f"{basename}-{style}.ttf"
            font.save(args.full_faces / filename)
            options = subset.Options()
            options.layout_features = ["*"]
            options.name_IDs = ["*"]
            options.name_legacy = True
            options.name_languages = ["*"]
            options.recalc_timestamp = False
            keep = subset.Subsetter(options=options)
            keep.populate(unicodes=chars)
            keep.subset(font)
            target = ASSETS / filename
            font.save(target)
            cmap = font.getBestCmap()
            if "fvar" in font or font["OS/2"].usWeightClass != weight:
                raise SystemExit(f"Invalid static weight: {filename}")
            if family == "Noto Sans SC" and any(ord(c) not in cmap for c in "自动更新端口占用糊繁赢"):
                raise SystemExit("Missing required Chinese glyphs")
            if family == "Inter" and any(ord(c) not in cmap for c in "0123456789MB/s%"):
                raise SystemExit("Missing numeric glyphs")
            faces.append(dict(file=filename, family=family, style=style, weight=weight,
                              bytes=target.stat().st_size, glyphs=len(cmap), sha256=sha(target)))
            print(f"{filename}: {target.stat().st_size:,} bytes, {len(cmap)} characters", flush=True)
            font.close()

    for name in ("Inter", "NotoSansSC"):
        (ASSETS / f"{name}-OFL.txt").write_bytes((args.sources / f"{name}-OFL.txt").read_bytes())
    manifest = dict(tool="fonttools 4.67.0", sources=SOURCES, sourceSha256=source_hashes,
                    coverage="GB2312 + current project source text + punctuation/arrows",
                    faces=faces, totalBytes=sum(face["bytes"] for face in faces))
    manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"Total embedded font bytes: {manifest['totalBytes']:,}")


if __name__ == "__main__":
    main()

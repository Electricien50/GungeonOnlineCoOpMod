"""Builds the versioned release zip: installer, BepInEx, plugin and player documentation.
Same zip for Steam, GOG and Epic. Uses forward-slash paths so every unzip tool reads it."""
import os
import argparse
import json
import re
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
with open(os.path.join(ROOT, "src", "GungeonOnline", "Plugin.cs"), encoding="utf-8") as source:
    VERSION = re.search(r'const string Version = "([^"]+)"', source.read()).group(1)
OUT = os.path.join(ROOT, "dist", "GungeonOnline-" + VERSION + ".zip")


def add_tree(z, src, prefix):
    for base, _, files in os.walk(src):
        for f in files:
            if f == "changelog.txt":
                continue
            full = os.path.join(base, f)
            rel = os.path.relpath(full, src).replace(os.sep, "/")
            z.write(full, prefix + rel)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--bepinex-dir", default=os.path.join(ROOT, "third_party", "bepinex"))
    args = ap.parse_args()
    for required in ("winhttp.dll", os.path.join("BepInEx", "core", "BepInEx.dll")):
        if not os.path.isfile(os.path.join(args.bepinex_dir, required)):
            raise SystemExit("Missing BepInEx payload: " + required)
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    if os.path.exists(OUT):
        os.remove(OUT)
    with zipfile.ZipFile(OUT, "w", zipfile.ZIP_DEFLATED) as z:
        add_tree(z, args.bepinex_dir, "files/")
        z.write(os.path.join(ROOT, "src", "GungeonOnline", "bin", "Release", "GungeonOnline.dll"), "files/BepInEx/plugins/GungeonOnline/GungeonOnline.dll")
        for f in os.listdir(os.path.join(ROOT, "tools", "installer")):
            z.write(os.path.join(ROOT, "tools", "installer", f), f)
        z.write(os.path.join(ROOT, "docs", "HOW-TO-PLAY.txt"), "HOW-TO-PLAY.txt")
        for name in ("CHANGELOG.md", "LICENSE", "THIRD-PARTY-NOTICES.md"):
            z.write(os.path.join(ROOT, name), name)
    print("Package:", OUT)
    # r2modman / Thunderstore: import with "Import local mod" in r2modman. BepInEx comes from the mod manager.
    ts = os.path.join(ROOT, "dist", "GungeonOnline-" + VERSION + "-r2modman.zip")
    if os.path.exists(ts):
        os.remove(ts)
    manifest = {"name": "Gungeon_Online_CoOp", "version_number": VERSION, "website_url": "https://github.com/Hero88go/GungeonOnlineCoOpMod",
                "description": "Online co-op with independent cameras, experimental 3 and 4 player. Everyone needs the same mods.",
                "dependencies": ["BepInEx-BepInExPack_EtG-5.4.2101"]}
    with zipfile.ZipFile(ts, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("manifest.json", json.dumps(manifest, indent=2))
        z.write(os.path.join(ROOT, "tools", "thunderstore", "icon.png"), "icon.png")
        z.write(os.path.join(ROOT, "tools", "thunderstore", "README.md"), "README.md")
        z.write(os.path.join(ROOT, "CHANGELOG.md"), "CHANGELOG.md")
        z.write(os.path.join(ROOT, "src", "GungeonOnline", "bin", "Release", "GungeonOnline.dll"), "plugins/GungeonOnline/GungeonOnline.dll")
    print("Package:", ts)


if __name__ == "__main__":
    main()

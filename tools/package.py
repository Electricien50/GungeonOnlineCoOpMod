"""Builds dist/GungeonOnline-1.0.4.zip: double-click installer + files/ payload (BepInEx + plugin) + how-to-play.
Same zip for Steam, GOG and Epic. Uses forward-slash paths so every unzip tool reads it."""
import os
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "dist", "GungeonOnline-1.0.4.zip")


def add_tree(z, src, prefix):
    for base, _, files in os.walk(src):
        for f in files:
            if f == "changelog.txt":
                continue
            full = os.path.join(base, f)
            rel = os.path.relpath(full, src).replace(os.sep, "/")
            z.write(full, prefix + rel)


def main():
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    if os.path.exists(OUT):
        os.remove(OUT)
    with zipfile.ZipFile(OUT, "w", zipfile.ZIP_DEFLATED) as z:
        add_tree(z, os.path.join(ROOT, "third_party", "bepinex"), "files/")
        z.write(os.path.join(ROOT, "src", "GungeonOnline", "bin", "Release", "GungeonOnline.dll"), "files/BepInEx/plugins/GungeonOnline/GungeonOnline.dll")
        for f in os.listdir(os.path.join(ROOT, "tools", "installer")):
            z.write(os.path.join(ROOT, "tools", "installer", f), f)
        z.write(os.path.join(ROOT, "docs", "HOW-TO-PLAY.txt"), "HOW-TO-PLAY.txt")
    print("Package:", OUT)


if __name__ == "__main__":
    main()

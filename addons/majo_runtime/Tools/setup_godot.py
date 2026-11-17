#!/usr/bin/env python3
"""Install SHA-256 pinned Godot .NET templates, or the Linux editor for CI."""
import argparse
import hashlib
import os
from pathlib import Path
import shutil
import stat
import subprocess
import sys
import tempfile
import zipfile

VERSION = "4.7.2"
BASE = f"https://github.com/godotengine/godot-builds/releases/download/{VERSION}-stable/"
ASSETS = {
    "templates": (f"Godot_v{VERSION}-stable_mono_export_templates.tpz", "92f8681e349ef1f90891b792da95e3b2b0bd1ed610b78018c58feb2d87e15a9d"),
    "editor": (f"Godot_v{VERSION}-stable_mono_linux_x86_64.zip", "129f82db7bafd54ae14bb5bb284041c73860e8c7a009a3a026ca5e946cbff247"),
}


def checksum(path):
    h = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            h.update(block)
    return h.hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--templates", action="store_true")
    parser.add_argument("--editor", action="store_true", help="Linux x86_64 editor for CI")
    parser.add_argument("--directory", type=Path, default=Path(".tools/godot"))
    parser.add_argument("--github-env", type=Path)
    args = parser.parse_args()
    if not (args.templates or args.editor): parser.error("Specify --templates and/or --editor")
    cache = Path.home() / ("Library/Caches/majo" if sys.platform == "darwin" else ".cache/majo")
    cache.mkdir(parents=True, exist_ok=True)
    for kind in [k for k in ASSETS if getattr(args, k)]:
        if kind == "editor" and (sys.platform != "linux" or os.uname().machine != "x86_64"):
            raise SystemExit("Automatic editor installation currently supports Linux x86_64 only; install Godot .NET manually on other hosts")
        name, expected = ASSETS[kind]
        archive = cache / name
        if not archive.exists() or checksum(archive) != expected:
            partial = archive.with_suffix(archive.suffix + ".part")
            print("Downloading", name, flush=True)
            subprocess.run(["curl", "--fail", "--location", "--retry", "3", "--output", str(partial), BASE + name], check=True)
            if checksum(partial) != expected: raise SystemExit("Godot asset checksum mismatch")
            partial.replace(archive)
        if kind == "templates":
            if sys.platform == "darwin":
                base = Path.home() / "Library/Application Support/Godot/export_templates"
            elif sys.platform == "win32":
                base = Path(os.environ["APPDATA"]) / "Godot/export_templates"
            else:
                base = Path(os.environ.get("XDG_DATA_HOME", str(Path.home() / ".local/share"))) / "godot/export_templates"
            destination = base / f"{VERSION}.stable.mono"
        else:
            destination = args.directory.resolve()
        if destination.exists() and any(destination.iterdir()):
            if kind == "templates" and (destination / "version.txt").is_file() and (destination / "version.txt").read_text().strip() == f"{VERSION}.stable.mono":
                print("Already installed", destination)
                continue
            binary = next(destination.rglob("Godot*_mono_linux.x86_64"), None) if kind == "editor" else None
            if binary:
                if args.github_env:
                    with args.github_env.open("a") as stream: stream.write(f"GODOT_BIN={binary}\n")
                print("GODOT_BIN=" + str(binary))
                continue
            raise SystemExit(f"Unexpected existing installation at {destination}; repair it explicitly")
        destination.parent.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix=".godot-setup-", dir=destination.parent) as temp:
            with zipfile.ZipFile(archive) as z:
                z.extractall(temp)
                for entry in z.infolist():
                    path = Path(temp) / entry.filename
                    mode = entry.external_attr >> 16
                    if mode and path.is_file(): path.chmod(stat.S_IMODE(mode))
            if kind == "templates":
                ready = Path(temp) / "templates"
            else:
                extracted = list(Path(temp).iterdir())
                ready = Path(temp) / "ready"
                ready.mkdir()
                for path in extracted: shutil.move(str(path), ready / path.name)
            if destination.exists(): destination.rmdir()
            ready.rename(destination)
        print("Installed", destination, flush=True)
        if kind == "editor":
            binary = next(destination.rglob("Godot*_mono_linux.x86_64"))
            binary.chmod(binary.stat().st_mode | 0o111)
            if args.github_env:
                with args.github_env.open("a") as stream: stream.write(f"GODOT_BIN={binary}\n")
            print("GODOT_BIN=" + str(binary))


if __name__ == "__main__":
    main()

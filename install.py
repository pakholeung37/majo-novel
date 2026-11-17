#!/usr/bin/env python3
"""Download an explicit Majo release, verify its checksum, and run its installer."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import urllib.request
import zipfile


def download(url):
    request = urllib.request.Request(url, headers={"User-Agent": "Majo-Installer/1"})
    with urllib.request.urlopen(request, timeout=90) as response:
        return response.read()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=["init", "install", "upgrade"])
    parser.add_argument("--version", required=True, help="Explicit release tag, e.g. v0.1.0-rc.1")
    parser.add_argument("--repo", default="pakholeung37/majo-novel")
    parser.add_argument("--project", required=True, type=Path)
    parser.add_argument("--name", default="Game")
    parser.add_argument("--check", action="store_true")
    parser.add_argument("--godot")
    parser.add_argument("--check-command")
    parser.add_argument("--allow-breaking", action="store_true")
    args = parser.parse_args()
    if not re.fullmatch(r'v[0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9.-]+)?', args.version):
        parser.error("Use an explicit version tag; moving latest/main installs are not supported")
    if not re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', args.repo): parser.error("Invalid repository")
    base = f"https://github.com/{args.repo}/releases/download/{args.version}/"
    name = f"majo-runtime-{args.version[1:]}-with-yarn.zip"
    sums = download(base + "SHA256SUMS").decode()
    matches = [line.split()[0] for line in sums.splitlines() if len(line.split()) == 2 and line.split()[1] == name]
    if len(matches) != 1 or not re.fullmatch('[0-9a-fA-F]{64}', matches[0]):
        raise ValueError("Release checksum list does not identify one bundled addon archive")
    print(f"Downloading {args.repo} {args.version}", flush=True)
    raw = download(base + name)
    if hashlib.sha256(raw).hexdigest().lower() != matches[0].lower():
        raise ValueError("Release archive checksum mismatch")
    with tempfile.TemporaryDirectory(prefix="majo-release-install-") as temp:
        root = Path(temp)
        archive = root / name
        archive.write_bytes(raw)
        with zipfile.ZipFile(archive) as z:
            # No archive extraction: only the verified single-file manager is bootstrapped.
            manager = root / "majo.py"
            manager.write_bytes(z.read("addons/majo_runtime/Tools/majo.py"))
            manifest = json.loads(z.read("addons/majo_runtime/release-manifest.json"))
            if manifest["version"] != args.version[1:] or not manifest["yarnBundled"]:
                raise ValueError("Release manifest does not match the requested version")
        cmd = [sys.executable, str(manager), args.command, "--project", str(args.project.resolve()),
               "--archive", str(archive), "--sha256", matches[0]]
        if args.command == "init": cmd += ["--name", args.name]
        if args.check: cmd += ["--check"]
        if args.allow_breaking: cmd += ["--allow-breaking"]
        if args.godot: cmd += ["--godot", args.godot]
        if args.check_command: cmd += ["--check-command", args.check_command]
        result = subprocess.run(cmd)
        if result.returncode: raise SystemExit(result.returncode)
        lock = args.project.resolve() / "majo.lock.json"
        record = json.loads(lock.read_text())
        record["source"].update({"release": args.version, "repository": args.repo,
                                 "url": base + name})
        lock.write_text(json.dumps(record, sort_keys=True, indent=2) + "\n")


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, KeyError, zipfile.BadZipFile) as error:
        raise SystemExit(str(error))

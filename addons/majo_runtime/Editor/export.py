#!/usr/bin/env python3
"""Validate Majo assets, then stage an export; publish outputs only on success."""
import argparse
import os
from pathlib import Path
import shutil
import subprocess
import tempfile


def run(command):
    result = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                            text=True, timeout=300)
    print(result.stdout, end="", flush=True)
    if result.returncode or any(line.startswith("ERROR:") for line in result.stdout.splitlines()):
        raise RuntimeError("Godot validation/export failed; no export was published")
    return result.stdout


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--godot", default=os.environ.get("GODOT_BIN", "godot"))
    parser.add_argument("--project", type=Path, default=Path.cwd())
    parser.add_argument("--preset", required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--pack", action="store_true", help="Export a PCK instead of a player")
    args = parser.parse_args()
    project = args.project.resolve()
    output = args.output.resolve()
    if output.exists():
        raise RuntimeError(f"Output already exists: {output}")
    base = [args.godot, "--headless", "--path", str(project)]
    # Plugin prints a sentinel so disabling it cannot silently skip validation.
    log = run(base + ["--editor", "--quit", "--", "--majo-validate-export"])
    if "MAJO_EXPORT_VALIDATED" not in log:
        raise RuntimeError("Majo export validation did not run; enable the Majo editor plugin")
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix=".majo-export-", dir=output.parent) as temp:
        staging = Path(temp)
        run(base + ["--export-pack" if args.pack else "--export-release",
                    args.preset, str(staging / output.name)])
        if not (staging / output.name).exists():
            raise RuntimeError("Godot did not produce the requested output")
        files = list(staging.iterdir())
        for item in files:
            if (output.parent / item.name).exists():
                raise RuntimeError(f"Export destination already exists: {item.name}")
        for item in files:
            shutil.move(str(item), output.parent / item.name)


if __name__ == "__main__":
    main()

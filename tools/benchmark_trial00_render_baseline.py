#!/usr/bin/env python3
"""Stage local court textures into a standalone Godot project and measure its render loop."""

import argparse
from datetime import datetime, timezone
import json
import math
import os
from pathlib import Path
import re
import shutil
from statistics import median
import subprocess

ROOT = Path(__file__).resolve().parent.parent
TEMPLATE = Path(__file__).with_name("trial00-render-baseline")
ACTORS = ("Ema", "Hanna", "Sherry", "Leia", "Alisa", "Coco",
          "Miria", "Margo", "Nanoka", "AnAn", "Meruru")
MODES = ("direct", "single", "split")


def stage_project(project, assets):
    stamp_file = project / ".asset-sources.json"
    previous = json.loads(stamp_file.read_text()) if stamp_file.exists() else {}
    current = {}
    textures = [Path("backgrounds/Court_Wall.png"), Path("objects/Court_Stand.png")]
    manifests = []
    for actor in ACTORS:
        manifest = Path("characters", actor, "layered_character.json")
        layers = json.loads((assets / manifest).read_text(encoding="utf-8"))["layers"]
        textures.extend(manifest.parent / atlas for atlas in dict.fromkeys(layer["atlas"] for layer in layers))
        manifests.append(manifest)
    for relative in manifests + [path for texture in textures for path in
                                 (texture, texture.with_suffix(texture.suffix + ".import"))]:
        source = assets / relative
        # The original import metadata preserves VRAM compression and alpha settings.
        if relative.suffix == ".import" and not source.is_file():
            continue
        info = source.stat()
        key = relative.as_posix()
        current[key] = [str(source.resolve()), info.st_size, info.st_mtime_ns]
        target = project / "demos/manosaba/assets" / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        if not target.exists() or current[key] != previous.get(key):
            shutil.copy2(source, target)
    for source in TEMPLATE.iterdir():
        if source.is_file() and source.suffix in (".gd", ".tscn", ".gdshader", ".godot"):
            shutil.copy2(source, project / source.name)
    stamp_file.write_text(json.dumps(current))


def run_logged(command, log_path, timeout=None):
    with log_path.open("w") as log:
        completed = subprocess.run(command, stdout=log, stderr=subprocess.STDOUT, timeout=timeout)
    output = log_path.read_text(errors="replace")
    if completed.returncode != 0 or re.search(r"^(ERROR:|SCRIPT ERROR:|SHADER ERROR:)", output, re.M):
        raise RuntimeError(f"Godot failed, see {log_path}")
    return output


def summarize(result):
    frames = result.pop("frames")
    if not frames or result["actors"] != len(ACTORS) or not result["layers"] or not result["atlases"]:
        raise RuntimeError("Incomplete baseline scene or no timing samples")
    if result["rendering_method"] != "forward_plus":
        raise RuntimeError("Expected real Forward+ rendering")
    gaps = sorted(frame["gap_ms"] for frame in frames)
    return result | {
        "frames": len(frames),
        "fps": len(gaps) * 1000 / sum(gaps),
        "p50_ms": median(gaps),
        "p95_ms": gaps[math.ceil(len(gaps) * 0.95) - 1],
        "p99_ms": gaps[math.ceil(len(gaps) * 0.99) - 1],
        "max_ms": gaps[-1],
        "over_8_33_percent": 100 * sum(gap > 1000 / 120 for gap in gaps) / len(gaps),
        "over_12_5_percent": 100 * sum(gap > 12.5 for gap in gaps) / len(gaps),
        "focused_percent": 100 * sum(frame["focused"] for frame in frames) / len(frames),
        "median_draw_calls": median(frame["draw_calls"] for frame in frames),
    }


def main():
    default_godot = "/Applications/Godot_mono.app/Contents/MacOS/Godot"
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--godot", default=os.environ.get("GODOT_BIN") or shutil.which("godot") or default_godot)
    parser.add_argument("--assets", type=Path, default=ROOT / "demos/manosaba/assets")
    parser.add_argument("--mode", choices=(*MODES, "all"), default="all")
    parser.add_argument("--seconds", type=float, default=12.0, help="Time per mode. Use 0 to leave one mode open.")
    parser.add_argument("--resolution", default="2560x1440")
    parser.add_argument("--driver", help="Godot rendering driver, e.g. metal or vulkan")
    parser.add_argument("--no-vsync", action="store_true", help="Measure uncapped throughput separately from 120 Hz pacing.")
    parser.add_argument("--static", action="store_true", help="Disable camera motion for a fixed shot.")
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    if not math.isfinite(args.seconds) or args.seconds < 0 or (args.seconds == 0 and args.mode == "all"):
        parser.error("--seconds must be finite and nonnegative; interactive mode requires a single --mode")
    if not re.fullmatch(r"[1-9]\d*x[1-9]\d*", args.resolution):
        parser.error("--resolution must be WIDTHxHEIGHT")
    stamp = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%S-%f")
    base = ROOT / "build/trial00-render-baseline"
    output = (args.output or base / stamp).resolve()
    output.mkdir(parents=True, exist_ok=True)
    project = base / "project"
    try:
        stage_project(project, args.assets)
        if not (project / ".godot/imported").is_dir():
            print("Importing local court and character atlases into the isolated project.", flush=True)
        common = [args.godot, "--path", str(project)]
        run_logged(common + ["--headless", "--editor", "--import"], output / "import.log", timeout=300)
        modes = MODES if args.mode == "all" else (args.mode,)
        summaries = []
        for mode in modes:
            directory = output / mode
            directory.mkdir(exist_ok=False)
            command = [*common, "--audio-driver", "Dummy", "--resolution", args.resolution, "--always-on-top"]
            if args.driver:
                command.extend(["--rendering-driver", args.driver])
            if args.no_vsync:
                command.append("--disable-vsync")
            command.extend(["--", f"--layout={mode}", f"--seconds={args.seconds}", f"--output={directory}"])
            if args.static:
                command.append("--static")
            print(f"{mode}: keep the baseline window focused.", flush=True)
            log = run_logged(command, directory / "game.log", args.seconds + 120 if args.seconds else None)
            if args.seconds == 0:
                continue
            metrics = summarize(json.loads((directory / "result.json").read_text()))
            device = next((line for line in log.splitlines() if "Using Device" in line), "")
            if not device or any(name in device.lower() for name in ("llvmpipe", "lavapipe", "swiftshader", "microsoft basic render")):
                raise RuntimeError(f"Expected a hardware renderer, see {directory / 'game.log'}")
            metrics["device"] = device.strip()
            summaries.append(metrics)
            (output / "summary.json").write_text(json.dumps(summaries, indent=2) + "\n")
            print(f"  {metrics['fps']:.2f} FPS, P99 {metrics['p99_ms']:.2f} ms, "
                  f"{metrics['focused_percent']:.1f}% focused", flush=True)
        if any(entry["focused_percent"] < 99 for entry in summaries):
            raise RuntimeError("At least one run lost focus during sampling. Repeat with the window in front.")
    except (OSError, RuntimeError, ValueError, subprocess.TimeoutExpired) as error:
        parser.exit(1, f"{error}\nEvidence: {output}\n")
    print(f"Evidence: {output}")


if __name__ == "__main__":
    main()

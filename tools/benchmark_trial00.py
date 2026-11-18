#!/usr/bin/env python3
"""Run the real-renderer Manosaba Trial00 input/performance benchmark sequentially."""

import argparse
from contextlib import contextmanager
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
from tempfile import TemporaryDirectory

from analyze_trial00_progress import analyze

ROOT = Path(__file__).resolve().parent.parent


@contextmanager
def benchmark_project(frame_queue_size):
    if frame_queue_size is None:
        yield ROOT
        return
    if (ROOT / 'override.cfg').exists():
        raise ValueError('frame queue experiment requires a project without override.cfg')
    with TemporaryDirectory(prefix='majo-trial00-') as temporary:
        project = Path(temporary)
        for name in ('project.godot', '.godot', 'addons', 'demos', 'icon.svg', 'Majo Novel.csproj'):
            (project / name).symlink_to(ROOT / name)
        (project / 'override.cfg').write_text(f'[rendering]\nrendering_device/vsync/frame_queue_size={frame_queue_size}\n')
        yield project


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--godot", default=os.environ.get("GODOT_BIN", "/Applications/Godot_mono.app/Contents/MacOS/Godot"))
    parser.add_argument("--mode", choices=("normal", "rapid"), default="rapid")
    parser.add_argument("--runs", type=int, default=2)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--no-build", action="store_true")
    parser.add_argument("--stage-rasters", action="store_true", help="Force legacy Direction Stage raster outputs as a control")
    parser.add_argument("--gpu-screen-snapshots", action="store_true", help="Experiment: copy Screen transition snapshots on the GPU")
    parser.add_argument("--frame-queue-size", type=int, choices=(2, 3), help="Compare RD frame buffering in an isolated launch directory")
    args = parser.parse_args()
    if args.runs < 1:
        parser.error("--runs must be positive")
    output = (args.output or ROOT / "build/trial00-benchmark" / datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%S")).resolve()
    output.mkdir(parents=True, exist_ok=True)
    if any(output.glob("run-*")):
        parser.error("output already contains runs; choose a new directory")
    if not args.no_build:
        with (output / "build.log").open("w") as log:
            subprocess.run(["dotnet", "build", str(ROOT / "Majo Novel.csproj"), "--nologo"], cwd=ROOT, stdout=log, stderr=subprocess.STDOUT, check=True)
    # Include untracked benchmark sources in the fingerprint, without copying assets.
    paths = subprocess.check_output(["git", "ls-files", "-co", "--exclude-standard", "-z"], cwd=ROOT).split(b"\0")
    digest = hashlib.sha256()
    for name in sorted(set(paths)):
        if not name:
            continue
        path = ROOT / os.fsdecode(name)
        if path.is_file():
            digest.update(name + b"\0" + path.read_bytes() + b"\0")
    manifest = {
        "revision": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip(),
        "workingTree": subprocess.check_output(["git", "status", "--short"], cwd=ROOT, text=True),
        "sourceSha256": digest.hexdigest(), "mode": args.mode, "runs": [],
    }
    passed = True
    for index in range(args.runs):
        directory = output / f"run-{index + 1}"
        directory.mkdir()
        command = [args.godot, "--path", str(ROOT), "--always-on-top", "res://demos/manosaba/ManosabaDemo.tscn", "--",
                   "--trial00-progress", "--trial00-progress-input", f"--trial00-progress-output={directory}"]
        if args.mode == "rapid":
            command.append("--trial00-progress-rapid")
        if args.stage_rasters:
            command.append("--trial00-progress-stage-rasters")
        if args.gpu_screen_snapshots:
            command.append("--majo-gpu-screen-snapshots")
        print(f"Trial00 {args.mode} {index + 1}/{args.runs}; keep the game focused.", flush=True)
        with (directory / "run.log").open("w") as log:
            try:
                with benchmark_project(args.frame_queue_size) as project:
                    command[2] = str(project)
                    process = subprocess.run(command, cwd=ROOT, stdout=log, stderr=subprocess.STDOUT, timeout=180)
                code = process.returncode
            except subprocess.TimeoutExpired:
                code = -1
        entry = {"directory": str(directory), "command": command, "exitCode": code, "valid": False}
        if code == 0 and (directory / "configuration.json").exists():
            # Keep the complete machine-readable report, print just the comparison metrics.
            from contextlib import redirect_stdout
            with (directory / "analysis.log").open("w") as log, redirect_stdout(log):
                summary = analyze(directory)
            config = summary["configuration"]
            frame = summary["phases"]["progression"]
            inputs = summary.get("inputs", {})
            entry["valid"] = bool(config["completed"] and config["lineCount"] == 24 and
                                  not config.get("visibilityAudit", False) and
                                  frame.get("focusedPercent", 0) >= 99 and inputs.get("samples", 0) >= 24 and
                                  inputs.get("unhandled", 1) == 0 and inputs.get("unacknowledged", 1) == 0 and
                                  inputs.get("timedOut", 1) == 0 and inputs.get("missingContent", 1) == 0 and
                                  inputs.get("focusedSamples", 0) == inputs.get("samples", -1))
            entry["progression"] = frame
            if args.frame_queue_size is not None:
                entry['requestedFrameQueueSize'] = args.frame_queue_size
                entry['actualFrameQueueSize'] = config.get('rdFrameDelay')
                entry['valid'] &= config.get('rdFrameDelay') == args.frame_queue_size
            entry["inputs"] = inputs
            entry["timeToFirstLineMs"] = summary["timeToFirstLineMs"]
            entry["screenCaptureSpans"] = {name: stats for name, stats in summary["spanStats"].items()
                                           if name.startswith("direction.screen.")}
            if args.gpu_screen_snapshots:
                entry["valid"] &= ("direction.screen.gpu-copy.enqueue.sync" in entry["screenCaptureSpans"] and
                                   "direction.screen.readback.sync" not in entry["screenCaptureSpans"] and
                                   "direction.screen.upload.sync" not in entry["screenCaptureSpans"])
            log_text = (directory / "run.log").read_text()
            entry['metalBarriersRequested'] = os.environ.get('GODOT_MTL_FORCE_BARRIERS') == '1'
            entry['metalBarriersEnabled'] = 'Metal 3: Resource barriers enabled.' in log_text and 'Resource barriers unavailable.' not in log_text
            if entry['metalBarriersRequested']:
                entry['valid'] &= entry['metalBarriersEnabled']
            entry["renderer"] = next((line for line in log_text.splitlines() if "Using Device" in line), None)
            entry["softwareRenderer"] = bool(entry["renderer"] and any(name in entry["renderer"].lower() for name in
                ("llvmpipe", "lavapipe", "swiftshader", "software rasterizer", "microsoft basic render")))
            entry["engineErrorLines"] = [line for line in log_text.splitlines() if line.startswith(("ERROR:", "SCRIPT ERROR:", "SHADER ERROR:"))]
            if entry["engineErrorLines"] or entry["renderer"] is None or entry["softwareRenderer"]:
                entry["valid"] = False
            print(f"  FPS {frame.get('fps', 0):.2f}; P99 {frame.get('p99Ms', 0):.2f}ms; valid={entry['valid']}", flush=True)
        manifest["runs"].append(entry)
        passed &= entry["valid"]
        (output / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n")
        if code != 0:
            break
    print(f"Evidence: {output}")
    return 0 if passed else 1


if __name__ == "__main__":
    sys.exit(main())

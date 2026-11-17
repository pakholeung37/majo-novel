#!/usr/bin/env python3
"""Run render-host pixel/lifetime acceptance on a real Godot renderer and save evidence."""

import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import time

ROOT = Path(__file__).resolve().parent.parent


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--godot", default=os.environ.get("GODOT_BIN", "/Applications/Godot_mono.app/Contents/MacOS/Godot"))
    parser.add_argument("--method", choices=("forward_plus", "mobile", "gl_compatibility"), default="forward_plus")
    parser.add_argument("--driver", help="Godot rendering driver, e.g. metal, vulkan or opengl3")
    parser.add_argument("--output", type=Path)
    parser.add_argument("--no-build", action="store_true")
    args = parser.parse_args()
    output = (args.output or ROOT / "build/rendering-gpu" / f"{args.method}-{args.driver or 'auto'}").resolve()
    output.mkdir(parents=True, exist_ok=True)
    (output / "results.json").unlink(missing_ok=True)
    if not args.no_build:
        subprocess.run(["dotnet", "build", str(ROOT / "Majo Novel.csproj"), "--nologo"], cwd=ROOT, check=True)
    command = [args.godot, "--path", str(ROOT), "--rendering-method", args.method,
               "--audio-driver", "Dummy", "--resolution", "640x360", "--max-fps", "0", "--always-on-top"]
    if args.driver:
        command += ["--rendering-driver", args.driver]
    command += ["res://tests/headless/HeadlessTestRunner.tscn", "--", "--majo-render-host"]
    started = time.time()
    log_path = output / "godot.log"
    with log_path.open("w") as log:
        try:
            process = subprocess.run(command, cwd=ROOT, stdout=log, stderr=subprocess.STDOUT, timeout=180)
            exit_code = process.returncode
        except subprocess.TimeoutExpired:
            exit_code = -1
    log = log_path.read_text()
    cases = re.findall(r"^MAJO_RENDER_CASE_OK (\w+)$", log, re.M)
    renderer = next((line for line in log.splitlines() if "Using Device" in line), None)
    software = renderer is not None and any(name in renderer.lower() for name in (
        "llvmpipe", "lavapipe", "swiftshader", "software rasterizer", "microsoft basic render"))
    errors = re.findall(r"^.*(?:SHADER ERROR:|Shader compilation failed|ObjectDB.*leaked|RID.*leaked).*$", log, re.M)
    passed = (
        exit_code == 0 and "MAJO_RENDER_HOST_GPU_OK" in log and len(set(cases)) == 13
        and not errors and renderer is not None and not software
    )
    sources = sorted((ROOT / "addons/majo_runtime/Runtime/Direction/Godot").glob("*.cs"))
    digest = hashlib.sha256()
    for path in sources:
        digest.update(path.relative_to(ROOT).as_posix().encode())
        digest.update(path.read_bytes())
    evidence = []
    for relative, name in [
        ("build/restore-render-evidence", "restore"),
        ("build/render-host-ownership", "ownership"),
        ("build/wall-repetition-evidence", "wall-repetition"),
        ("artifacts/live-visual", "live-visual"),
        ("artifacts/background-actor/alpha", "background-alpha"),
        ("artifacts/background-actor/custom", "background-custom"),
    ]:
        directory = ROOT / relative
        if not directory.exists():
            continue
        for path in directory.rglob("*"):
            if path.is_file() and path.stat().st_mtime >= started:
                destination = output / "evidence" / name / path.relative_to(directory)
                destination.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(path, destination)
                evidence.append(destination.relative_to(output).as_posix())
    required = ["evidence/restore/results.json", "evidence/ownership/retained-transition-curtain.png",
                "evidence/ownership/released-target.png"]
    passed = passed and all(path in evidence for path in required)
    result = {
        "passed": passed, "exitCode": exit_code, "renderer": renderer,
        "method": args.method, "driver": args.driver, "cases": cases,
        "startedAtUtc": datetime.fromtimestamp(started, timezone.utc).isoformat(),
        "seconds": time.time() - started, "sourceSha256": digest.hexdigest(),
        "renderErrors": errors, "evidence": evidence, "command": command,
    }
    (output / "results.json").write_text(json.dumps(result, indent=2) + "\n")
    print(f"GPU acceptance {'passed' if passed else 'FAILED'}: {renderer}")
    print(f"Evidence: {output}")
    if not passed:
        print("\n".join(log.splitlines()[-50:]))
        raise SystemExit(1)


if __name__ == "__main__":
    main()

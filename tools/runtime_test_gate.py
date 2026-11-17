#!/usr/bin/env python3
"""Run the same runtime, installation and packed-export gate on every desktop OS."""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time

ROOT = Path(__file__).resolve().parent.parent
PROJECTS = {
    "core": "tests/Majo.Runtime.Core.UnitTests/Majo.Runtime.Core.UnitTests.csproj",
    "domain": "tests/Majo.Runtime.Domain.UnitTests/Majo.Runtime.Domain.UnitTests.csproj",
    "commands": "tests/Majo.Runtime.Commands.UnitTests/Majo.Runtime.Commands.UnitTests.csproj",
    "persistence": "tests/Majo.Runtime.Persistence.UnitTests/Majo.Runtime.Persistence.UnitTests.csproj",
    "resources": "tests/Majo.Runtime.Resources.UnitTests/Majo.Runtime.Resources.UnitTests.csproj",
    "generator": "tests/Majo.CommandGenerator.Tests/Majo.CommandGenerator.Tests.csproj",
    "runtime": "tests/Majo.Runtime.UnitTests/Majo.Runtime.UnitTests.csproj",
    "demos": "tests/Majo.Demos.UnitTests/Majo.Demos.UnitTests.csproj",
}
INDEPENDENT = ("core", "domain", "commands", "persistence", "resources", "generator")
GAME = ROOT / "Majo Novel.csproj"


class RuntimeTestGate:
    def __init__(self, output):
        self.output = Path(output).resolve()
        self.output.mkdir(parents=True, exist_ok=True)
        self.steps = []
        self.started = time.monotonic()
        self.finish(False)

    def finish(self, passed):
        report = {"passed": passed, "platform": sys.platform, "seconds": time.monotonic() - self.started,
                  "steps": self.steps}
        (self.output / "results.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")

    def run(self, name, command, *, cwd=ROOT, timeout=180, required_output=(), expect_failure=False):
        command = [str(arg) for arg in command]
        print(f"{name}: {subprocess.list2cmdline(command)}", flush=True)
        record = {"name": name, "command": command, "passed": False, "timedOut": False,
                  "log": f"{len(self.steps) + 1:02d}.log"}
        started = time.monotonic()
        try:
            result = subprocess.run(command, cwd=cwd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                    encoding="utf-8", errors="replace", timeout=timeout,
                                    env={**os.environ, "PYTHONUTF8": "1"})
            log = result.stdout
            record["exitCode"] = result.returncode
            success = result.returncode > 0 if expect_failure else result.returncode == 0
            record["passed"] = success and all(marker in log for marker in required_output)
        except subprocess.TimeoutExpired as error:
            record["timedOut"] = True
            log = error.stdout or b""
            if isinstance(log, bytes):
                log = log.decode("utf-8", errors="replace")
            log += f"\nCommand exceeded {timeout} seconds.\n"
        except OSError as error:
            log = str(error) + "\n"
        record["seconds"] = time.monotonic() - started
        (self.output / record["log"]).write_text(log, encoding="utf-8")
        print(log, end="", flush=True)
        self.steps.append(record)
        self.finish(False)
        if not record["passed"]:
            raise RuntimeError(f"{name} failed; evidence: {self.output / record['log']}")
        return log


def resolve_godot(value):
    value = value or os.environ.get("GODOT_BIN")
    if not value and sys.platform == "darwin":
        value = "/Applications/Godot_mono.app/Contents/MacOS/Godot"
    value = value or shutil.which("godot_console") or shutil.which("godot")
    executable = Path(value).resolve() if value and Path(value).is_file() else None
    if executable is None:
        raise ValueError("Godot .NET executable not found; set GODOT_BIN or pass --godot.")
    return str(executable)


def consumer_smoke(gate, godot):
    with tempfile.TemporaryDirectory(prefix="majo-consumer-") as temp, \
            tempfile.TemporaryDirectory(prefix="majo-package-") as package_temp:
        project = Path(temp).resolve()
        package = Path(package_temp).resolve()
        shutil.copytree(ROOT / "tools/core-consumer-smoke", project, dirs_exist_ok=True)
        gate.run("Package release addons", [sys.executable, ROOT / "tools/package-addon.py",
                                           "--output-dir", package])
        archives = list(package.glob("*-with-yarn.zip"))
        if len(archives) != 1:
            raise RuntimeError("Expected exactly one bundled addon archive.")
        manager = ROOT / "addons/majo_runtime/Tools/majo.py"
        install = [sys.executable, manager, "install", "--project", project, "--archive", archives[0]]
        gate.run("Install consumer", install)
        if any(path.is_dir() for path in project.rglob("demos")):
            raise RuntimeError("Core consumer unexpectedly contains demo sources.")
        csproj = project / "CoreConsumerSmoke.csproj"
        gate.run("Restore consumer", ["dotnet", "restore", csproj, "--nologo"], cwd=project)
        gate.run("Build consumer", ["dotnet", "build", csproj, "--nologo", "--no-restore"], cwd=project)
        gate.run("Import consumer", [godot, "--headless", "--path", project, "--editor", "--quit"])
        gate.run("Check installed dependencies", [sys.executable, manager, "doctor", "--project", project])
        gate.run("Checked consumer upgrade", install + ["--allow-breaking", "--check", "--godot", godot],
                 timeout=300)
        gate.run("Run source consumer", [godot, "--headless", "--path", project], timeout=60,
                 required_output=("MAJO_CONSUMER_SOURCE_OK",))
        gate.run("Export and boot packed Release consumer",
                 [sys.executable, ROOT / "tools/test-addon-export.py", project, godot], timeout=600,
                 required_output=("MAJO_CONSUMER_EXPORT_OK", "MAJO_EXPORT_REJECTION_OK"))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    modes = parser.add_mutually_exclusive_group()
    for name in ("independent", "domain", "unit", "fast", "full"):
        modes.add_argument("--" + name, dest="mode", action="store_const", const=name)
    parser.set_defaults(mode="full")
    parser.add_argument("--godot", help="Godot .NET executable (or set GODOT_BIN)")
    parser.add_argument("--output", type=Path, default=ROOT / "build/test-gate")
    args = parser.parse_args()
    gate = RuntimeTestGate(args.output)
    try:
        if args.mode in ("domain", "independent"):
            for name in ("core", "domain") if args.mode == "domain" else INDEPENDENT:
                gate.run(f"Test {name}", ["dotnet", "test", ROOT / PROJECTS[name], "--nologo"])
        else:
            godot = resolve_godot(args.godot) if args.mode != "unit" else None
            for script in ("test-gate.py", "test-addon-manager.py", "test-installer.py"):
                gate.run(f"Test {script}", [sys.executable, ROOT / "tools" / script])
            for name, project in PROJECTS.items():
                gate.run(f"Restore {name}", ["dotnet", "restore", ROOT / project, "--nologo"])
            if godot:
                gate.run("Restore game", ["dotnet", "restore", GAME, "--nologo"])
            for name in INDEPENDENT[:-1]:
                gate.run(f"Test {name}", ["dotnet", "test", ROOT / PROJECTS[name], "--nologo", "--no-restore"])
            build = ["dotnet", "build", ROOT / PROJECTS["runtime"], "--nologo", "--no-restore"]
            if args.mode == "full":
                build.append("--no-incremental")
            gate.run("Build runtime tests", build)
            gate.run("Test runtime", ["dotnet", "test", ROOT / PROJECTS["runtime"], "--nologo",
                                      "--no-restore", "--no-build"])
            for name in ("demos", "generator"):
                gate.run(f"Test {name}", ["dotnet", "test", ROOT / PROJECTS[name], "--nologo", "--no-restore"])
            if godot:
                gate.run("Build game", ["dotnet", "build", GAME, "--nologo", "--no-restore"])
                base = [godot, "--headless", "--path", ROOT]
                gate.run("Import game", base + ["--editor", "--quit"])
                runner = base + ["res://tests/headless/HeadlessTestRunner.tscn", "--"]
                gate.run("Headless integration", runner, timeout=60,
                         required_output=("Majo Runtime headless tests passed.",))
                gate.run("Headless failure probe", runner + ["--majo-force-failure"], timeout=60,
                         expect_failure=True, required_output=("Intentional headless failure probe.",))
                if args.mode == "full":
                    consumer_smoke(gate, godot)
        gate.finish(True)
        print(f"Majo Runtime {args.mode} test suite passed in {time.monotonic() - gate.started:.1f}s.", flush=True)
    except (RuntimeError, ValueError) as error:
        raise SystemExit(str(error))


if __name__ == "__main__":
    main()

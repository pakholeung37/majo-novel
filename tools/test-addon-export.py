#!/usr/bin/env python3
"""Exercise selected-resource export and boot Release assemblies without source fallback."""
from pathlib import Path
import shutil
import subprocess
import sys

from platform_target import export_target

project = Path(sys.argv[1]).resolve()
godot = sys.argv[2]
target = export_target()
preset = target.preset
rid = target.rid
config = project / "export_presets.cfg"
config.write_text(config.read_text().replace('binary_format/architecture="x86_64"', f'binary_format/architecture="{target.architecture}"'))
player = project / "player"
player.mkdir()
(player / ".gdignore").touch()
wrapper = project / "addons/majo_runtime/Editor/export.py"
export = [sys.executable, str(wrapper), "--godot", godot, "--project", str(project),
          "--preset", preset, "--output", str(player / "consumer.pck"), "--pack"]

def run(args, log):
    result = subprocess.run(args, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, timeout=300)
    (player / log).write_text(result.stdout)
    if result.returncode:
        raise RuntimeError(result.stdout[-16000:])
    return result.stdout

run(export, "export.log")
# A valid Yarn program with invalid Direction metadata must fail before publishing.
source = project / "assets/scripts/main.yarn"
good = source.read_text()
source.write_text(good.replace("title: Start", "title: Start\nscene: first\nscene: duplicate"))
try:
    invalid = export.copy()
    invalid[invalid.index("--output") + 1] = str(player / "invalid.pck")
    rejected = subprocess.run(invalid, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, timeout=300)
    (player / "invalid.log").write_text(rejected.stdout)
    assert rejected.returncode != 0 and not (player / "invalid.pck").exists(), rejected.stdout
finally:
    source.write_text(good)
# Disabling Majo must not silently bypass validation.
settings = project / "project.godot"
good_settings = settings.read_text()
settings.write_text(good_settings.replace(', "res://addons/majo_runtime/plugin.cfg"', ''))
try:
    disabled = export.copy()
    disabled[disabled.index("--output") + 1] = str(player / "disabled.pck")
    rejected = subprocess.run(disabled, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, timeout=300)
    assert rejected.returncode != 0 and not (player / "disabled.pck").exists(), rejected.stdout
finally:
    settings.write_text(good_settings)
# Keep only published binaries and the pack in a fresh project directory.
isolated = player / "isolated"
binaries = isolated / ".godot/mono/temp/bin/Debug"
binaries.mkdir(parents=True)
published = project / f".godot/mono/temp/bin/ExportRelease/{rid}"
for path in published.iterdir():
    if path.is_file():
        shutil.copy2(path, binaries / path.name)
shutil.copy2(player / "consumer.pck", isolated / "consumer.pck")
log = run([godot, "--headless", "--path", str(isolated), "--main-pack", str(isolated / "consumer.pck"),
           "--", "--majo-export-smoke"], "boot.log")
print(log)
assert "MAJO_CONSUMER_EXPORT_OK" in log, "Packed Release consumer did not finish"
assert not any(line.startswith("ERROR:") for line in log.splitlines()), log
print("MAJO_EXPORT_REJECTION_OK")

#!/usr/bin/env python3
"""Exercise the real editor pipeline with disposable Yarn sources (requires a Debug build)."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parent.parent
GODOT = os.environ.get("GODOT_BIN", "/Applications/Godot_mono.app/Contents/MacOS/Godot")
fixture = Path(tempfile.mkdtemp(prefix="direction-import-", dir=ROOT / "tests/headless"))
manifest = fixture / "probe.yarnproject"
asset = Path(str(manifest) + ".direction.tres")


def import_project():
    result = subprocess.run(
        [GODOT, "--headless", "--path", str(ROOT), "--editor", "--quit"],
        capture_output=True, text=True, timeout=60,
    )
    if result.returncode:
        raise RuntimeError(result.stdout + result.stderr)


def modules():
    line = next(line for line in asset.read_text().splitlines() if line.startswith("ModulesJson = "))
    return json.loads(json.loads(line.split(" = ", 1)[1]))


try:
    manifest.write_text(json.dumps({"projectFileVersion": 3, "sourceFiles": ["*.yarn"], "baseLanguage": "en"}))
    main = fixture / "main.yarn"
    extra = fixture / "extra.yarn"
    main.write_text("title: DirectionProbe\nscene: probe_room\n---\n<<mark focus at:25,75>>\nHello. #line:directionprobe01\n===\n")
    extra.write_text("title: DirectionExtra\n---\nExtra. #line:directionprobe02\n===\n")
    import_project()
    assert {m["NodeId"] for m in modules()} == {"DirectionProbe", "DirectionExtra"}
    entry = next(m for m in modules() if m["NodeId"] == "DirectionProbe")
    assert entry["SceneEntry"]["SceneId"] == "probe_room"
    assert entry["SceneEntry"]["Line"] == 2
    assert entry["SourceMaps"][0]["Line"] == 4, "Source map must use authored lines"
    assert "SchemaVersion = 2" in asset.read_text()
    before = asset.read_bytes()
    import_project()
    assert asset.read_bytes() == before, "Unchanged import rewrote the companion"
    print("PASS: initial import and stable repeat", flush=True)

    main.write_text(main.read_text().replace("25,75", "40,60"))
    extra.unlink()
    import_project()
    result = modules()
    assert len(result) == 1 and result[0]["Marks"][0]["X"] == 40
    print("PASS: source update and deleted node", flush=True)

    good_source = main.read_text()
    main.write_text("title: DirectionProbe\n---\n<<if>>\n===\n")
    import_project()
    assert not asset.exists(), "Compilation failure retained stale Direction data"
    main.write_text(good_source)
    import_project()
    assert modules()[0]["Marks"][0]["X"] == 40
    print("PASS: compile failure invalidation and recovery", flush=True)

    good_source = main.read_text()
    main.write_text(good_source.replace("scene: probe_room", "scene: probe_room\nscene: duplicate"))
    import_project()
    assert not asset.exists(), "Duplicate scene header retained usable metadata"
    main.write_text(good_source)
    extra.write_text("title: DuplicateScene\nscene: probe_room\n---\nDuplicate #line:scene_duplicate\n===\n")
    import_project()
    assert not asset.exists(), "Duplicate scene entry retained usable metadata"
    extra.unlink()
    main.write_text(good_source.replace("<<mark focus", "<<__majo_node_entry>>\n<<mark focus"))
    import_project()
    assert not asset.exists(), "Authored reserved prelude retained usable metadata"
    main.write_text(good_source)
    import_project()
    assert asset.exists()
    print("PASS: scene entry validation and authored source mapping", flush=True)

    manifest.unlink()
    import_project()
    assert not asset.exists(), "Deleted manifest retained orphan companion"
    print("PASS: deleted manifest cleanup", flush=True)
finally:
    shutil.rmtree(fixture)

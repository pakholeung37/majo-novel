#!/usr/bin/env python3
"""Regression checks for installation, upgrades, and protection of game-owned files."""
import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import zipfile

ROOT = Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location("majo_manager", ROOT / "addons/majo_runtime/Tools/majo.py")
manager = importlib.util.module_from_spec(spec)
spec.loader.exec_module(manager)


class ManagerTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="majo-manager-test-")
        self.addCleanup(self.temp.cleanup)
        self.game = Path(self.temp.name) / "game"
        self.content = {
            "addons/majo_runtime/plugin.cfg": b'[plugin]\nversion="0.1.0"\n',
            "addons/YarnSpinner-Godot/plugin.cfg": b'[plugin]\nversion="0.3.22"\n',
            manager.IMPORTS[0]: b'<Project><PackageReference Include="YarnSpinner" Version="3.2.0"/></Project>',
            manager.IMPORTS[1]: b'<Project/>',
            "addons/majo_runtime/Runtime.cs": b"// original runtime\n",
            "addons/majo_runtime/old.cs": b"// deleted by upgrade\n",
        }
        base = ROOT / "addons/majo_runtime/Tools/Template"
        self.content.update({"addons/majo_runtime/Tools/Template/" + p.relative_to(base).as_posix(): p.read_bytes()
                             for p in base.rglob("*") if p.is_file()})
        manager.initialize(self.game, "MyGame", self.content, {"kind": "test"})

    def test_upgrade_removes_old_files_preserves_game_and_plugins(self):
        story = self.game / "assets/scripts/main.yarn"
        story.write_text("My own story")
        config = self.game / "project.godot"
        config.write_text(config.read_text().replace('enabled=PackedStringArray(', 'enabled=PackedStringArray("res://addons/my_plugin/plugin.cfg", '))
        updated = dict(self.content)
        del updated["addons/majo_runtime/old.cs"]
        updated["addons/majo_runtime/Runtime.cs"] = b"// new runtime\n"
        manager.install(self.game, updated, {"kind": "upgrade"})
        self.assertFalse((self.game / "addons/majo_runtime/old.cs").exists())
        self.assertEqual(story.read_text(), "My own story")
        self.assertIn("my_plugin", config.read_text())
        first = (self.game / "MyGame.csproj").read_bytes()
        manager.install(self.game, updated, {"kind": "upgrade"})
        self.assertEqual((self.game / "MyGame.csproj").read_bytes(), first)

    def test_failed_init_leaves_empty_destination_untouched(self):
        other = Path(self.temp.name) / "empty"
        other.mkdir()
        with patch.object(manager, "install", side_effect=OSError("disk failure")):
            with self.assertRaisesRegex(OSError, "disk failure"):
                manager.initialize(other, "Other", self.content, {})
        self.assertEqual(list(other.iterdir()), [])

    def test_checked_upgrade_failure_leaves_live_game_untouched(self):
        before = manager.files(self.game)
        updated = dict(self.content)
        updated["addons/majo_runtime/Runtime.cs"] = b"incompatible candidate"
        with patch.object(manager, "run_checks", side_effect=RuntimeError("game regression")):
            with self.assertRaisesRegex(RuntimeError, "game regression"):
                manager.checked_install(self.game, updated, {}, check=True)
        self.assertEqual(manager.files(self.game), before)

    def test_successful_upgrade_and_rollback_restore_exact_pin_preserve_story(self):
        before = (self.game / manager.LOCK).read_bytes()
        updated = dict(self.content)
        updated["addons/majo_runtime/Runtime.cs"] = b"new candidate"
        with patch.object(manager, "run_checks") as checks:
            manager.checked_install(self.game, updated, {}, check=True)
            self.assertNotEqual(checks.call_args[0][0], self.game)
            self.assertEqual(checks.call_args[0][0], checks.call_args[0][0].resolve())
        story = self.game / "assets/scripts/main.yarn"
        story.write_text("Game content edited after upgrade")
        manager.rollback(self.game)
        self.assertEqual((self.game / manager.LOCK).read_bytes(), before)
        self.assertEqual((self.game / "addons/majo_runtime/Runtime.cs").read_bytes(), self.content["addons/majo_runtime/Runtime.cs"])
        self.assertEqual(story.read_text(), "Game content edited after upgrade")

    def test_rollback_refuses_modified_host_configuration(self):
        manager.checked_install(self.game, self.content, {})
        settings = self.game / "project.godot"
        settings.write_text(settings.read_text() + "\n; user edit\n")
        with self.assertRaisesRegex(ValueError, "Host configuration changed"):
            manager.rollback(self.game)
        self.assertIn("user edit", settings.read_text())

    def test_api_boundary_change_requires_explicit_checked_migration(self):
        import json
        old = dict(self.content)
        old["addons/majo_runtime/compatibility.json"] = json.dumps({"apiVersion": "0.1"}).encode()
        manager.install(self.game, old, {})
        new = dict(old)
        new["addons/majo_runtime/compatibility.json"] = json.dumps({"apiVersion": "0.2"}).encode()
        before = (self.game / manager.LOCK).read_bytes()
        with self.assertRaisesRegex(ValueError, "Compatibility boundary changed"):
            manager.checked_install(self.game, new, {}, check=True)
        self.assertEqual((self.game / manager.LOCK).read_bytes(), before)

    def test_profile_uses_current_python_and_required_output(self):
        import json, sys
        (self.game / "majo-check.json").write_text(json.dumps({"format": 1, "commands": [
            {"argv": ["{python}", "test.py"], "requiredOutput": "GAME_OK"}]}))
        commands = manager.game_checks(self.game)
        self.assertEqual(commands[0]["argv"][0], sys.executable)
        self.assertEqual(commands[0]["requiredOutput"], "GAME_OK")

    def test_zero_exit_without_game_success_marker_is_rejected(self):
        import json, subprocess
        (self.game / "majo-check.json").write_text(json.dumps({"format": 1, "commands": [
            {"argv": ["test"], "requiredOutput": "GAME_OK"}]}))
        with patch.object(manager.subprocess, "run", return_value=subprocess.CompletedProcess([], 0, "MAJO_EXPORT_VALIDATED")):
            with self.assertRaisesRegex(RuntimeError, "Candidate validation failed"):
                manager.run_checks(self.game, __file__)

    def test_missing_solution_is_created_for_existing_host(self):
        (self.game / "MyGame.sln").unlink()
        manager.install(self.game, self.content, {})
        self.assertIn("MyGame.csproj", (self.game / "MyGame.sln").read_text())

    def test_local_runtime_edit_blocks_upgrade_before_any_changes(self):
        runtime = self.game / "addons/majo_runtime/Runtime.cs"
        runtime.write_text("local patch")
        lock = (self.game / manager.LOCK).read_bytes()
        with self.assertRaisesRegex(ValueError, "Local addon file differs"):
            manager.install(self.game, self.content, {"kind": "upgrade"})
        self.assertEqual(runtime.read_text(), "local patch")
        self.assertEqual((self.game / manager.LOCK).read_bytes(), lock)

    def test_unknown_extension_inside_addon_is_protected(self):
        custom = self.game / "addons/majo_runtime/MyExtension.cs"
        custom.write_text("custom extension")
        with self.assertRaisesRegex(ValueError, "Local addon file differs"):
            manager.install(self.game, self.content, {})
        self.assertTrue(custom.exists())

    def test_failed_config_write_rolls_back_addons_and_lock(self):
        updated = dict(self.content)
        updated["addons/majo_runtime/Runtime.cs"] = b"upgrade"
        before = manager.files(self.game)
        original = Path.write_bytes
        failed = False
        def fail_once(path, data):
            nonlocal failed
            if path == self.game / "MyGame.csproj" and not failed:
                failed = True
                raise OSError("simulated disk error")
            return original(path, data)
        with patch.object(Path, "write_bytes", fail_once):
            with self.assertRaisesRegex(OSError, "simulated disk error"):
                manager.install(self.game, updated, {})
        self.assertEqual(manager.files(self.game), before)

    def test_bad_host_config_does_not_replace_addons(self):
        project = self.game / "MyGame.csproj"
        project.write_text(project.read_text().replace("net8.0", "net9.0"))
        before = manager.files(self.game)
        with self.assertRaisesRegex(ValueError, "net8.0"):
            manager.install(self.game, self.content, {})
        self.assertEqual(manager.files(self.game), before)

    def test_watch_syncs_source_changes_without_changing_game_story(self):
        import subprocess
        import sys
        import time
        import signal
        source = Path(self.temp.name) / "system"
        for name, data in self.content.items():
            path = source / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(data)
        story = self.game / "assets/scripts/main.yarn"
        original_story = story.read_bytes()
        log = Path(self.temp.name) / "watch.log"
        with log.open("w") as output:
            proc = subprocess.Popen([sys.executable, str(ROOT / "addons/majo_runtime/Tools/majo.py"),
                                     "watch", "--source", str(source), "--project", str(self.game), "--no-build"],
                                    stdout=output, stderr=subprocess.STDOUT)
            try:
                def wait_for(test):
                    end = time.monotonic() + 10
                    while time.monotonic() < end:
                        if test(): return
                        if proc.poll() is not None: self.fail(log.read_text())
                        time.sleep(0.1)
                    self.fail(log.read_text())
                wait_for(lambda: "Watching source" in log.read_text())
                runtime = "addons/majo_runtime/Runtime.cs"
                (source / runtime).write_bytes(b"// live source change")
                wait_for(lambda: (self.game / runtime).read_bytes() == b"// live source change")
                self.assertEqual(story.read_bytes(), original_story)
            finally:
                proc.send_signal(signal.SIGINT)
                proc.wait(timeout=5)
        self.assertEqual(proc.returncode, 0, log.read_text())

    def test_archive_traversal_and_checksum_mismatch_are_rejected(self):
        archive = Path(self.temp.name) / "bad.zip"
        with zipfile.ZipFile(archive, "w") as z:
            z.writestr("addons/majo_runtime/../../../outside", b"bad")
        with self.assertRaisesRegex(ValueError, "Unsafe archive path"):
            manager.read_archive(archive, None)
        with self.assertRaisesRegex(ValueError, "SHA-256"):
            manager.read_archive(archive, "0" * 64)

    def test_lock_paths_cannot_escape_game(self):
        import json
        record = manager.load_lock(self.game)
        record["files"]["../outside"] = "0" * 64
        (self.game / manager.LOCK).write_text(json.dumps(record))
        with self.assertRaisesRegex(ValueError, "Invalid paths"):
            manager.load_lock(self.game)


if __name__ == "__main__":
    unittest.main()

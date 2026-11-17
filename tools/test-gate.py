#!/usr/bin/env python3
"""Check native export selection and actual subprocess success/failure evidence."""
import contextlib
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest

from platform_target import export_target
from runtime_test_gate import RuntimeTestGate


class PlatformTests(unittest.TestCase):
    def test_export_presets_match_native_dotnet_rids(self):
        for system, machine, preset, architecture, rid in [
            ("darwin", "arm64", "Consumer macOS", "arm64", "osx-arm64"),
            ("darwin", "x86_64", "Consumer macOS", "x86_64", "osx-x64"),
            ("linux", "aarch64", "Consumer Linux", "arm64", "linux-arm64"),
            ("linux", "x86_64", "Consumer Linux", "x86_64", "linux-x64"),
            ("win32", "AMD64", "Consumer Windows", "x86_64", "win-x64"),
            ("win32", "ARM64", "Consumer Windows", "arm64", "win-arm64"),
        ]:
            with self.subTest(system=system, machine=machine):
                self.assertEqual(export_target(system, machine), (preset, architecture, rid))

    def test_unknown_platforms_are_rejected_instead_of_falling_back_to_linux(self):
        for system, machine in [("freebsd", "x86_64"), ("win32", "x86"), ("linux", "riscv64")]:
            with self.subTest(system=system, machine=machine):
                with self.assertRaises(ValueError):
                    export_target(system, machine)


class GateTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="majo gate 中文 ")
        self.addCleanup(self.temp.cleanup)
        self.gate = RuntimeTestGate(Path(self.temp.name) / "evidence")
        self.log = io.StringIO()
        self.redirect = contextlib.redirect_stdout(self.log)
        self.redirect.__enter__()
        self.addCleanup(self.redirect.__exit__, None, None, None)

    def command(self, code, **options):
        return self.gate.run("probe", [sys.executable, "-c", code], **options)

    def test_zero_exit_without_required_marker_is_rejected(self):
        with self.assertRaises(RuntimeError):
            self.command("print('started')", required_output=("GAME_OK",))
        report = json.loads((self.gate.output / "results.json").read_text())
        self.assertFalse(report["passed"])
        self.assertFalse(report["steps"][0]["passed"])
        self.assertEqual(report["steps"][0]["exitCode"], 0)

    def test_intentional_failure_requires_nonzero_exit_and_marker(self):
        self.command("import sys; print('EXPECTED_FAILURE'); sys.exit(7)", expect_failure=True,
                     required_output=("EXPECTED_FAILURE",))
        for code in ["print('EXPECTED_FAILURE')", "import sys; sys.exit(7)"]:
            with self.assertRaises(RuntimeError):
                self.command(code, expect_failure=True, required_output=("EXPECTED_FAILURE",))

    def test_timeout_cannot_pass_as_an_expected_failure(self):
        with self.assertRaises(RuntimeError):
            self.command("import time; time.sleep(5)", timeout=0.1, expect_failure=True)
        record = self.gate.steps[-1]
        self.assertTrue(record["timedOut"])
        self.assertFalse(record["passed"])
        self.assertIn("exceeded", (self.gate.output / record["log"]).read_text())

    def test_paths_with_spaces_and_unicode_are_passed_without_a_shell(self):
        path = Path(self.temp.name) / "probe with spaces.py"
        path.write_text("import sys; print(sys.argv[1]); print('GAME_OK')", encoding="utf-8")
        result = self.gate.run("unicode probe", [sys.executable, path, "中文 $literal"], required_output=("GAME_OK",))
        self.assertIn("中文 $literal", result)
        self.gate.finish(True)
        self.assertTrue(json.loads((self.gate.output / "results.json").read_text())["passed"])


if __name__ == "__main__":
    unittest.main()

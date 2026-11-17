#!/usr/bin/env python3
"""Verify release selection/checksum checks before bootstrapping executable code."""
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch
import zipfile

ROOT = Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location("installer", ROOT / "install.py")
installer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(installer)


class InstallerTests(unittest.TestCase):
    def test_checksum_mismatch_never_executes_manager(self):
        with patch.object(sys, "argv", ["install.py", "init", "--project", "/unused", "--version", "v0.1.0-rc.1"]), \
             patch.object(installer, "download", side_effect=[b'0000000000000000000000000000000000000000000000000000000000000000  majo-runtime-0.1.0-rc.1-with-yarn.zip\n', b'bad']), \
             patch.object(installer.subprocess, "run") as execute:
            with self.assertRaisesRegex(ValueError, "checksum mismatch"):
                installer.main()
            execute.assert_not_called()

    def test_verified_release_bootstraps_and_records_exact_tag(self):
        stream = io.BytesIO()
        code = '''import json,sys\nfrom pathlib import Path\np=Path(sys.argv[sys.argv.index('--project')+1]);p.mkdir();(p/'majo.lock.json').write_text(json.dumps({'source':{}}))\n'''
        with zipfile.ZipFile(stream, "w") as z:
            z.writestr("addons/majo_runtime/Tools/majo.py", code)
            z.writestr("addons/majo_runtime/release-manifest.json", json.dumps({"version": "0.1.0-rc.1", "yarnBundled": True}))
        raw = stream.getvalue()
        sums = f'{hashlib.sha256(raw).hexdigest()}  majo-runtime-0.1.0-rc.1-with-yarn.zip\n'.encode()
        with tempfile.TemporaryDirectory() as temp:
            game = Path(temp) / "Game"
            with patch.object(sys, "argv", ["install.py", "init", "--project", str(game), "--version", "v0.1.0-rc.1"]), \
                 patch.object(installer, "download", side_effect=[sums, raw]):
                installer.main()
            record = json.loads((game / "majo.lock.json").read_text())
            self.assertEqual(record['source']['release'], 'v0.1.0-rc.1')


if __name__ == "__main__":
    unittest.main()

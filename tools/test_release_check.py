"""Release boundary tests with synthetic distribution files."""

import io
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
import zipfile

import check_public_repo
import check_release


class ReleaseTests(unittest.TestCase):
    def test_binary_parser_magic_is_not_image_metadata(self):
        self.assertFalse(check_public_repo.scan_blob("parser.dll", b"MZ\0parser Exif\0\0"))
        self.assertIn((1, "image-metadata"), check_public_repo.scan_blob("photo.jpg", b"\xff\xd8\xff\0Exif\0\0"))

    def test_private_symbols_are_rejected(self):
        with self.assertRaises(ValueError):
            check_release.check_files([("private.pdb", b"synthetic")])

    def test_archive_path_traversal_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "sample.zip"
            with zipfile.ZipFile(path, "w") as archive:
                archive.writestr("../outside.txt", "synthetic")
            with self.assertRaises(ValueError):
                list(check_release.zip_files(path))

    def test_checked_distribution_round_trips(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            package = root / "publish"
            package.mkdir()
            for name in check_release.REQUIRED:
                (package / name).write_bytes(b"MZ\0synthetic")
            archive = root / "package.zip"
            manifest = root / "manifest.json"
            script = Path(check_release.__file__).resolve()
            result = subprocess.run([sys.executable, str(script), "--directory", str(package), "--archive-out", str(archive), "--manifest", str(manifest), "--commit", "abc123"], capture_output=True, text=True)
            self.assertEqual(0, result.returncode, result.stderr)
            result = subprocess.run([sys.executable, str(script), "--zip", str(archive)], capture_output=True, text=True)
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertTrue(manifest.exists())

    def test_credential_in_installer_bytes_is_rejected_without_echo(self):
        token = ("gh" + "p_" + "Z" * 36).encode()
        with self.assertRaises(ValueError):
            check_release.check_files([("setup.exe", b"MZ\0" + token)])

    def test_chinese_diagnostics_work_with_a_legacy_output_encoding(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "synthetic.txt"
            path.write_text("PASSED\n合成界面测试\n", encoding="utf-8")
            env = os.environ.copy()
            env["PYTHONIOENCODING"] = "ascii"
            result = subprocess.run([sys.executable, str(Path(check_release.__file__).resolve()), "--text-file", str(path)], env=env, capture_output=True, text=True, encoding="utf-8")
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertIn("合成界面测试", result.stdout)

    def test_upstream_reference_requires_exact_content_and_path(self):
        with tempfile.TemporaryDirectory() as directory:
            data = b"MZ\0" + b"/home/" + b"synthetic-upstream/build/example.pdb\0"
            records = Path(directory) / "records.json"
            records.write_text(json.dumps([{"file":"upstream.dll", "sha256":hashlib.sha256(data).hexdigest(), "allowed_rules":["personal-path"]}]))
            with patch.object(check_release, "UPSTREAM_RECORDS", records):
                self.assertIn("verified_public_upstream_reference", check_release.check_files([("upstream.dll", data)])[0])
                for name, content in (("changed.dll", data), ("upstream.dll", data + b"changed")):
                    with self.assertRaises(ValueError):
                        check_release.check_files([(name, content)])

    def test_upstream_record_cannot_allow_credentials(self):
        with tempfile.TemporaryDirectory() as directory:
            data = b"MZ\0" + ("gh" + "p_" + "H" * 36).encode()
            records = Path(directory) / "records.json"
            records.write_text(json.dumps([{"file":"upstream.dll", "sha256":hashlib.sha256(data).hexdigest(), "allowed_rules":["github-token"]}]))
            with patch.object(check_release, "UPSTREAM_RECORDS", records):
                with self.assertRaises(ValueError):
                    check_release.check_files([("upstream.dll", data)])


if __name__ == "__main__":
    unittest.main()

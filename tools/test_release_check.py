"""Release boundary tests with synthetic distribution files."""

import io
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
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


if __name__ == "__main__":
    unittest.main()

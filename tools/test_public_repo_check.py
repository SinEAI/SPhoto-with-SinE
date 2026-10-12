"""Synthetic regression cases for the public-repository boundary."""

import importlib.util
import io
import os
from pathlib import Path
import shutil
import struct
import subprocess
import sys
import tempfile
import unittest
import zipfile


SCRIPT = Path(__file__).with_name("check_public_repo.py").resolve()
spec = importlib.util.spec_from_file_location("public_repo_check", SCRIPT)
check = importlib.util.module_from_spec(spec)
spec.loader.exec_module(check)


class ContentTests(unittest.TestCase):
    def rules(self, path, data):
        return {rule for _, rule in check.scan_blob(path, data)}

    def test_token_is_detected(self):
        self.assertIn("github-token", self.rules("sample.txt", ("gh" + "p_" + "A" * 36).encode()))

    def test_private_email_and_public_placeholders(self):
        private = b"person@" + b"mail.test"
        self.assertIn("private-email", self.rules("sample.txt", private))
        self.assertFalse(self.rules("sample.txt", b"person@example.com developer@users.noreply.github.com"))

    def test_personal_path_and_generic_runner(self):
        self.assertIn("personal-path", self.rules("sample.txt", b"/home/" + b"private-person/photo.jpg"))
        self.assertFalse(self.rules("sample.txt", b"/home/" + b"runner/work"))

    def test_quoted_json_credentials(self):
        data = b'{"api_key":"' + b"synthetic-secret-value" + b'"}'
        self.assertIn("credential-assignment", self.rules("sample.json", data))

    def test_sensitive_files_even_without_content(self):
        for path in (".env", ".env.local", "crash.log", "library.db", "private.pfx"):
            self.assertIn("sensitive-file", self.rules(path, b""))
        self.assertFalse(self.rules(".env.example", b""))

    def test_archive_contents_are_checked(self):
        buffer = io.BytesIO()
        with zipfile.ZipFile(buffer, "w", compression=zipfile.ZIP_DEFLATED) as archive:
            archive.writestr("sample.txt", "gh" + "p_" + "B" * 36)
        self.assertIn("archive:github-token", self.rules("wrapper.jar", buffer.getvalue()))

    def test_png_metadata_is_blocked(self):
        payload = b"Comment\0" + b"synthetic metadata"
        png = b"\x89PNG\r\n\x1a\n" + struct.pack(">I", len(payload)) + b"tEXt" + payload + b"\0" * 4
        self.assertIn("image-metadata", self.rules("icon.png", png))

    def test_utf16_email_is_checked(self):
        data = ("person@" + "mail.test").encode("utf-16le")
        self.assertIn("private-email", self.rules("sample.bin", data))

    def test_coauthor_must_use_github_even_for_example_email(self):
        self.assertIn((1, "non-github-trailer-identity"), check.issues_in_message(b"Co-authored-by: Example <person@example.com>"))

    def test_sensitive_filename_is_redacted(self):
        path = "person@" + "mail.test.txt"
        self.assertIn("filename:private-email", self.rules(path, b"safe"))
        self.assertNotIn("person@", check.display_path(path))

    def test_public_license_contacts_have_a_precise_exception(self):
        data = "\n".join(sorted(check.ZLIB_LICENSE_CONTACTS)).encode()
        self.assertFalse(self.rules("third-party-notices/zlib.txt", data))
        self.assertIn("private-email", self.rules("ordinary.txt", data))
        token = ("gh" + "p_" + "L" * 36).encode()
        self.assertIn("github-token", self.rules("third-party-notices/zlib.txt", data + b"\n" + token))
        for email in check.ZLIB_LICENSE_CONTACTS:
            self.assertFalse(check.public_identity(email))


class GitBoundaryTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.env = os.environ.copy()
        for name in list(self.env):
            if name.startswith("GIT_"):
                del self.env[name]
        self.env["GIT_CONFIG_NOSYSTEM"] = "1"
        self.env["GIT_CONFIG_GLOBAL"] = os.devnull
        self.git("init", "-q")
        self.git("config", "user.name", "Example")
        self.git("config", "user.email", "developer@users.noreply.github.com")

    def tearDown(self):
        self.temp.cleanup()

    def git(self, *args):
        return subprocess.run(["git", *args], cwd=self.root, env=self.env, check=True, capture_output=True)

    def run_check(self, *args, env=None):
        return subprocess.run([sys.executable, str(SCRIPT), *args], cwd=self.root, env=env or self.env, capture_output=True, text=True)

    def test_index_is_checked_instead_of_unstaged_worktree(self):
        sample = self.root / "sample.txt"
        sample.write_text("safe")
        self.git("add", "sample.txt")
        token = "gh" + "p_" + "C" * 36
        sample.write_text(token)
        self.assertEqual(0, self.run_check("--staged", "--identities").returncode)
        self.assertEqual(1, self.run_check("--worktree").returncode)
        self.git("add", "sample.txt")
        sample.write_text("safe")
        result = self.run_check("--staged")
        self.assertEqual(1, result.returncode)
        self.assertNotIn(token, result.stdout + result.stderr)

    def test_author_environment_override_is_checked(self):
        env = self.env.copy()
        env["GIT_AUTHOR_EMAIL"] = "private@example.com"
        self.assertEqual(1, self.run_check("--identities", env=env).returncode)

    def test_deleted_historical_secret_is_still_detected(self):
        sample = self.root / "sample.txt"
        token = "gh" + "p_" + "D" * 36
        sample.write_text(token)
        self.git("add", "sample.txt")
        self.git("commit", "-qm", "Synthetic first fixture")
        self.git("rm", "-q", "sample.txt")
        self.git("commit", "-qm", "Remove synthetic fixture")
        result = self.run_check("--history", "HEAD")
        self.assertEqual(1, result.returncode)
        self.assertNotIn(token, result.stdout + result.stderr)

    def test_commit_message_is_checked(self):
        message = self.root / "message"
        token = "gh" + "p_" + "E" * 36
        message.write_text("Synthetic message " + token)
        result = self.run_check("--message", str(message))
        self.assertEqual(1, result.returncode)
        self.assertNotIn(token, result.stdout + result.stderr)

    def test_untracked_content_is_checked(self):
        token = "gh" + "p_" + "F" * 36
        (self.root / "new.txt").write_text(token)
        result = self.run_check("--worktree")
        self.assertEqual(1, result.returncode)
        self.assertNotIn(token, result.stdout + result.stderr)

    def test_real_commit_hooks_block_content_and_message(self):
        hooks = self.root / ".githooks"
        hooks.mkdir()
        for name in ("pre-commit", "commit-msg", "run-public-check"):
            destination = hooks / name
            shutil.copyfile(SCRIPT.parent.parent / ".githooks" / name, destination)
            destination.chmod(0o755)
        (self.root / "tools").mkdir()
        shutil.copyfile(SCRIPT, self.root / "tools" / SCRIPT.name)
        self.git("config", "core.hooksPath", ".githooks")
        sample = self.root / "sample.txt"
        token = "gh" + "p_" + "G" * 36
        sample.write_text(token)
        self.git("add", "sample.txt")
        result = subprocess.run(["git", "commit", "-qm", "Synthetic blocked fixture"], cwd=self.root, env=self.env, capture_output=True, text=True)
        self.assertNotEqual(0, result.returncode)
        self.assertNotIn(token, result.stdout + result.stderr)
        sample.write_text("safe")
        self.git("add", "sample.txt")
        result = subprocess.run(["git", "commit", "-qm", token], cwd=self.root, env=self.env, capture_output=True, text=True)
        self.assertNotEqual(0, result.returncode)
        self.assertNotIn(token, result.stdout + result.stderr)
        self.git("commit", "-qm", "Synthetic allowed fixture")


if __name__ == "__main__":
    unittest.main()

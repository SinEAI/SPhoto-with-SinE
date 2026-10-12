#!/usr/bin/env python3
"""Fail closed on common public-repository privacy risks; never print matches."""

import argparse
import io
import os
from pathlib import Path, PurePosixPath
import re
import struct
import subprocess
import sys
import zipfile
import zlib


RULES = {
    "private-key": rb"-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----",
    "github-token": rb"\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,})\b",
    "cloud-key": rb"\b(?:AKIA|ASIA)[A-Z0-9]{16}\b",
    "service-token": rb"\b(?:sk-(?:proj-)?[A-Za-z0-9_-]{20,}|xox[baprs]-[A-Za-z0-9-]{15,}|AIza[A-Za-z0-9_-]{30,})\b",
    "credential-url": rb"(?i)https?://[^/\s:@]+:[^/\s@]+@",
    "credential-assignment": rb"(?im)\b(?:password|passwd|secret|api[_-]?key|access[_-]?token|client[_-]?secret)\b\s*[\"']?\s*[:=]\s*[\"']([^\"'\r\n]{6,})[\"']",
}
PATTERNS = {name: re.compile(pattern) for name, pattern in RULES.items()}
EMAIL = re.compile(rb"(?i)\b[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}\b")
IDENTITY_TRAILER = re.compile(rb"(?im)^(?:co-authored-by|signed-off-by|reviewed-by|acked-by|tested-by):[^\r\n]*<([^<>\r\n]+)>")
USER_PATH = re.compile(rb"(?i)(?:[A-Z]:[\\/]+Users[\\/]+|/(?:Users|home)/)([^/\\\s\"'<>]+)")
OFFICIAL_EMAILS = {"noreply@github.com", "web-flow@github.com"}
EXAMPLE_DOMAINS = {"example.com", "example.org", "example.net"}
GENERIC_USERS = {b"runner", b"runneradmin", b"agent", b"user", b"username", b"example", b"public"}
PLACEHOLDERS = {b"placeholder", b"redacted", b"example", b"changeme", b"<redacted>"}
SENSITIVE_SUFFIXES = {".log", ".dmp", ".pdb", ".db", ".sqlite", ".sqlite3", ".pfx", ".p12", ".pem", ".key", ".jks", ".keystore"}
MAX_MEMBER = 20 * 1024 * 1024
MAX_ARCHIVE = 100 * 1024 * 1024


def git(*args):
    return subprocess.check_output(["git", *args], stderr=subprocess.PIPE)


def public_identity(email):
    email = email.lower()
    return bool(re.fullmatch(r"[a-z0-9+_.\[\]-]+@users\.noreply\.github\.com", email)) or email in OFFICIAL_EMAILS


def issues_in_text(data):
    issues = []
    for name, pattern in PATTERNS.items():
        for match in pattern.finditer(data):
            if name == "credential-assignment":
                value = match.group(1).lower()
                if value in PLACEHOLDERS or value.startswith((b"${", b"{{", b"<")):
                    continue
            issues.append((data[:match.start()].count(b"\n") + 1, name))
    for match in EMAIL.finditer(data):
        email = match.group().decode("ascii").lower()
        if not public_identity(email) and email.rsplit("@", 1)[-1] not in EXAMPLE_DOMAINS:
            issues.append((data[:match.start()].count(b"\n") + 1, "private-email"))
    for match in USER_PATH.finditer(data):
        username = match.group(1).lower()
        if username not in GENERIC_USERS and not username.startswith((b"$", b"%", b"{")):
            issues.append((data[:match.start()].count(b"\n") + 1, "personal-path"))
    return sorted(set(issues))


def issues_in_message(data):
    issues = issues_in_text(data)
    for match in IDENTITY_TRAILER.finditer(data):
        if not public_identity(match.group(1).decode("utf-8", "replace")):
            issues.append((data[:match.start()].count(b"\n") + 1, "non-github-trailer-identity"))
    return sorted(set(issues))


def display_path(path):
    data = os.fsencode(path)
    for pattern in [*PATTERNS.values(), EMAIL, USER_PATH]:
        data = pattern.sub(b"<redacted>", data)
    return os.fsdecode(data)


def scan_blob(path, data, depth=0):
    issues = issues_in_text(data)
    issues.extend((1, "filename:" + rule) for _, rule in issues_in_text(os.fsencode(path)))
    name = PurePosixPath(path).name.lower()
    suffix = PurePosixPath(path).suffix.lower()
    if (suffix in SENSITIVE_SUFFIXES or name in {"settings.json", "prefs.json", "id_rsa", "id_ed25519", "credentials"}
            or (name == ".env" or name.startswith(".env.")) and name not in {".env.example", ".env.template"}
            or any(part in {".aws", ".ssh"} for part in PurePosixPath(path).parts)):
        issues.append((1, "sensitive-file"))
    if b"\0" in data:
        issues.extend(issues_in_text(data.replace(b"\0", b"")))
        is_image = data.startswith((b"\xff\xd8\xff", b"II*\0", b"MM\0*")) or data[:4] == b"RIFF" and data[8:12] == b"WEBP"
        if is_image and (b"Exif\0\0" in data or b"http://ns.adobe.com/xap/1.0/" in data):
            issues.append((1, "image-metadata"))
    if data.startswith(b"\x89PNG\r\n\x1a\n"):
        offset = 8
        while offset + 12 <= len(data):
            size = struct.unpack_from(">I", data, offset)[0]
            kind = data[offset + 4:offset + 8]
            if offset + 12 + size > len(data):
                issues.append((1, "invalid-png"))
                break
            if kind in {b"tEXt", b"zTXt", b"iTXt", b"eXIf"}:
                issues.append((1, "image-metadata"))
            offset += 12 + size
    if data.startswith(b"\0\0\x01\0") and len(data) >= 6:
        count = struct.unpack_from("<H", data, 4)[0]
        if 6 + count * 16 > len(data):
            issues.append((1, "invalid-ico"))
        else:
            for index in range(count):
                size, offset = struct.unpack_from("<II", data, 6 + index * 16 + 8)
                if offset + size > len(data):
                    issues.append((1, "invalid-ico"))
                elif depth >= 3:
                    issues.append((1, "image-depth-limit"))
                else:
                    issues.extend(scan_blob("embedded-image", data[offset:offset + size], depth + 1))
    if data.startswith(b"PK\x03\x04"):
        if depth >= 3:
            issues.append((1, "archive-depth-limit"))
        else:
            try:
                with zipfile.ZipFile(io.BytesIO(data)) as archive:
                    members = archive.infolist()
                    if sum(member.file_size for member in members) > MAX_ARCHIVE:
                        issues.append((1, "archive-size-limit"))
                    else:
                        for member in members:
                            if member.is_dir():
                                continue
                            if member.file_size > MAX_MEMBER:
                                issues.append((1, "archive-member-size-limit"))
                                continue
                            for _, rule in scan_blob(member.filename, archive.read(member), depth + 1):
                                issues.append((1, "archive:" + rule))
            except (zipfile.BadZipFile, RuntimeError, OSError, ValueError, NotImplementedError, zlib.error):
                issues.append((1, "unreadable-archive"))
    return sorted(set(issues))


def indexed_blobs():
    for entry in git("ls-files", "--stage", "-z").split(b"\0"):
        if not entry:
            continue
        info, path = entry.split(b"\t", 1)
        _, oid, stage = info.split()
        if stage != b"0":
            raise ValueError("unmerged index")
        yield path.decode("utf-8", "surrogateescape"), oid.decode("ascii")


def revision_blobs(revision):
    for entry in git("ls-tree", "-rz", revision).split(b"\0"):
        if entry:
            info, path = entry.split(b"\t", 1)
            _, kind, oid = info.split()
            if kind == b"blob":
                yield path.decode("utf-8", "surrogateescape"), oid.decode("ascii")
            else:
                raise ValueError("unreviewed submodule")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--staged", action="store_true")
    parser.add_argument("--worktree", action="store_true")
    parser.add_argument("--history", metavar="REV")
    parser.add_argument("--rev-range", metavar="BASE..HEAD")
    parser.add_argument("--identities", action="store_true")
    parser.add_argument("--message", type=Path)
    args = parser.parse_args()
    if not any(vars(args).values()):
        parser.error("select a snapshot, history, identities or message")
    failures = set()
    seen = set()
    blobs = 0
    commits = 0

    def inspect(path, data, context):
        nonlocal blobs
        blobs += 1
        for line, rule in scan_blob(path, data):
            failures.add((context, path, line, rule))

    def inspect_git(path, oid, context):
        # The path is part of the policy, even when content is shared.
        key = (path, oid)
        if key not in seen:
            seen.add(key)
            inspect(path, git("cat-file", "blob", oid), context)

    try:
        if args.identities:
            for kind in ("AUTHOR", "COMMITTER"):
                ident = git("var", "GIT_" + kind + "_IDENT").decode("utf-8", "replace")
                match = re.search(r"<([^<>]+)>", ident)
                if not match or not public_identity(match.group(1)):
                    failures.add(("identity", kind.lower(), 1, "non-github-identity"))
        if args.message:
            for line, rule in issues_in_message(args.message.read_bytes()):
                failures.add(("message", "commit-message", line, rule))
        if args.staged or args.worktree:
            root = Path(os.fsdecode(git("rev-parse", "--show-toplevel")).strip())
            for path, oid in indexed_blobs():
                if args.staged:
                    inspect_git(path, oid, "index")
                if args.worktree:
                    item = root / path
                    if item.is_symlink():
                        inspect(path, os.fsencode(os.readlink(item)), "worktree")
                    elif item.exists():
                        inspect(path, item.read_bytes(), "worktree")
            if args.worktree:
                for raw_path in git("ls-files", "--others", "--exclude-standard", "-z").split(b"\0"):
                    if not raw_path:
                        continue
                    path = raw_path.decode("utf-8", "surrogateescape")
                    item = root / path
                    data = os.fsencode(os.readlink(item)) if item.is_symlink() else item.read_bytes()
                    inspect(path, data, "untracked")
        revision = args.rev_range or args.history
        if revision:
            for sha in git("rev-list", "--end-of-options", revision).decode("ascii").splitlines():
                commits += 1
                author, committer, message = git("show", "-s", "--format=%ae%x00%ce%x00%B", sha).split(b"\0", 2)
                for kind, email in (("author", author), ("committer", committer)):
                    if not public_identity(email.decode("utf-8", "replace")):
                        failures.add((sha[:12], kind, 1, "non-github-identity"))
                for line, rule in issues_in_message(message):
                    failures.add((sha[:12], "commit-message", line, rule))
                for path, oid in revision_blobs(sha):
                    inspect_git(path, oid, sha[:12])
    except (subprocess.CalledProcessError, OSError, ValueError) as error:
        print("Public repository check could not complete (" + type(error).__name__ + ").", file=sys.stderr)
        return 2
    for context, path, line, rule in sorted(failures):
        # repr also escapes control characters in untrusted filenames.
        print(f"{context}: {display_path(path)!r}:{line}: {rule}", file=sys.stderr)
    print(f"Public repository check: {blobs} blobs, {commits} commits, {len(failures)} issues.")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())

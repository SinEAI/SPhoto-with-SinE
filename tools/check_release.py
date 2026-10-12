#!/usr/bin/env python3
"""Check distribution contents and write a digest manifest before publishing."""

import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import sys
import zipfile

from check_public_repo import display_path, issues_in_text, scan_blob


REQUIRED = {"SPhoto.exe", "SPhoto.dll", "sphoto_core.dll", "libEGL.dll", "libGLESv2.dll", "e_sqlite3.dll"}
UPSTREAM_RECORDS = Path(__file__).with_name("public-upstream-references.json")


def check_files(files):
    failed = False
    manifest = []
    records = json.loads(UPSTREAM_RECORDS.read_text(encoding="utf-8")) if UPSTREAM_RECORDS.exists() else []
    for name, data in files:
        digest = hashlib.sha256(data).hexdigest()
        verified = next((record for record in records if record["file"] == name and record["sha256"] == digest), None)
        allowed = set(verified["allowed_rules"]) & {"personal-path", "image-metadata"} if verified else set()
        for line, rule in scan_blob(name, data):
            if rule in allowed:
                continue
            print(f"{display_path(name)!r}:{line}: {rule}", file=sys.stderr)
            failed = True
        entry = {"path": name, "bytes": len(data), "sha256": digest}
        if verified:
            entry["verified_public_upstream_reference"] = verified
        manifest.append(entry)
    if failed:
        raise ValueError("release privacy checks failed")
    return manifest


def folder_files(root):
    for path in sorted(root.rglob("*")):
        if path.is_symlink():
            raise ValueError("distribution contains a symlink")
        if path.is_file():
            yield path.relative_to(root).as_posix(), path.read_bytes()


def zip_files(path):
    seen = set()
    with zipfile.ZipFile(path) as archive:
        for member in archive.infolist():
            if member.is_dir():
                continue
            name = PurePosixPath(member.filename)
            if name.is_absolute() or ".." in name.parts or "\\" in member.filename or member.filename in seen:
                raise ValueError("unsafe or duplicate archive path")
            seen.add(member.filename)
            yield member.filename, archive.read(member)


def main():
    # GitHub's Windows output pipe may otherwise select a legacy code page.
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description=__doc__)
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument("--directory", type=Path)
    group.add_argument("--zip", type=Path)
    group.add_argument("--text-file", type=Path)
    parser.add_argument("--manifest", type=Path)
    parser.add_argument("--archive-out", type=Path)
    parser.add_argument("--commit", default="")
    args = parser.parse_args()
    try:
        if args.text_file:
            data = args.text_file.read_bytes()
            issues = issues_in_text(data)
            for line, rule in issues:
                print(f"diagnostic:{line}: {rule}", file=sys.stderr)
            if issues:
                raise ValueError("diagnostic text privacy checks failed")
            print(data.decode("utf-8-sig", "replace"))
            return 0
        if args.directory:
            if not args.directory.is_dir():
                raise ValueError("missing distribution directory")
            manifest = check_files(folder_files(args.directory))
        else:
            manifest = check_files(zip_files(args.zip))
        names = {entry["path"] for entry in manifest}
        if not REQUIRED.issubset(names):
            raise ValueError("distribution is missing required binaries")
        if args.archive_out:
            if not args.directory:
                raise ValueError("archive creation requires a directory")
            args.archive_out.parent.mkdir(parents=True, exist_ok=True)
            with zipfile.ZipFile(args.archive_out, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
                for entry in manifest:
                    path = args.directory / entry["path"]
                    data = path.read_bytes()
                    if hashlib.sha256(data).hexdigest() != entry["sha256"]:
                        raise ValueError("distribution changed during packaging")
                    archive.writestr(entry["path"], data)
        if args.manifest:
            args.manifest.parent.mkdir(parents=True, exist_ok=True)
            args.manifest.write_text(json.dumps({"source_commit": args.commit, "privacy_check": "passed", "files": manifest}, indent=2) + "\n", encoding="utf-8")
        print(f"Release privacy check passed: {len(manifest)} files.")
        return 0
    except (OSError, ValueError, zipfile.BadZipFile, RuntimeError) as error:
        # No exception paths or matched values are sent to public build logs.
        print("Release check failed (" + type(error).__name__ + ").", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())

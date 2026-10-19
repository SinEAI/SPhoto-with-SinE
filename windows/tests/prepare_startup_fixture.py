"""Build a synthetic library for first-window tests, optionally unindexed.

Only schema definitions and user_version are read from the UI test's database.
No source rows, images, preferences, or thumbnail cache are copied.
"""

import argparse
import hashlib
import json
import os
from pathlib import Path
import sqlite3
import struct
import sys
import zlib


COUNT = 600
WIDTH, HEIGHT = 1280, 960
EDGE_BYTES = 32 * 1024  # SPhoto.Library/Fingerprint.cs
UNIX_EPOCH_TICKS = 621355968000000000
FIRST_MTIME_SECONDS = 1704067200  # 2024-01-01 UTC
PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"
TABLES = {"files", "analysis", "tags", "pages", "page_tags", "stack_overrides", "versions"}


class FixtureError(Exception):
    pass


def read_schema(path):
    with sqlite3.connect(path.resolve().as_uri() + "?mode=ro", uri=True) as source:
        source.execute("PRAGMA query_only=ON")
        version = source.execute("PRAGMA user_version").fetchone()[0]
        definitions = source.execute(
            "SELECT type, name, tbl_name, sql FROM sqlite_master "
            "WHERE sql IS NOT NULL ORDER BY CASE type WHEN 'table' THEN 0 ELSE 1 END, name"
        ).fetchall()
        if version != 3:
            raise FixtureError("the template must use the current library schema (version 3)")
        if {name for kind, name, _, _ in definitions if kind == "table"} != TABLES:
            raise FixtureError("the template has an unexpected library table structure")
        if any(kind not in {"table", "index"} or table not in TABLES for kind, _, table, _ in definitions):
            raise FixtureError("the template must contain only library tables and indexes")
        expected = {
            "files": {"path", "size", "mtime_ticks", "taken_ticks", "exif_taken", "camera", "fingerprint"},
            "analysis": {"fingerprint", "dhash", "sharpness", "exposure"},
        }
        for table, columns in expected.items():
            actual = {row[1] for row in source.execute(f'PRAGMA table_info("{table}")')}
            if actual != columns:
                raise FixtureError("the template has unsupported file or analysis columns")
        return version, [sql for _, _, _, sql in definitions]


def chunk(kind, payload):
    return struct.pack(">I", len(payload)) + kind + payload + struct.pack(">I", zlib.crc32(kind + payload) & 0xFFFFFFFF)


def photo_png(index):
    # Four colored quadrants and a geometric stripe, with no EXIF or camera.
    # tEXt makes every file's content unique even when colors repeat.
    colors = [bytes(((index * 37 + i * 61) % 192 + 32,
                     (index * 53 + i * 43) % 192 + 32,
                     (index * 19 + i * 79) % 192 + 32)) for i in range(4)]
    upper = b"\0" + colors[0] * (WIDTH // 2) + colors[1] * (WIDTH // 2)
    lower = b"\0" + colors[2] * (WIDTH // 2) + colors[3] * (WIDTH // 2)
    stripe = b"\0" + b"\xff\xff\xff" * WIDTH
    pixels = upper * (HEIGHT // 2 - 4) + stripe * 8 + lower * (HEIGHT // 2 - 4)
    header = struct.pack(">IIBBBBB", WIDTH, HEIGHT, 8, 2, 0, 0, 0)
    return (PNG_SIGNATURE + chunk(b"IHDR", header)
            + chunk(b"tEXt", b"fixture-index\0" + str(index).encode("ascii"))
            + chunk(b"IDAT", zlib.compress(pixels, 6)) + chunk(b"IEND", b""))


def fingerprint(content):
    # The C# implementation hashes signed Int64 length in little-endian,
    # followed by the entire file below 64 KiB, else its two 32 KiB ends.
    edges = content if len(content) < 2 * EDGE_BYTES else content[:EDGE_BYTES] + content[-EDGE_BYTES:]
    return hashlib.sha256(struct.pack("<q", len(content)) + edges).hexdigest()


def prepare(schema, root, unindexed=False):
    if root.is_symlink() or (root.exists() and (not root.is_dir() or any(root.iterdir()))):
        raise FixtureError("fixture root must be a new or empty directory")
    version, definitions = read_schema(schema)
    root = root.resolve()
    root.mkdir(parents=True, exist_ok=True)
    photos, data = root / "photos", root / "data"
    photos.mkdir()
    data.mkdir()
    with sqlite3.connect(data / "library.db") as target:
        for definition in definitions:
            target.execute(definition)
        target.execute(f"PRAGMA user_version={version}")
        for index in range(COUNT):
            content = photo_png(index)
            path = photos / f"synthetic-{index:04d}.png"
            path.write_bytes(content)
            stamp_ns = (FIRST_MTIME_SECONDS + index * 3600) * 1000000000
            os.utime(path, ns=(stamp_ns, stamp_ns))
            info = path.stat()
            identity = fingerprint(content)
            ticks = UNIX_EPOCH_TICKS + info.st_mtime_ns // 100
            if unindexed:
                continue
            target.execute(
                "INSERT INTO files(path, size, mtime_ticks, taken_ticks, exif_taken, camera, fingerprint) "
                "VALUES (?, ?, ?, NULL, 0, NULL, ?)",
                (str(path), info.st_size, ticks, identity),
            )
            # Mark these synthetic fixtures indexed so analysis cannot change
            # the first-window list while this thumbnail regression runs.
            target.execute(
                "INSERT INTO analysis(fingerprint, dhash, sharpness, exposure) VALUES (?, 0, 1.0, 0.5)",
                (identity,),
            )
        if target.execute("PRAGMA integrity_check").fetchone()[0] != "ok":
            raise FixtureError("generated library failed its integrity check")
        if not unindexed and target.execute("SELECT COUNT(DISTINCT fingerprint) FROM files").fetchone()[0] != COUNT:
            raise FixtureError("generated photos must have distinct fingerprints")
    (data / "settings.json").write_text(json.dumps({"Folders": [str(photos)]}), encoding="utf-8")
    (data / "prefs.json").write_text(json.dumps({"Design": "main"}), encoding="utf-8")
    print(f"Prepared {COUNT} synthetic {WIDTH}x{HEIGHT} PNG photos; {'empty' if unindexed else 'populated'} index.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--schema", type=Path, required=True, help="database created by the small synthetic UI test")
    parser.add_argument("--root", type=Path, required=True, help="new or empty synthetic fixture directory")
    parser.add_argument("--unindexed", action="store_true", help="exercise a newly added folder with an empty index")
    args = parser.parse_args()
    try:
        prepare(args.schema, args.root, args.unindexed)
    except FixtureError as error:
        print(f"Fixture preparation failed: {error}", file=sys.stderr)
        return 1
    except (OSError, sqlite3.Error):
        # Do not echo supplied paths, filenames or database contents.
        print("Fixture preparation failed while reading the schema or writing the synthetic fixture.", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())

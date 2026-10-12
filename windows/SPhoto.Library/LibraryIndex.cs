using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace SPhoto.Library;

/// <summary>
/// Everything the library knows, in one SQLite file: the files under its
/// folders (so a later start reads only what changed), what was measured
/// about each photo, and what the user organised by hand (tags, pages,
/// stack choices, versions). User data is keyed by fingerprint, so it
/// follows a photo that is moved or renamed. Tags and pages never touch
/// the photo files themselves.
///
/// One connection guarded by a lock: safe to call from any thread, and
/// every call is short.
/// </summary>
public sealed class LibraryIndex : IDisposable
{
    /// Bumped with every schema change; Migrate brings older files forward.
    public const int SchemaVersion = 3;

    private readonly object _gate = new();
    private SqliteConnection? _db;
    private SqliteTransaction? _tx;

    private LibraryIndex(SqliteConnection db) => _db = db;

    /// <summary>
    /// Opens (or creates) the index at dbPath; ":memory:" gives a private
    /// in-memory index. Throws when the file is not a usable database.
    /// </summary>
    public static LibraryIndex Open(string dbPath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            // A pooled connection keeps the file open after Dispose, which
            // stops tests and "reset library" from deleting it.
            Pooling = false,
        };
        var db = new SqliteConnection(builder.ToString());
        try
        {
            db.Open();
            var index = new LibraryIndex(db);
            index.Exec("PRAGMA journal_mode=WAL");
            // WAL with NORMAL survives an app crash; only a power cut can
            // lose the last moments, and those are a rescan away.
            index.Exec("PRAGMA synchronous=NORMAL");
            index.Exec("PRAGMA busy_timeout=5000");
            index.Migrate();
            return index;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    private void Migrate()
    {
        long version = Scalar<long>("PRAGMA user_version");
        if (version > SchemaVersion)
        {
            throw new InvalidOperationException($"library.db is from a newer SPhoto (schema {version})");
        }
        if (version < 1)
        {
            InTransaction(() =>
            {
                Exec("""
                    CREATE TABLE files(
                      path TEXT PRIMARY KEY,
                      size INTEGER NOT NULL,
                      mtime_ticks INTEGER NOT NULL,
                      taken_ticks INTEGER NULL,
                      exif_taken INTEGER NOT NULL DEFAULT 0,
                      camera TEXT NULL,
                      fingerprint TEXT NULL);
                    CREATE INDEX files_fingerprint ON files(fingerprint);
                    CREATE TABLE analysis(
                      fingerprint TEXT PRIMARY KEY,
                      dhash INTEGER NULL,
                      sharpness REAL NULL,
                      exposure REAL NULL);
                    CREATE TABLE tags(
                      fingerprint TEXT NOT NULL,
                      name TEXT NOT NULL,
                      source TEXT NOT NULL CHECK(source IN ('manual', 'auto')),
                      created INTEGER NOT NULL,
                      PRIMARY KEY(fingerprint, name));
                    CREATE INDEX tags_name ON tags(name);
                    CREATE TABLE albums(
                      id INTEGER PRIMARY KEY,
                      name TEXT NOT NULL,
                      created INTEGER NOT NULL,
                      sort INTEGER NOT NULL);
                    CREATE TABLE album_items(
                      album_id INTEGER NOT NULL,
                      fingerprint TEXT NOT NULL,
                      added INTEGER NOT NULL,
                      PRIMARY KEY(album_id, fingerprint));
                    CREATE TABLE stack_overrides(
                      fingerprint TEXT PRIMARY KEY,
                      manual_group TEXT NULL,
                      keep_apart INTEGER NOT NULL DEFAULT 0,
                      is_cover INTEGER NOT NULL DEFAULT 0);
                    CREATE TABLE versions(
                      version_fingerprint TEXT PRIMARY KEY,
                      original_fingerprint TEXT NOT NULL,
                      created INTEGER NOT NULL);
                    PRAGMA user_version = 1;
                    """);
            });
        }
        if (version < 2)
        {
            // Albums gave way to pages: named sets of tags (nothing released had albums).
            InTransaction(() =>
            {
                Exec("""
                    DROP TABLE IF EXISTS album_items;
                    DROP TABLE IF EXISTS albums;
                    CREATE TABLE pages(
                      id INTEGER PRIMARY KEY,
                      name TEXT NOT NULL,
                      created INTEGER NOT NULL,
                      sort INTEGER NOT NULL,
                      archived INTEGER NOT NULL DEFAULT 0);
                    CREATE TABLE page_tags(
                      page_id INTEGER NOT NULL,
                      tag TEXT NOT NULL,
                      sort INTEGER NOT NULL,
                      PRIMARY KEY(page_id, tag));
                    PRAGMA user_version = 2;
                    """);
            });
        }
        if (version < 3)
        {
            // Each tag's card on a page can be small, medium or large.
            InTransaction(() =>
            {
                Exec("""
                    ALTER TABLE page_tags ADD COLUMN size INTEGER NOT NULL DEFAULT 0;
                    PRAGMA user_version = 3;
                    """);
            });
        }
        // Later versions: if (version < 4) { ALTER ...; PRAGMA user_version = 4; }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _db?.Dispose();
            _db = null;
        }
    }

    // ----- Files -----

    /// The record for path, or null when the index has never seen it.
    public FileRecord? Known(string path)
    {
        lock (_gate)
        {
            using var cmd = Command("SELECT path, size, mtime_ticks, taken_ticks, exif_taken, camera, fingerprint FROM files WHERE path = $p",
                                    ("$p", path));
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadFile(r) : null;
        }
    }

    /// Every file record, by path: what a scan compares the disk against.
    public Dictionary<string, FileRecord> AllFiles()
    {
        lock (_gate)
        {
            var all = new Dictionary<string, FileRecord>(StringComparer.Ordinal);
            using var cmd = Command("SELECT path, size, mtime_ticks, taken_ticks, exif_taken, camera, fingerprint FROM files");
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var f = ReadFile(r);
                all[f.Path] = f;
            }
            return all;
        }
    }

    private static FileRecord ReadFile(SqliteDataReader r) => new(
        r.GetString(0), r.GetInt64(1), r.GetInt64(2),
        r.IsDBNull(3) ? null : new DateTime(r.GetInt64(3)),
        r.GetInt64(4) != 0,
        r.IsDBNull(5) ? null : r.GetString(5),
        r.IsDBNull(6) ? null : r.GetString(6));

    public void Upsert(FileRecord file) => UpsertMany(new[] { file });

    /// Writes the records in one transaction (thousands per second).
    public void UpsertMany(IEnumerable<FileRecord> files)
    {
        lock (_gate)
        {
            InTransaction(() =>
            {
                using var cmd = Command("""
                    INSERT INTO files(path, size, mtime_ticks, taken_ticks, exif_taken, camera, fingerprint)
                    VALUES($path, $size, $mtime, $taken, $exif, $camera, $fp)
                    ON CONFLICT(path) DO UPDATE SET size = excluded.size, mtime_ticks = excluded.mtime_ticks,
                      taken_ticks = excluded.taken_ticks, exif_taken = excluded.exif_taken,
                      camera = excluded.camera, fingerprint = excluded.fingerprint
                    """);
                var path = cmd.Parameters.Add("$path", SqliteType.Text);
                var size = cmd.Parameters.Add("$size", SqliteType.Integer);
                var mtime = cmd.Parameters.Add("$mtime", SqliteType.Integer);
                var taken = cmd.Parameters.Add("$taken", SqliteType.Integer);
                var exif = cmd.Parameters.Add("$exif", SqliteType.Integer);
                var camera = cmd.Parameters.Add("$camera", SqliteType.Text);
                var fp = cmd.Parameters.Add("$fp", SqliteType.Text);
                cmd.Prepare();
                foreach (var f in files)
                {
                    path.Value = f.Path;
                    size.Value = f.Size;
                    mtime.Value = f.MtimeTicks;
                    taken.Value = f.Taken is { } t ? t.Ticks : DBNull.Value;
                    exif.Value = f.ExifTaken ? 1 : 0;
                    camera.Value = (object?)f.Camera ?? DBNull.Value;
                    fp.Value = (object?)f.Fingerprint ?? DBNull.Value;
                    cmd.ExecuteNonQuery();
                }
            });
        }
    }

    /// <summary>
    /// Forgets files under roots that are not in presentPaths (deleted or
    /// moved away). Tags and the rest stay with the fingerprint, so
    /// a photo that comes back finds them again. Returns how many went.
    /// </summary>
    public int RemoveMissing(IEnumerable<string> presentPaths, IReadOnlyList<string> roots)
    {
        var present = presentPaths as ISet<string> ?? new HashSet<string>(presentPaths, StringComparer.Ordinal);
        return RemoveWhere(path => !present.Contains(path) && roots.Any(root => LibraryPaths.IsInside(path, root)));
    }

    /// Forgets files outside every root: a folder the user removed from the library.
    public int ForgetOutside(IReadOnlyList<string> roots) =>
        RemoveWhere(path => !roots.Any(root => LibraryPaths.IsInside(path, root)));

    private int RemoveWhere(Func<string, bool> gone)
    {
        lock (_gate)
        {
            var doomed = new List<string>();
            using (var list = Command("SELECT path FROM files"))
            using (var r = list.ExecuteReader())
            {
                while (r.Read())
                {
                    var path = r.GetString(0);
                    if (gone(path)) doomed.Add(path);
                }
            }
            if (doomed.Count == 0) return 0;
            InTransaction(() =>
            {
                using var cmd = Command("DELETE FROM files WHERE path = $p");
                var p = cmd.Parameters.Add("$p", SqliteType.Text);
                foreach (var path in doomed)
                {
                    p.Value = path;
                    cmd.ExecuteNonQuery();
                }
            });
            return doomed.Count;
        }
    }

    // ----- Fingerprints -----

    /// <summary>
    /// Up to limit files still without a fingerprint, in path order after
    /// the path given: a cursor, so files that cannot be read are passed
    /// over instead of being asked for again and again.
    /// </summary>
    public IReadOnlyList<FileRecord> PendingFingerprints(int limit, string? after = null)
    {
        lock (_gate)
        {
            using var cmd = Command("""
                SELECT path, size, mtime_ticks, taken_ticks, exif_taken, camera, fingerprint FROM files
                WHERE fingerprint IS NULL AND path > $after ORDER BY path LIMIT $limit
                """, ("$after", after ?? ""), ("$limit", limit));
            using var r = cmd.ExecuteReader();
            var list = new List<FileRecord>();
            while (r.Read()) list.Add(ReadFile(r));
            return list;
        }
    }

    /// <summary>
    /// Stores the fingerprint of path. With size and mtime, only when the
    /// row still describes the file that was hashed (a scan may have seen a
    /// newer version meanwhile). Returns whether a row changed.
    /// </summary>
    public bool SetFingerprint(string path, string fingerprint, long? size = null, long? mtimeTicks = null) =>
        SetFingerprints(new[] { (path, fingerprint, size, mtimeTicks) }) == 1;

    /// Many SetFingerprint calls in one transaction.
    public int SetFingerprints(IEnumerable<(string Path, string Fingerprint, long? Size, long? MtimeTicks)> items)
    {
        lock (_gate)
        {
            int changed = 0;
            InTransaction(() =>
            {
                using var cmd = Command("""
                    UPDATE files SET fingerprint = $fp WHERE path = $p
                      AND ($size IS NULL OR size = $size) AND ($mtime IS NULL OR mtime_ticks = $mtime)
                    """);
                var fp = cmd.Parameters.Add("$fp", SqliteType.Text);
                var p = cmd.Parameters.Add("$p", SqliteType.Text);
                var size = cmd.Parameters.Add("$size", SqliteType.Integer);
                var mtime = cmd.Parameters.Add("$mtime", SqliteType.Integer);
                foreach (var item in items)
                {
                    fp.Value = item.Fingerprint;
                    p.Value = item.Path;
                    size.Value = item.Size is { } s ? s : DBNull.Value;
                    mtime.Value = item.MtimeTicks is { } m ? m : DBNull.Value;
                    changed += cmd.ExecuteNonQuery();
                }
            });
            return changed;
        }
    }

    // ----- Analysis -----

    /// <summary>
    /// Up to limit fingerprinted photos not analysed yet, newest first (the
    /// photos the user looks at first stack first), each with one of its files.
    /// </summary>
    public IReadOnlyList<AnalysisJob> PendingAnalysis(int limit)
    {
        lock (_gate)
        {
            using var cmd = Command("""
                SELECT f.fingerprint, MIN(f.path), MAX(f.taken_ticks) AS newest FROM files f
                WHERE f.fingerprint IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM analysis a WHERE a.fingerprint = f.fingerprint)
                GROUP BY f.fingerprint ORDER BY newest DESC LIMIT $limit
                """, ("$limit", limit));
            using var r = cmd.ExecuteReader();
            var list = new List<AnalysisJob>();
            while (r.Read()) list.Add(new AnalysisJob(r.GetString(0), r.GetString(1)));
            return list;
        }
    }

    public void SetAnalysis(string fingerprint, ulong dhash, float sharpness, float exposure) =>
        WriteAnalysis(fingerprint, unchecked((long)dhash), sharpness, exposure);

    /// Records that the photo could not be analysed, so it is not tried again.
    public void SetAnalysisFailed(string fingerprint) => WriteAnalysis(fingerprint, null, null, null);

    private void WriteAnalysis(string fingerprint, long? dhash, float? sharpness, float? exposure)
    {
        lock (_gate)
        {
            using var cmd = Command("""
                INSERT INTO analysis(fingerprint, dhash, sharpness, exposure) VALUES($fp, $d, $s, $e)
                ON CONFLICT(fingerprint) DO UPDATE SET dhash = excluded.dhash,
                  sharpness = excluded.sharpness, exposure = excluded.exposure
                """, ("$fp", fingerprint), ("$d", dhash), ("$s", sharpness), ("$e", exposure));
            cmd.ExecuteNonQuery();
        }
    }

    /// What the analyzer stored for the photo, or null when not analysed yet.
    public AnalysisInfo? AnalysisOf(string fingerprint)
    {
        lock (_gate)
        {
            using var cmd = Command("SELECT dhash, sharpness, exposure FROM analysis WHERE fingerprint = $fp", ("$fp", fingerprint));
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadAnalysis(r, 0) : null;
        }
    }

    private static AnalysisInfo ReadAnalysis(SqliteDataReader r, int at) => new(
        r.IsDBNull(at) ? null : unchecked((ulong)r.GetInt64(at)),
        r.IsDBNull(at + 1) ? 0 : (float)r.GetDouble(at + 1),
        r.IsDBNull(at + 2) ? 0 : (float)r.GetDouble(at + 2));

    // ----- Tags -----

    /// <summary>
    /// Tags a photo. A tag the user added stays "manual" when recognition
    /// adds it again; a user adding an "auto" tag makes it theirs.
    /// </summary>
    public void AddTag(string fingerprint, string name, TagSource source = TagSource.Manual)
    {
        name = CleanTag(name);
        lock (_gate)
        {
            using var cmd = Command("""
                INSERT INTO tags(fingerprint, name, source, created) VALUES($fp, $name, $source, $now)
                ON CONFLICT(fingerprint, name) DO UPDATE SET
                  source = CASE WHEN excluded.source = 'manual' THEN 'manual' ELSE tags.source END
                """, ("$fp", fingerprint), ("$name", name), ("$source", SourceText(source)), ("$now", Now));
            cmd.ExecuteNonQuery();
        }
    }

    public void RemoveTag(string fingerprint, string name)
    {
        lock (_gate)
        {
            using var cmd = Command("DELETE FROM tags WHERE fingerprint = $fp AND name = $name",
                                    ("$fp", fingerprint), ("$name", name.Trim()));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Drops every tag of these photos (ones no longer on disk); their
    /// other records stay. Returns how many photos lost tags.
    /// </summary>
    public int ForgetTags(IReadOnlyCollection<string> fingerprints)
    {
        if (fingerprints.Count == 0) return 0;
        lock (_gate)
        {
            int forgotten = 0;
            InTransaction(() =>
            {
                foreach (var fp in fingerprints)
                {
                    using var cmd = Command("DELETE FROM tags WHERE fingerprint = $fp", ("$fp", fp));
                    if (cmd.ExecuteNonQuery() > 0) forgotten++;
                }
            });
            return forgotten;
        }
    }

    /// The photo's tags by name.
    public IReadOnlyList<TagInfo> TagsOf(string fingerprint)
    {
        lock (_gate)
        {
            using var cmd = Command("SELECT name, source FROM tags WHERE fingerprint = $fp ORDER BY name", ("$fp", fingerprint));
            using var r = cmd.ExecuteReader();
            var list = new List<TagInfo>();
            while (r.Read()) list.Add(new TagInfo(r.GetString(0), ParseSource(r.GetString(1))));
            return list;
        }
    }

    /// Every tag with the number of photos carrying it, most used first.
    public IReadOnlyList<TagCount> AllTags()
    {
        lock (_gate)
        {
            using var cmd = Command("SELECT name, COUNT(*) AS n FROM tags GROUP BY name ORDER BY n DESC, name");
            using var r = cmd.ExecuteReader();
            var list = new List<TagCount>();
            while (r.Read()) list.Add(new TagCount(r.GetString(0), r.GetInt32(1)));
            return list;
        }
    }

    private static string SourceText(TagSource s) => s == TagSource.Auto ? "auto" : "manual";
    private static TagSource ParseSource(string s) => s == "auto" ? TagSource.Auto : TagSource.Manual;

    /// <summary>
    /// Renames a tag on every photo and in every page. Merges into an
    /// existing tag of the new name; a photo that had both keeps one.
    /// </summary>
    public void RenameTag(string name, string newName)
    {
        newName = CleanTag(newName);
        if (newName == name) return;
        lock (_gate)
        {
            InTransaction(() =>
            {
                Exec("""
                    INSERT OR IGNORE INTO tags(fingerprint, name, source, created)
                      SELECT fingerprint, $new, source, created FROM tags WHERE name = $old;
                    DELETE FROM tags WHERE name = $old;
                    UPDATE OR IGNORE page_tags SET tag = $new WHERE tag = $old;
                    DELETE FROM page_tags WHERE tag = $old;
                    """, ("$old", name), ("$new", newName));
            });
        }
    }

    /// Takes a tag off every photo and out of every page.
    public void DeleteTag(string name)
    {
        lock (_gate)
        {
            InTransaction(() =>
            {
                Exec("DELETE FROM tags WHERE name = $name", ("$name", name));
                Exec("DELETE FROM page_tags WHERE tag = $name", ("$name", name));
            });
        }
    }

    // ----- Pages -----

    /// Creates a page showing the given tags, at the end of the list, and returns its id.
    public long CreatePage(string name, IEnumerable<string> tags)
    {
        name = CleanName(name);
        lock (_gate)
        {
            long id = 0;
            InTransaction(() =>
            {
                using (var cmd = Command("""
                    INSERT INTO pages(name, created, sort, archived) VALUES($name, $now, (SELECT COALESCE(MAX(sort), 0) + 1 FROM pages), 0);
                    SELECT last_insert_rowid();
                    """, ("$name", name), ("$now", Now)))
                {
                    id = Convert.ToInt64(cmd.ExecuteScalar());
                }
                WritePageTags(id, tags);
            });
            return id;
        }
    }

    public void RenamePage(long id, string name)
    {
        name = CleanName(name);
        lock (_gate) Exec("UPDATE pages SET name = $name WHERE id = $id", ("$name", name), ("$id", id));
    }

    /// Replaces the page's tags, kept in the given order.
    public void SetPageTags(long id, IEnumerable<string> tags)
    {
        lock (_gate) InTransaction(() => WritePageTags(id, tags));
    }

    /// How big the tag's card is on this page.
    public void SetPageTagSize(long id, string tag, CardSize size)
    {
        lock (_gate)
        {
            Exec("UPDATE page_tags SET size = $size WHERE page_id = $id AND tag = $tag",
                 ("$size", (int)size), ("$id", id), ("$tag", tag));
        }
    }

    /// Archived pages leave the main list but keep their tags.
    public void ArchivePage(long id, bool archived)
    {
        lock (_gate) Exec("UPDATE pages SET archived = $a WHERE id = $id", ("$a", archived ? 1 : 0), ("$id", id));
    }

    /// Deletes the page; its tags and photos stay.
    public void DeletePage(long id)
    {
        lock (_gate)
        {
            InTransaction(() =>
            {
                Exec("DELETE FROM page_tags WHERE page_id = $id", ("$id", id));
                Exec("DELETE FROM pages WHERE id = $id", ("$id", id));
            });
        }
    }

    /// Pages in the user's order, archived ones included.
    public IReadOnlyList<PageInfo> Pages()
    {
        lock (_gate)
        {
            var tags = new Dictionary<long, List<string>>();
            var sizes = new Dictionary<long, Dictionary<string, CardSize>>();
            using (var cmd = Command("SELECT page_id, tag, size FROM page_tags ORDER BY page_id, sort"))
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    long id = r.GetInt64(0);
                    if (!tags.TryGetValue(id, out var list)) tags[id] = list = new List<string>();
                    list.Add(r.GetString(1));
                    var size = (CardSize)Math.Clamp(r.GetInt64(2), 0, 2);
                    if (size == CardSize.Small) continue;
                    if (!sizes.TryGetValue(id, out var bySize)) sizes[id] = bySize = new Dictionary<string, CardSize>(StringComparer.Ordinal);
                    bySize[r.GetString(1)] = size;
                }
            }
            using (var cmd = Command("SELECT id, name, archived FROM pages ORDER BY sort, id"))
            using (var r = cmd.ExecuteReader())
            {
                var pages = new List<PageInfo>();
                while (r.Read())
                {
                    long id = r.GetInt64(0);
                    pages.Add(new PageInfo(id, r.GetString(1), tags.TryGetValue(id, out var t) ? t : new List<string>(), r.GetInt64(2) != 0,
                                           sizes.TryGetValue(id, out var z) ? z : null));
                }
                return pages;
            }
        }
    }

    /// Tags that stay on the page keep their card size.
    private void WritePageTags(long id, IEnumerable<string> tags)
    {
        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
        using (var cmd = Command("SELECT tag, size FROM page_tags WHERE page_id = $id", ("$id", id)))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read()) sizes[r.GetString(0)] = r.GetInt64(1);
        }
        Exec("DELETE FROM page_tags WHERE page_id = $id", ("$id", id));
        int sort = 0;
        foreach (var tag in tags.Select(t => t.Trim().TrimStart('#').Trim()).Where(t => t.Length > 0).Distinct(StringComparer.Ordinal))
        {
            Exec("INSERT INTO page_tags(page_id, tag, sort, size) VALUES($id, $tag, $sort, $size)",
                 ("$id", id), ("$tag", tag), ("$sort", sort++), ("$size", sizes.TryGetValue(tag, out var size) ? size : 0));
        }
    }

    // ----- Stacks -----

    /// <summary>
    /// Makes the photo the cover of whatever stack it is in. The latest
    /// choice wins, so earlier choices in the same stack need no clearing.
    /// </summary>
    public void SetCover(string fingerprint)
    {
        lock (_gate)
        {
            InTransaction(() =>
            {
                EnsureOverride(fingerprint);
                Exec("UPDATE stack_overrides SET is_cover = (SELECT MAX(is_cover) FROM stack_overrides) + 1 WHERE fingerprint = $fp",
                     ("$fp", fingerprint));
            });
        }
    }

    /// Takes the photo out of any stack, now and in later rebuilds.
    public void KeepApart(string fingerprint) => SplitStack(new[] { fingerprint });

    /// <summary>
    /// Puts the photos into one stack regardless of the rule. Pass every
    /// member of the stacks being merged (CatalogItem.Stack); photos already
    /// in a manual stack bring that whole stack along.
    /// </summary>
    public void MergeIntoStack(IEnumerable<string> fingerprints)
    {
        var list = fingerprints.Distinct(StringComparer.Ordinal).ToList();
        if (list.Count < 2) return;
        var group = Guid.NewGuid().ToString("N");
        lock (_gate)
        {
            InTransaction(() =>
            {
                foreach (var fp in list)
                {
                    using var old = Command("SELECT manual_group FROM stack_overrides WHERE fingerprint = $fp AND manual_group IS NOT NULL",
                                            ("$fp", fp));
                    if (old.ExecuteScalar() is string oldGroup)
                    {
                        Exec("UPDATE stack_overrides SET manual_group = $g WHERE manual_group = $old", ("$g", group), ("$old", oldGroup));
                    }
                    EnsureOverride(fp);
                    Exec("UPDATE stack_overrides SET manual_group = $g, keep_apart = 0 WHERE fingerprint = $fp", ("$g", group), ("$fp", fp));
                }
            });
        }
    }

    /// Each photo leaves its stack and stays on its own.
    public void SplitStack(IEnumerable<string> fingerprints)
    {
        lock (_gate)
        {
            InTransaction(() =>
            {
                foreach (var fp in fingerprints)
                {
                    EnsureOverride(fp);
                    Exec("UPDATE stack_overrides SET manual_group = NULL, keep_apart = 1, is_cover = 0 WHERE fingerprint = $fp", ("$fp", fp));
                }
            });
        }
    }

    /// Forgets every hand-made stack choice for the photos: the rule decides again.
    public void ResetStack(IEnumerable<string> fingerprints)
    {
        lock (_gate)
        {
            InTransaction(() =>
            {
                foreach (var fp in fingerprints) Exec("DELETE FROM stack_overrides WHERE fingerprint = $fp", ("$fp", fp));
            });
        }
    }

    private void EnsureOverride(string fingerprint) =>
        Exec("INSERT OR IGNORE INTO stack_overrides(fingerprint) VALUES($fp)", ("$fp", fingerprint));

    /// The stack choices as the stack builder takes them.
    public StackOverrides Overrides()
    {
        lock (_gate) return ReadOverrides();
    }

    private StackOverrides ReadOverrides()
    {
        var groups = new Dictionary<string, string>(StringComparer.Ordinal);
        var apart = new HashSet<string>(StringComparer.Ordinal);
        var covers = new Dictionary<string, long>(StringComparer.Ordinal);
        using var cmd = Command("SELECT fingerprint, manual_group, keep_apart, is_cover FROM stack_overrides");
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var fp = r.GetString(0);
            if (!r.IsDBNull(1)) groups[fp] = r.GetString(1);
            if (r.GetInt64(2) != 0) apart.Add(fp);
            if (r.GetInt64(3) > 0) covers[fp] = r.GetInt64(3);
        }
        return new StackOverrides { ManualGroups = groups, KeepApart = apart, Covers = covers };
    }

    // ----- Versions -----

    /// Records that versionFp is an edited version of originalFp; beats name matching.
    public void AddVersion(string originalFingerprint, string versionFingerprint)
    {
        if (originalFingerprint == versionFingerprint) return;
        lock (_gate)
        {
            Exec("""
                INSERT INTO versions(version_fingerprint, original_fingerprint, created) VALUES($v, $o, $now)
                ON CONFLICT(version_fingerprint) DO UPDATE SET original_fingerprint = excluded.original_fingerprint
                """, ("$v", versionFingerprint), ("$o", originalFingerprint), ("$now", Now));
        }
    }

    // ----- Catalog -----

    /// <summary>
    /// A snapshot of the whole library: photos by fingerprint with their
    /// copies, stacks and versions folded, ready to list.
    /// </summary>
    public Catalog Catalog()
    {
        var data = new CatalogData();
        lock (_gate)
        {
            using (var cmd = Command("SELECT path, size, mtime_ticks, taken_ticks, exif_taken, camera, fingerprint FROM files"))
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read()) data.Files.Add(ReadFile(r));
            }
            using (var cmd = Command("SELECT fingerprint, dhash, sharpness, exposure FROM analysis"))
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read()) data.Analysis[r.GetString(0)] = ReadAnalysis(r, 1);
            }
            data.Overrides = ReadOverrides();
            using (var cmd = Command("SELECT version_fingerprint, original_fingerprint FROM versions"))
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read()) data.Versions[r.GetString(0)] = r.GetString(1);
            }
            using (var cmd = Command("SELECT name, fingerprint FROM tags"))
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read()) data.Tags.Add((r.GetString(0), r.GetString(1)));
            }
        }
        return new Catalog(data);
    }

    // ----- Plumbing -----

    private static long Now => DateTime.UtcNow.Ticks;

    private static string CleanName(string name)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("名称不能为空");
        return name;
    }

    /// A tag's name; a leading # is how people type tags, not part of the name.
    public static string CleanTag(string name) => CleanName(name.Trim().TrimStart('#'));

    private SqliteConnection Db => _db ?? throw new ObjectDisposedException(nameof(LibraryIndex));

    private SqliteCommand Command(string sql, params (string Name, object? Value)[] args)
    {
        var cmd = Db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = _tx;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private void Exec(string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = Command(sql, args);
        cmd.ExecuteNonQuery();
    }

    private T Scalar<T>(string sql)
    {
        using var cmd = Command(sql);
        return (T)Convert.ChangeType(cmd.ExecuteScalar()!, typeof(T));
    }

    /// Runs body in a transaction, or inside the current one when nested.
    private void InTransaction(Action body)
    {
        if (_tx is not null)
        {
            body();
            return;
        }
        _tx = Db.BeginTransaction();
        try
        {
            body();
            _tx.Commit();
        }
        finally
        {
            _tx.Dispose();
            _tx = null;
        }
    }
}

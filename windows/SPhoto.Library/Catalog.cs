using System;
using System.Collections.Generic;
using System.Linq;

namespace SPhoto.Library;

/// <summary>
/// One photo of the library (图库): the same bytes in several places are one
/// photo. Path is the primary copy (shortest path, then ordinal order) and
/// Copies every path, primary first. A file not fingerprinted yet is its own
/// photo keyed "path:" + its path, so nothing hides while hashing runs.
/// </summary>
public sealed record CatalogPhoto(
    string Fingerprint, string Path, IReadOnlyList<string> Copies, DateTime Taken, string? Camera, long Bytes)
{
    /// Whether Fingerprint is a real fingerprint rather than a "path:" stand-in.
    public bool IsFingerprinted => !Fingerprint.StartsWith(Catalog.PathKeyPrefix, StringComparison.Ordinal);
}

/// <summary>
/// What the gallery shows as one tile. Stack lists the members in time order
/// (just the cover when not stacked); Versions the edited versions of the
/// cover, newest first, which do not appear as tiles of their own.
/// </summary>
public sealed record CatalogItem(CatalogPhoto Cover, IReadOnlyList<CatalogPhoto> Stack, IReadOnlyList<CatalogPhoto> Versions)
{
    public bool IsStack => Stack.Count > 1;
}

/// <summary>Which photos Catalog.Items lists: all, one tag, or any of several (a page).</summary>
public abstract record CatalogScope
{
    public static readonly CatalogScope All = new AllScope();

    public static CatalogScope Tag(string name) => new TagScope(name);

    public static CatalogScope AnyTag(IEnumerable<string> names) => new AnyTagScope(names.ToArray());

    public sealed record AllScope : CatalogScope;

    /// Photos carrying at least one of the tags.
    public sealed record AnyTagScope(IReadOnlyList<string> Names) : CatalogScope;

    public sealed record TagScope(string Name) : CatalogScope;
}

/// What LibraryIndex.Catalog reads under its lock; built into a Catalog outside it.
internal sealed class CatalogData
{
    internal List<FileRecord> Files { get; } = new();
    internal Dictionary<string, AnalysisInfo> Analysis { get; } = new(StringComparer.Ordinal);
    internal StackOverrides Overrides { get; set; } = StackOverrides.None;
    internal Dictionary<string, string> Versions { get; } = new(StringComparer.Ordinal);
    internal List<(string Name, string Fingerprint)> Tags { get; } = new();
}

/// <summary>
/// An immutable snapshot of the library: copies merged, stacks built and
/// versions folded. Cheap to query from any thread; take a new one from
/// LibraryIndex.Catalog when the library changes.
/// </summary>
public sealed class Catalog
{
    public const string PathKeyPrefix = "path:";

    private readonly Dictionary<string, CatalogPhoto> _photos;
    private readonly Dictionary<string, CatalogPhoto> _byPath;
    private readonly List<CatalogItem> _items;
    private readonly Dictionary<string, CatalogItem> _itemOf;
    private readonly Dictionary<string, string> _originalOf;
    private readonly Dictionary<string, List<CatalogPhoto>> _versions;
    private readonly Dictionary<string, StackInput> _inputs;
    private readonly StackOverrides _overrides;
    private readonly Dictionary<string, HashSet<string>> _tags;

    internal Catalog(CatalogData data)
    {
        _overrides = data.Overrides;

        // Copies of the same photo.
        var byKey = new Dictionary<string, List<FileRecord>>(data.Files.Count, StringComparer.Ordinal);
        foreach (var f in data.Files)
        {
            var key = f.Fingerprint ?? PathKeyPrefix + f.Path;
            if (!byKey.TryGetValue(key, out var list)) byKey[key] = list = new List<FileRecord>(1);
            list.Add(f);
        }

        _photos = new Dictionary<string, CatalogPhoto>(byKey.Count, StringComparer.Ordinal);
        _byPath = new Dictionary<string, CatalogPhoto>(data.Files.Count, LibraryPaths.Comparer);
        var modified = new Dictionary<string, long>(byKey.Count, StringComparer.Ordinal);
        _inputs = new Dictionary<string, StackInput>(byKey.Count, StringComparer.Ordinal);
        foreach (var (key, files) in byKey)
        {
            if (files.Count > 1)
            {
                files.Sort((a, b) =>
                {
                    int c = a.Path.Length.CompareTo(b.Path.Length);
                    return c != 0 ? c : string.CompareOrdinal(a.Path, b.Path);
                });
            }
            var primary = files[0];
            // A copy's EXIF date beats a file date of another copy.
            var dated = files.FirstOrDefault(f => f.ExifTaken && f.Taken is not null) ?? primary;
            var taken = dated.Taken ?? new DateTime(primary.MtimeTicks, DateTimeKind.Utc).ToLocalTime();
            var camera = primary.Camera ?? files.FirstOrDefault(f => f.Camera is not null)?.Camera;
            var copies = files.Count == 1 ? new[] { primary.Path } : files.Select(f => f.Path).ToArray();
            var photo = new CatalogPhoto(key, primary.Path, copies, taken, camera, primary.Size);
            _photos[key] = photo;
            foreach (var f in files) _byPath[f.Path] = photo;
            modified[key] = files.Count == 1 ? primary.MtimeTicks : files.Max(f => f.MtimeTicks);

            data.Analysis.TryGetValue(key, out var analysis);
            _inputs[key] = new StackInput(key, dated.ExifTaken ? dated.Taken : null, camera, analysis?.DHash, analysis?.Quality);
        }

        // Versions: names first, records in the index override them.
        var parent = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (version, original) in Versions.MatchByName(_byPath.Keys))
        {
            string v = _byPath[version].Fingerprint, o = _byPath[original].Fingerprint;
            if (v != o) parent[v] = o;
        }
        foreach (var (version, original) in data.Versions)
        {
            if (version != original && _photos.ContainsKey(version) && _photos.ContainsKey(original)) parent[version] = original;
        }
        _originalOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var version in parent.Keys)
        {
            // Follow edits of edits to the first original; a loop means the
            // records disagree, and then the photo stands on its own.
            var seen = new HashSet<string>(StringComparer.Ordinal) { version };
            var root = parent[version];
            while (parent.TryGetValue(root, out var up) && seen.Add(root)) root = up;
            if (!seen.Contains(root)) _originalOf[version] = root;
        }
        _versions = new Dictionary<string, List<CatalogPhoto>>(StringComparer.Ordinal);
        foreach (var (version, original) in _originalOf)
        {
            if (!_versions.TryGetValue(original, out var list)) _versions[original] = list = new List<CatalogPhoto>();
            list.Add(_photos[version]);
        }
        foreach (var list in _versions.Values)
        {
            list.Sort((a, b) =>
            {
                int c = modified[b.Fingerprint].CompareTo(modified[a.Fingerprint]);
                return c != 0 ? c : string.CompareOrdinal(a.Path, b.Path);
            });
        }

        // Stacks among the photos that are not versions.
        var stackable = new List<StackInput>(_photos.Count);
        foreach (var key in _photos.Keys)
        {
            if (!_originalOf.ContainsKey(key)) stackable.Add(_inputs[key]);
        }
        _items = new List<CatalogItem>(stackable.Count);
        _itemOf = new Dictionary<string, CatalogItem>(_photos.Count, StringComparer.Ordinal);
        foreach (var group in StackBuilder.Build(stackable, _overrides))
        {
            var item = MakeItem(_photos[group.Cover], group.Members.Select(m => _photos[m]));
            _items.Add(item);
            foreach (var m in group.Members) _itemOf[m] = item;
        }
        foreach (var (version, original) in _originalOf)
        {
            if (_itemOf.TryGetValue(original, out var item)) _itemOf[version] = item;
        }
        SortNewestFirst(_items);

        _tags = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (name, fp) in data.Tags)
        {
            if (!_tags.TryGetValue(name, out var set)) _tags[name] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(fp);
        }
    }

    /// Every photo by fingerprint (or "path:" key), versions and stack members included.
    public IReadOnlyDictionary<string, CatalogPhoto> ByFingerprint => _photos;

    /// Every file path to the photo it is a copy of.
    public IReadOnlyDictionary<string, CatalogPhoto> ByPath => _byPath;

    /// The number of tiles in the whole library.
    public int Count => _items.Count;

    /// <summary>
    /// Each tag with the number of its photos on disk now, most used first.
    /// A tag whose photos are all missing (moved out of the library, on a
    /// drive not plugged in) is left out; its records stay in the index.
    /// </summary>
    public IReadOnlyList<TagCount> TagCounts() =>
        _tags.Select(t => new TagCount(t.Key, t.Value.Count(_photos.ContainsKey)))
            .Where(t => t.Count > 0)
            .OrderByDescending(t => t.Count).ThenBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

    /// The photos carrying a tag that are on disk now, one per content, newest first.
    public IReadOnlyList<CatalogPhoto> PhotosWithTag(string name) =>
        (_tags.GetValueOrDefault(name) ?? new HashSet<string>())
            .Where(_photos.ContainsKey).Select(fp => _photos[fp])
            .OrderByDescending(p => p.Taken).ThenBy(p => p.Path, StringComparer.Ordinal)
            .ToList();

    /// Photos that carry tags but are not on disk now, by fingerprint.
    public IReadOnlyList<string> MissingTagged() =>
        _tags.Values.SelectMany(v => v).Where(fp => !_photos.ContainsKey(fp)).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// The tiles of a scope, newest first. In a tag or page, only its
    /// photos are listed; they still stack with each other and versions
    /// still fold under an original that is in it.
    /// </summary>
    public IReadOnlyList<CatalogItem> Items(CatalogScope scope)
    {
        HashSet<string>? members = scope switch
        {
            CatalogScope.AllScope => null,
            CatalogScope.AnyTagScope any => new HashSet<string>(
                any.Names.SelectMany(n => _tags.GetValueOrDefault(n) ?? Enumerable.Empty<string>()), StringComparer.Ordinal),
            CatalogScope.TagScope t => _tags.GetValueOrDefault(t.Name) ?? new HashSet<string>(),
            _ => throw new ArgumentOutOfRangeException(nameof(scope)),
        };
        return members is null ? _items : ItemsIn(members);
    }

    /// The tile a photo appears in (as cover, stack member or version), or null.
    public CatalogItem? ItemOf(string fingerprint) => _itemOf.GetValueOrDefault(fingerprint);

    /// The original a version folds under, or null when the photo is not a version.
    public CatalogPhoto? OriginalOf(string fingerprint) =>
        _originalOf.TryGetValue(fingerprint, out var original) ? _photos[original] : null;

    /// The edited versions of any photo (not only covers), newest first.
    public IReadOnlyList<CatalogPhoto> VersionsOf(string fingerprint) =>
        _versions.TryGetValue(fingerprint, out var list) ? list : Array.Empty<CatalogPhoto>();

    private CatalogItem MakeItem(CatalogPhoto cover, IEnumerable<CatalogPhoto> members)
    {
        var stack = members.ToList();
        if (stack.Count > 1)
        {
            stack.Sort((a, b) =>
            {
                int c = a.Taken.CompareTo(b.Taken);
                return c != 0 ? c : string.CompareOrdinal(a.Path, b.Path);
            });
        }
        return new CatalogItem(cover, stack, VersionsOf(cover.Fingerprint));
    }

    private List<CatalogItem> ItemsIn(HashSet<string> scope)
    {
        var result = new List<CatalogItem>();
        var done = new HashSet<CatalogItem>(ReferenceEqualityComparer.Instance);
        foreach (var fp in scope)
        {
            if (!_photos.ContainsKey(fp)) continue;  // not on disk right now
            if (_originalOf.TryGetValue(fp, out var original) && scope.Contains(original)) continue;  // folds
            if (_originalOf.ContainsKey(fp))
            {
                // A version whose original is not in the scope shows on its own.
                result.Add(new CatalogItem(_photos[fp], new[] { _photos[fp] }, VersionsOf(fp)));
                continue;
            }
            var item = _itemOf[fp];
            if (!done.Add(item)) continue;
            var inScope = item.Stack.Where(p => scope.Contains(p.Fingerprint)).ToList();
            if (inScope.Count == item.Stack.Count)
            {
                result.Add(item);
                continue;
            }
            var cover = scope.Contains(item.Cover.Fingerprint)
                ? item.Cover
                : _photos[StackBuilder.PickCover(inScope.Select(p => _inputs[p.Fingerprint]).ToList(), _overrides).Key];
            result.Add(new CatalogItem(cover, inScope, VersionsOf(cover.Fingerprint)));
        }
        SortNewestFirst(result);
        return result;
    }

    private static void SortNewestFirst(List<CatalogItem> items) => items.Sort((a, b) =>
    {
        int c = b.Cover.Taken.CompareTo(a.Cover.Taken);
        return c != 0 ? c : string.CompareOrdinal(a.Cover.Path, b.Cover.Path);
    });
}

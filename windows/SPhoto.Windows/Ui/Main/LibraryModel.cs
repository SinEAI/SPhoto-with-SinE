using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SPhoto.Library;
using SPhoto.Services;

namespace SPhoto.Ui.Main;

/// <summary>
/// One tile of the library: a photo (all its identical copies count as
/// one), possibly the cover of a stack of similar shots, with its edited
/// versions folded under it.
/// </summary>
internal sealed class Shot
{
    internal required string Key { get; init; }
    internal required PhotoEntry Cover { get; init; }
    /// The similar shots stacked with it, in time order (just the cover when not stacked).
    internal required IReadOnlyList<PhotoEntry> Stack { get; init; }
    /// Edited versions, newest first.
    internal required IReadOnlyList<PhotoEntry> Versions { get; init; }
    /// Every file with the cover's content, the cover's own path first.
    internal required IReadOnlyList<string> Copies { get; init; }

    /// Fingerprinted yet? Until then tags wait.
    internal bool Indexed => !Key.StartsWith("path:", StringComparison.Ordinal);
}

/// <summary>Which photos a library page shows.</summary>
internal sealed record Scope(string Kind, long Page = 0, string Tag = "")
{
    internal static readonly Scope All = new("all");
    /// Every photo with any of the page's tags.
    internal static Scope OfPage(long id) => new("page", Page: id);
    internal static Scope OfTag(string name) => new("tag", Tag: name);
}

/// <summary>
/// The UI's view of the library index: tiles for the library's views, pages
/// and tags for the sidebar, and what a viewer needs to know about any file.
/// A snapshot: the design asks for a new one when the library changes.
/// </summary>
internal sealed class LibraryModel
{
    private readonly Catalog _catalog;
    private readonly LibraryIndex _index;
    private readonly Dictionary<string, FileRecord> _files;
    private readonly Dictionary<string, Shot> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Shot> _byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<Scope, List<Shot>> _scopes = new();

    private LibraryModel(LibraryIndex index, Catalog catalog)
    {
        _index = index;
        _files = index.AllFiles();
        _catalog = catalog;
        Pages = index.Pages();
        // Only photos on disk count: a tag whose photos all went is not listed.
        Tags = catalog.TagCounts().Select(t => new TagInfo(t.Name, t.Count)).ToList();
    }

    internal sealed record TagInfo(string Name, int Count);

    /// The user's pages, archived ones included, in their order.
    internal IReadOnlyList<PageInfo> Pages { get; }

    internal PageInfo? PageOf(long id) => Pages.FirstOrDefault(p => p.Id == id);
    internal IReadOnlyList<TagInfo> Tags { get; }

    internal static Task<LibraryModel> LoadAsync(PhotoLibrary library) => Task.Run(() =>
    {
        var model = new LibraryModel(library.Index, library.Catalog());
        model.Shots(Scope.All);
        return model;
    });

    private PhotoEntry Entry(CatalogPhoto p) =>
        new(p.Path, p.Taken, p.Bytes) { Camera = p.Camera, MtimeTicks = _files.GetValueOrDefault(p.Path)?.MtimeTicks ?? 0 };

    private Shot ToShot(CatalogItem item)
    {
        if (_byKey.TryGetValue(item.Cover.Fingerprint, out var known)) return known;
        var shot = new Shot
        {
            Key = item.Cover.Fingerprint,
            Cover = Entry(item.Cover),
            Stack = item.Stack.Select(Entry).ToList(),
            Versions = item.Versions.Select(Entry).ToList(),
            Copies = item.Cover.Copies,
        };
        _byKey[shot.Key] = shot;
        foreach (var path in item.Cover.Copies) _byPath.TryAdd(path, shot);
        foreach (var p in item.Stack.Concat(item.Versions))
        {
            foreach (var path in p.Copies) _byPath.TryAdd(path, shot);
        }
        return shot;
    }

    /// The tiles of a page, newest first.
    internal List<Shot> Shots(Scope scope)
    {
        lock (_scopes)
        {
            if (_scopes.TryGetValue(scope, out var cached)) return cached;
            var catalogScope = scope.Kind switch
            {
                "page" => CatalogScope.AnyTag(PageOf(scope.Page)?.Tags ?? Array.Empty<string>()),
                "tag" => CatalogScope.Tag(scope.Tag),
                _ => CatalogScope.All,
            };
            var shots = _catalog.Items(catalogScope).Select(ToShot).ToList();
            _scopes[scope] = shots;
            return shots;
        }
    }

    /// The newest few photos with a tag, for its cover.
    internal IReadOnlyList<PhotoEntry> CoverPhotos(string tag, int count) =>
        Shots(Scope.OfTag(tag)).Take(count).Select(s => s.Cover).ToList();

    /// The number of tiles with a tag.
    internal int CountOf(string tag) => Tags.FirstOrDefault(t => t.Name == tag)?.Count ?? 0;

    /// The tile a file belongs to: as cover, copy, stacked shot or version.
    internal Shot? ShotOf(string path)
    {
        lock (_scopes) return _byPath.GetValueOrDefault(path);
    }

    /// The fingerprint of a file, when known.
    internal string? KeyOf(string path)
    {
        var photo = _catalog.ByPath.GetValueOrDefault(path);
        return photo is null || photo.Fingerprint.StartsWith("path:", StringComparison.Ordinal) ? null : photo.Fingerprint;
    }

    /// Every path holding this content.
    internal IReadOnlyList<string> CopiesOf(string path) =>
        _catalog.ByPath.GetValueOrDefault(path)?.Copies ?? new[] { path };

    internal TileBadge Badge(PhotoEntry cover) =>
        ShotOf(cover.Path) is { } s && string.Equals(s.Cover.Path, cover.Path, StringComparison.OrdinalIgnoreCase)
            ? new TileBadge(s.Stack.Count, s.Versions.Count)
            : default;

    internal string? CoverPathOf(string key) =>
        _catalog.ByFingerprint.GetValueOrDefault(key)?.Path;

    // ----- Writes go straight to the index; callers reload the model. -----

    internal LibraryIndex Index => _index;

    internal static string Fingerprint(string path) => SPhoto.Library.Fingerprint.Compute(path);

    /// Records a saved edit as a version of the photo it came from.
    internal static void RecordVersion(LibraryIndex index, string original, string saved)
    {
        try
        {
            var from = SPhoto.Library.Fingerprint.Compute(original);
            var to = SPhoto.Library.Fingerprint.Compute(saved);
            index.AddVersion(from, to);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The name still ties it to the original ("_已编辑").
        }
    }
}

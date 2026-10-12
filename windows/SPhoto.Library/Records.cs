using System;
using System.Collections.Generic;
using System.IO;

namespace SPhoto.Library;

/// <summary>
/// One file under the library's folders as the index remembers it. Taken is
/// the date shown (EXIF when ExifTaken, else the file's local write time);
/// MtimeTicks is the UTC write time that, with Size, tells an unchanged file.
/// Fingerprint is null until the background fingerprinter gets to it.
/// </summary>
public sealed record FileRecord(
    string Path, long Size, long MtimeTicks, DateTime? Taken, bool ExifTaken, string? Camera, string? Fingerprint)
{
    /// Whether this record still describes the file on disk.
    public bool Matches(long size, long mtimeTicks) => Size == size && MtimeTicks == mtimeTicks;
}

/// <summary>Who put a tag on a photo: the user, or recognition.</summary>
public enum TagSource
{
    Manual,
    Auto,
}

public sealed record TagInfo(string Name, TagSource Source);

public sealed record TagCount(string Name, int Count);

/// <summary>How big a tag's card is on a page, in cells: 1×1, 2×1 or 2×2.</summary>
public enum CardSize { Small = 0, Medium = 1, Large = 2 }

/// <summary>A page: a named set of tags the user shows together, in their order,
/// each tag's card at the size chosen for this page.</summary>
public sealed record PageInfo(long Id, string Name, IReadOnlyList<string> Tags, bool Archived,
                              IReadOnlyDictionary<string, CardSize>? Sizes = null)
{
    public CardSize SizeOf(string tag) => Sizes is not null && Sizes.TryGetValue(tag, out var size) ? size : CardSize.Small;
}

/// <summary>A photo the analyzer still has to look at, and one file to read it from.</summary>
public sealed record AnalysisJob(string Fingerprint, string Path);

/// <summary>
/// What the analyzer measured. DHash null means the photo could not be
/// decoded; it is then not asked for again until its fingerprint changes.
/// </summary>
public sealed record AnalysisInfo(ulong? DHash, float Sharpness, float Exposure)
{
    public float? Quality => DHash is null ? null : ImageStats.Quality(Sharpness, Exposure);
}

/// <summary>Path helpers that follow the file system's case rules.</summary>
public static class LibraryPaths
{
    public static StringComparison Comparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static StringComparer Comparer =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// Whether path is folder itself or somewhere below it.
    public static bool IsInside(string path, string folder)
    {
        folder = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return path.Equals(folder, Comparison) ||
               (path.Length > folder.Length && path.StartsWith(folder, Comparison) &&
                (path[folder.Length] == Path.DirectorySeparatorChar || path[folder.Length] == Path.AltDirectorySeparatorChar));
    }
}

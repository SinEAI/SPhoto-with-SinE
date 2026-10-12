using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace SPhoto.Library;

/// <summary>
/// Finds the original of an edited copy by its name. SPhoto saves edits as
/// "name_已编辑.jpg" next to the original, numbered "name_已编辑 (2).jpg"
/// when that exists; editing a copy again adds another "_已编辑". Records
/// in the index (LibraryIndex.AddVersion) take precedence over names.
/// </summary>
public static class Versions
{
    public const string EditedSuffix = "_已编辑";

    // One or more "_已编辑", each optionally numbered, at the end of the stem.
    private static readonly Regex Edited = new(@"^(?<stem>.+?)(?:_已编辑(?: \(\d+\))?)+$", RegexOptions.CultureInvariant);

    /// <summary>
    /// The stem of the original for an edited copy's file name
    /// ("IMG_1_已编辑 (2).jpg" → "IMG_1"), or null when the name is not one.
    /// </summary>
    public static string? OriginalStem(string fileName)
    {
        var match = Edited.Match(Path.GetFileNameWithoutExtension(fileName));
        return match.Success ? match.Groups["stem"].Value : null;
    }

    public static bool IsEditedName(string fileName) => OriginalStem(fileName) is not null;

    /// <summary>
    /// For every edited copy among paths whose original ("stem.&lt;any photo
    /// extension&gt;" in the same folder) is also among them: version path →
    /// original path. When several originals share the stem, the one with the
    /// copy's extension wins, then the first by name.
    /// </summary>
    public static Dictionary<string, string> MatchByName(IEnumerable<string> paths)
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        // Edited copies are few; find them first and look only for their stems.
        var all = paths as IReadOnlyCollection<string> ?? paths.ToList();
        var edited = new List<(string Path, string Key)>();
        var stems = new HashSet<string>(comparer);
        foreach (var path in all)
        {
            if (!path.Contains(EditedSuffix, StringComparison.Ordinal)) continue;
            var stem = OriginalStem(Path.GetFileName(path));
            if (stem is null) continue;
            edited.Add((path, Path.Combine(Path.GetDirectoryName(path) ?? "", stem)));
            stems.Add(stem);
        }
        if (edited.Count == 0) return result;

        var originals = new Dictionary<string, List<string>>(comparer);
        foreach (var path in all)
        {
            var stem = Path.GetFileNameWithoutExtension(path);
            if (!stems.Contains(stem) || IsEditedName(path)) continue;
            var key = Path.Combine(Path.GetDirectoryName(path) ?? "", stem);
            if (!originals.TryGetValue(key, out var list)) originals[key] = list = new List<string>();
            list.Add(path);
        }
        foreach (var (path, key) in edited)
        {
            if (!originals.TryGetValue(key, out var candidates)) continue;
            var ext = Path.GetExtension(path);
            result[path] = candidates
                .OrderBy(c => comparer.Equals(Path.GetExtension(c), ext) ? 0 : 1)
                .ThenBy(c => c, StringComparer.Ordinal)
                .First();
        }
        return result;
    }
}

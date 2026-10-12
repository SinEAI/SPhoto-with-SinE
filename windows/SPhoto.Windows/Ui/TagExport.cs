using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SPhoto.Ui;

/// <summary>
/// Copies or moves a tag's photos, wherever they are, into one folder. A
/// one-off: the folder keeps no tie to the tag. Nothing is overwritten; a
/// name already there gets " (1)", " (2)"... Tags follow the photos'
/// content, so a moved photo keeps its tags wherever it lands.
/// </summary>
internal static class TagExport
{
    internal sealed record Result(int Done, int Skipped, IReadOnlyList<string> Failed);

    internal static Task<Result> RunAsync(IReadOnlyList<string> paths, string target, bool move,
                                          IProgress<int>? progress = null, CancellationToken cancel = default) =>
        Task.Run(() =>
        {
            Directory.CreateDirectory(target);
            int done = 0, skipped = 0;
            var failed = new List<string>();
            foreach (var source in paths)
            {
                cancel.ThrowIfCancellationRequested();
                try
                {
                    // Already in that folder: nothing to copy or move.
                    if (string.Equals(Path.GetDirectoryName(Path.GetFullPath(source)), Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar),
                                      StringComparison.OrdinalIgnoreCase))
                    {
                        skipped++;
                        continue;
                    }
                    var destination = FreeName(target, Path.GetFileName(source));
                    if (move) File.Move(source, destination);
                    else File.Copy(source, destination);
                    done++;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    failed.Add(Path.GetFileName(source));
                }
                progress?.Report(done + skipped + failed.Count);
            }
            return new Result(done, skipped, failed);
        }, cancel);

    /// The name itself when free, else "name (1).ext", "name (2).ext"...
    internal static string FreeName(string folder, string fileName)
    {
        var path = Path.Combine(folder, fileName);
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (int i = 1; ; i++)
        {
            path = Path.Combine(folder, $"{stem} ({i}){extension}");
            if (!File.Exists(path) && !Directory.Exists(path)) return path;
        }
    }
}

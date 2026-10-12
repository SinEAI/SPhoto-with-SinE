using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SPhoto.Library;

/// <summary>The photos found by a scan, in no particular order.</summary>
public sealed record ScanResult(IReadOnlyList<FileRecord> Files);

/// <summary>
/// Brings the index in line with the folders on disk. Unchanged files (same
/// size and write time) come from the index without being opened; new and
/// changed ones have their EXIF read in parallel. Fingerprints are left to
/// the Fingerprinter, which runs afterwards in the background.
/// </summary>
public static class Scanner
{
    /// The file types the library shows.
    public static readonly IReadOnlySet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jfif", ".png", ".bmp", ".gif", ".tif", ".tiff",
        ".webp", ".heic", ".heif", ".jxr", ".wdp", ".avif",
    };

    public static bool IsPhoto(string path) => Extensions.Contains(Path.GetExtension(path));

    /// Reading EXIF is disk-bound: a few readers keep an SSD busy without
    /// thrashing a spinning disk.
    private static int ReaderCount => Math.Clamp(Environment.ProcessorCount, 1, 4);

    public static ScanResult Scan(LibraryIndex index, IReadOnlyList<string> roots, CancellationToken cancel = default)
    {
        var known = index.AllFiles();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };

        var found = new Dictionary<string, FileRecord>(StringComparer.Ordinal);
        var toRead = new List<FileInfo>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<FileInfo> files;
            try
            {
                files = new DirectoryInfo(root).EnumerateFiles("*", options);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var info in files)
            {
                cancel.ThrowIfCancellationRequested();
                var path = info.FullName;
                if (!IsPhoto(path) || found.ContainsKey(path)) continue;
                try
                {
                    // The enumeration already filled these in on Windows.
                    long size = info.Length, mtime = info.LastWriteTimeUtc.Ticks;
                    if (known.TryGetValue(path, out var record) && record.Matches(size, mtime))
                    {
                        found[path] = record;
                    }
                    else
                    {
                        found[path] = null!;  // placeholder keeps the path claimed
                        toRead.Add(info);
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Gone or locked since it was listed.
                }
            }
        }

        var read = new ConcurrentBag<FileRecord>();
        Parallel.ForEach(toRead, new ParallelOptions { MaxDegreeOfParallelism = ReaderCount, CancellationToken = cancel }, info =>
        {
            try
            {
                read.Add(ReadFile(info));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        });
        cancel.ThrowIfCancellationRequested();

        foreach (var info in toRead) found.Remove(info.FullName);
        foreach (var record in read) found[record.Path] = record;

        index.UpsertMany(read);
        index.RemoveMissing(found.Keys.ToHashSet(StringComparer.Ordinal), roots);
        index.ForgetOutside(roots);
        return new ScanResult(found.Values.ToList());
    }

    /// <summary>
    /// A fresh record for a new or changed file: its EXIF date (or the file
    /// date) and camera; the fingerprint is computed later.
    /// </summary>
    public static FileRecord ReadFile(FileInfo info)
    {
        info.Refresh();
        long size = info.Length, mtime = info.LastWriteTimeUtc.Ticks;
        var exif = Exif.Read(info.FullName);
        return new FileRecord(info.FullName, size, mtime, exif.Taken ?? info.LastWriteTime, exif.Taken is not null,
                              exif.Camera, null);
    }
}

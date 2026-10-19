using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SPhoto.Library;

/// <summary>
/// Computes the fingerprints a scan left pending, a batch at a time, so the
/// library's photo identities (tags, albums, duplicates) fill in shortly
/// after a scan without holding it up.
/// </summary>
public static class Fingerprinter
{
    public const int BatchSize = 64;

    /// <summary>
    /// Runs until no file is pending or cancel fires. progress gets the
    /// running count of fingerprints stored. Returns that count.
    /// </summary>
    public static int Run(LibraryIndex index, CancellationToken cancel = default, IProgress<int>? progress = null)
    {
        int done = 0;
        string? after = null;
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 4), CancellationToken = cancel };
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            var batch = index.PendingFingerprints(BatchSize, after);
            if (batch.Count == 0) return done;
            after = batch[^1].Path;

            var results = new ConcurrentBag<(string, string, long?, long?)>();
            Parallel.ForEach(batch, parallel, file =>
            {
                try
                {
                    var fp = Fingerprint.Compute(file.Path);
                    // Store it only when the file is still the one the index
                    // describes; otherwise the next scan resets it anyway.
                    var info = new FileInfo(file.Path);
                    if (info.Exists && file.Matches(info.Length, info.LastWriteTimeUtc.Ticks))
                    {
                        results.Add((file.Path, fp, file.Size, file.MtimeTicks));
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Locked or gone: passed over until the next run.
                }
            });
            done += index.SetFingerprints(results);
            SeparateCollisions(index, results.Select(r => r.Item2).Distinct(StringComparer.Ordinal),
                               batch.Select(f => f.Path).ToHashSet(StringComparer.Ordinal), cancel);
            progress?.Report(done);
        }
    }

    /// <summary>
    /// Sampled fingerprints shared by several files are checked against the
    /// whole content. Files that differ from the first one holding it get
    /// their full-content hash as identity, so different photos are never
    /// merged; real copies keep the shared fingerprint. Earlier holders were
    /// checked when they arrived, so one of them stands for all, and only
    /// this batch's files and that one are read in full.
    /// </summary>
    private static void SeparateCollisions(LibraryIndex index, IEnumerable<string> fingerprints,
                                           HashSet<string> batch, CancellationToken cancel)
    {
        foreach (var fp in fingerprints)
        {
            cancel.ThrowIfCancellationRequested();
            var holders = index.FilesWithFingerprint(fp);
            // The length is part of the fingerprint; short files are hashed whole already.
            if (holders.Count < 2 || holders[0].Size < 2 * Fingerprint.EdgeBytes) continue;
            var earlier = holders.Where(f => !batch.Contains(f.Path)).ToList();
            var arrived = holders.Where(f => batch.Contains(f.Path)).ToList();
            var candidates = earlier.Count > 0 ? earlier : arrived;
            string? reference = null;
            FileRecord? referenceFile = null;
            foreach (var file in candidates)
            {
                if (FullIfUnchanged(file) is { } full) { reference = full; referenceFile = file; break; }
            }
            // Nothing readable to compare with: keep the sampled identity.
            if (reference is null) continue;
            var separated = new List<(string, string, long?, long?)>();
            foreach (var file in arrived)
            {
                if (ReferenceEquals(file, referenceFile)) continue;
                if (FullIfUnchanged(file) is { } full && full != reference)
                {
                    separated.Add((file.Path, full, file.Size, file.MtimeTicks));
                }
            }
            if (separated.Count > 0) index.SetFingerprints(separated);
        }
    }

    private static string? FullIfUnchanged(FileRecord file)
    {
        try
        {
            var full = Fingerprint.ComputeFull(file.Path);
            var info = new FileInfo(file.Path);
            return info.Exists && file.Matches(info.Length, info.LastWriteTimeUtc.Ticks) ? full : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

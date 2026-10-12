using System;
using System.Collections.Concurrent;
using System.IO;
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
        bool migrating = index.NeedsFingerprintMigration;
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 4), CancellationToken = cancel };
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            var batch = index.PendingFingerprints(BatchSize, after);
            if (batch.Count == 0) { index.FinishFingerprintMigration(); return done; }
            after = batch[^1].Path;

            var results = new ConcurrentBag<(string, string, long?, long?, string?)>();
            Parallel.ForEach(batch, parallel, file =>
            {
                try
                {
                    var identity = migrating ? Fingerprint.ComputeForMigration(file.Path) : (Fingerprint.Compute(file.Path), (string?)null);
                    // Store it only when the file is still the one the index
                    // describes; otherwise the next scan resets it anyway.
                    var info = new FileInfo(file.Path);
                    if (info.Exists && file.Matches(info.Length, info.LastWriteTimeUtc.Ticks))
                    {
                        results.Add((file.Path, identity.Item1, file.Size, file.MtimeTicks, identity.Item2));
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Locked or gone: passed over until the next run.
                }
            });
            done += index.SetFingerprintsWithLegacy(results);
            progress?.Report(done);
        }
    }
}

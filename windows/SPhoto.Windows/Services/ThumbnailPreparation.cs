using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SPhoto.Services;

internal sealed record ThumbnailProgress(bool Running, int Completed, int Total, int Failed);

/// <summary>
/// Walks the library in order, including photos never scrolled into view.
/// Completed files are durable checkpoints, so a later run resumes naturally.
/// </summary>
internal sealed class ThumbnailPreparation : IDisposable
{
    private readonly string _directory;
    private readonly object _gate = new();
    private CancellationTokenSource? _current;
    private bool _disposed;
    private ThumbnailProgress _progress = new(false, 0, 0, 0);
    // Only ever touched by one run: each run waits for the one it replaced.
    private HashSet<string>? _ready;
    private bool _cleaned;

    private enum Outcome { Unavailable, Ready, Made }

    internal ThumbnailPreparation(string dataDirectory) => _directory = Path.Combine(dataDirectory, "thumbs");
    internal ThumbnailProgress Progress => Volatile.Read(ref _progress);
    internal Task Completion { get; private set; } = Task.CompletedTask;

    internal void Start(IReadOnlyList<PhotoEntry> photos)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _current?.Cancel();
            var run = _current = new CancellationTokenSource();
            _progress = new(true, 0, photos.Count, 0);
            var previous = Completion;
            Completion = Task.Run(async () =>
            {
                // The cancelled run stops at its next photo.
                await previous.ConfigureAwait(false);
                await RunAsync(photos, run).ConfigureAwait(false);
            });
        }
    }

    private async Task RunAsync(IReadOnlyList<PhotoEntry> photos, CancellationTokenSource run)
    {
        int done = 0, failed = 0, made = 0;
        double started = Perf.Now;
        var current = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // One directory listing per session instead of reading every
            // thumbnail back on each scan.
            var ready = _ready ??= ThumbnailCache.Names(_directory);
            foreach (var photo in photos)
            {
                run.Token.ThrowIfCancellationRequested();
                switch (await PrepareAsync(photo, ready, current, run.Token).ConfigureAwait(false))
                {
                    case Outcome.Unavailable: failed++; break;
                    case Outcome.Made: made++; break;
                }
                done++;
                lock (_gate)
                {
                    if (_current == run) _progress = new(true, done, photos.Count, failed);
                }
            }
            run.Token.ThrowIfCancellationRequested();
            // Old versions only need clearing once per session.
            if (!_cleaned)
            {
                ThumbnailCache.RemoveObsolete(_directory, current);
                _cleaned = true;
            }
            Perf.Line("prepare", $"finished {done} photos, {made} generated, {failed} unavailable, {Perf.Now - started:F0} ms");
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            failed++;
            Perf.Line("prepare", $"stopped (0x{e.HResult:X8})");
        }
        finally
        {
            lock (_gate)
            {
                if (_current == run)
                {
                    _progress = new(false, done, photos.Count, failed);
                    _current = null;
                }
                run.Dispose();
            }
        }
    }

    /// <summary>
    /// Makes sure one photo has a saved thumbnail. A photo prepared before
    /// costs a hash and a set lookup, with no disk access.
    /// </summary>
    private async Task<Outcome> PrepareAsync(PhotoEntry photo, HashSet<string> ready, HashSet<string> current, CancellationToken cancel)
    {
        try
        {
            if (photo.Bytes > 0 && photo.MtimeTicks > 0)
            {
                // The scan already read size and last change; cloud-only
                // files use another recipe, so they fall through to the check below.
                var known = Path.GetFileName(ThumbnailCache.FileFor(photo.Path, photo.Bytes, photo.MtimeTicks, false, _directory));
                if (ready.Contains(known))
                {
                    current.Add(known);
                    return Outcome.Ready;
                }
            }
            var file = ThumbnailCache.FileFor(photo.Path, _directory);
            if (file is null) return Outcome.Unavailable;
            var name = Path.GetFileName(file);
            current.Add(name);
            if (ready.Contains(name) || File.Exists(file))
            {
                ready.Add(name);
                return Outcome.Ready;
            }
            var bytes = await ThumbnailLoader.FetchAsync(photo.Path, cancel, _directory).ConfigureAwait(false);
            // Failed writes must not be advertised as ready for the next launch.
            if (bytes is null || !File.Exists(file)) return Outcome.Unavailable;
            ready.Add(name);
            return Outcome.Made;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return Outcome.Unavailable; }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _current?.Cancel();
        }
    }
}

using System;
using System.Threading;
using System.Threading.Tasks;
using SPhoto.Library;

namespace SPhoto.Services;

/// <summary>
/// Looks at every photo of the library once, in the background: a small
/// grayscale copy gives its dHash (for stacking near-identical shots) and
/// its sharpness and exposure (for picking a stack's cover). One thread at
/// below-normal priority, a photo at a time, so browsing and editing never
/// wait for it; the editor can also pause it outright.
/// </summary>
internal sealed class LibraryAnalyzer : IDisposable
{
    /// The long side of the copy measured. dHash needs only 9x8, but blur
    /// barely shows in the Laplacian of a smaller copy.
    internal const int Side = 256;

    private const int BatchSize = 8;

    /// Undecodable photos are logged, but a folder of broken files must
    /// not fill the log.
    private const int MaxLogged = 20;

    private readonly LibraryIndex _index;
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private readonly ManualResetEventSlim _running = new(true);
    private readonly CancellationTokenSource _stop = new();
    private int _logged;
    private volatile bool _working;

    internal bool IsWorking => _working;

    /// Raised on the analyzer's thread after it stored results.
    internal event Action? Stored;

    internal LibraryAnalyzer(LibraryIndex index)
    {
        _index = index;
        _thread = new Thread(Loop) { IsBackground = true, Name = "SPhoto analyzer", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// Looks for pending photos now (after a scan has fingerprinted new ones).
    internal void Wake()
    {
        _working = true;
        _wake.Set();
    }

    /// Stops after the current photo until Resume; for the editor.
    internal void Pause() => _running.Reset();

    internal void Resume() => _running.Set();

    internal bool IsPaused => !_running.IsSet;

    private void Loop()
    {
        var stop = _stop.Token;
        try
        {
            while (true)
            {
                WaitHandle.WaitAny(new[] { _wake, stop.WaitHandle });
                stop.ThrowIfCancellationRequested();
                _working = true;
                while (true)
                {
                    _running.Wait(stop);
                    var batch = _index.PendingAnalysis(BatchSize);
                    if (batch.Count == 0)
                    {
                        _working = false;
                        break;
                    }
                    foreach (var job in batch)
                    {
                        _running.Wait(stop);
                        Analyze(job, stop);
                    }
                    try
                    {
                        Stored?.Invoke();
                    }
                    catch (Exception e)
                    {
                        Crash.Log(e);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
            // The library closed under us.
        }
        catch (Exception e)
        {
            // Never take the app down; analysis resumes on the next start.
            Crash.Log(e);
        }
        finally { _working = false; }
    }

    private void Analyze(AnalysisJob job, CancellationToken stop)
    {
        try
        {
            var (gray, w, h) = LoadGrayAsync(job.Path).GetAwaiter().GetResult();
            stop.ThrowIfCancellationRequested();
            _index.SetAnalysis(job.Fingerprint, DHash.Compute(gray, w, h),
                               ImageStats.Sharpness(gray, w, h), ImageStats.Exposure(gray, w, h));
        }
        catch (Exception e) when (e is not OperationCanceledException and not ObjectDisposedException && !stop.IsCancellationRequested)
        {
            if (Interlocked.Increment(ref _logged) <= MaxLogged) Crash.Log($"analysing {job.Path}\r\n{e}");
            // Recorded as failed so it is not tried again and again.
            _index.SetAnalysisFailed(job.Fingerprint);
        }
    }

    /// <summary>
    /// A grayscale copy decoded directly from the original. Analysis keeps
    /// its uncompressed RGBA input, independently of the gallery's JPEGs.
    /// </summary>
    private static async Task<(byte[] Gray, int Width, int Height)> LoadGrayAsync(string path)
    {
        var pixels = await ImageIO.DecodeAsync(path, Side, bgra: false);
        return (ToGray(pixels.Data, pixels.Width * pixels.Height), pixels.Width, pixels.Height);
    }

    /// Rec. 601 luma from RGBA: what the eye calls sharp and bright.
    private static byte[] ToGray(byte[] rgba, int count)
    {
        var gray = new byte[count];
        for (int i = 0, p = 0; i < count; i++, p += 4)
        {
            gray[i] = (byte)((rgba[p] * 77 + rgba[p + 1] * 150 + rgba[p + 2] * 29) >> 8);
        }
        return gray;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _running.Set();
        // A decode in flight finishes on its own; do not hold up closing for it.
        _thread.Join(TimeSpan.FromSeconds(2));
    }
}

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace SPhoto.Services;

/// <summary>
/// Timings of the slow paths (thumbnails, opening a photo, opening the
/// editor) written to the app's data directory, fresh each run, so a
/// slow start or a flash on someone's PC can be read as numbers rather
/// than guessed at. Lines are queued and written in the background; a run
/// writes at most a few thousand.
/// </summary>
internal static class Perf
{
    private const int MaxLines = 4000;

    internal static string LogPath =>
        Path.Combine(PhotoLibrary.DataDir, "perf.log");

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly ConcurrentQueue<string> Pending = new();
    private static readonly Timer Flusher = new(_ => Flush(), null, 1000, 1000);
    private static int _lines;
    private static bool _started;

    /// Milliseconds since the app started.
    internal static double Now => Clock.Elapsed.TotalMilliseconds;

    internal static void Line(string area, string text)
    {
        if (Interlocked.Increment(ref _lines) > MaxLines) return;
        Pending.Enqueue($"{Now,9:F0} ms  {area,-7} {text}");
    }

    private static readonly object WriteLock = new();

    private static void Flush()
    {
        if (Pending.IsEmpty || !Monitor.TryEnter(WriteLock)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            using var writer = new StreamWriter(LogPath, append: _started);
            if (!_started) writer.WriteLine($"SPhoto {typeof(Perf).Assembly.GetName().Version} {DateTime.Now:O}");
            _started = true;
            while (Pending.TryDequeue(out var line)) writer.WriteLine(line);
        }
        catch (Exception)
        {
            // Diagnostics only.
        }
        finally
        {
            Monitor.Exit(WriteLock);
        }
    }
}

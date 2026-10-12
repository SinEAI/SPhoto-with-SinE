using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using SPhoto.Services;
using SPhoto.Ui;
using SPhoto.Views;
using Windows.Foundation;

namespace SPhoto;

/// <summary>
/// "SPhoto.exe --stress-test LOG [COUNT]" checks that a library of tens of
/// thousands of photos stays usable. It writes COUNT photos (default
/// 50,000) into a temporary folder, scans them into a fresh library (with
/// SPHOTO_DATA_DIR pointing at a temporary folder too, so the index and
/// thumbnail cache start empty), then shows them all in one month-grouped
/// PhotoGrid and scrolls from top to bottom while measuring.
///
/// Pass thresholds, lenient for a CI machine without a GPU (tighten them
/// once real numbers are in):
///   - first scan under 120 s, second (indexed) scan under 20 s;
///   - 95th percentile UI frame gap while scrolling under 100 ms;
///   - at least 80% of the visible tiles show a thumbnail 2 s after the
///     scroll stops (checked mid-way, at the bottom and back at the top);
///   - process working set under 1.5 GB throughout.
/// The exit code is 0 when all pass.
/// </summary>
internal static class StressTest
{
    private const int Steps = 60;
    private const int Dated = 300;
    private const double MaxColdScanSeconds = 120;
    private const double MaxWarmScanSeconds = 20;
    private const double MaxP95FrameGapMs = 100;
    private const double MinShownFraction = 0.8;
    private const long MaxWorkingSet = 1536L << 20;

    /// Set by Configure; App.OnLaunched runs the test instead of the app.
    internal static string? LogPath { get; private set; }

    private static int _count = 50_000;
    private static string _root = "";

    /// <summary>
    /// Called by Main before anything reads PhotoLibrary.DataDir, so the
    /// library index, prefs and thumbnail cache all start empty.
    /// </summary>
    internal static void Configure(string log, string? count)
    {
        LogPath = Path.GetFullPath(log);
        if (int.TryParse(count, out var n) && n > 0) _count = n;
        _root = Path.Combine(Path.GetTempPath(), "SPhotoStress-" + Guid.NewGuid().ToString("N")[..8]);
        Environment.SetEnvironmentVariable("SPHOTO_DATA_DIR", Path.Combine(_root, "data"));
    }

    /// Runs the test on the UI thread and exits the app with its result.
    internal static async void Start()
    {
        var log = new UiTestLog();
        try
        {
            File.WriteAllText(LogPath!, "");
            log.FilePath = LogPath;
        }
        catch (Exception)
        {
        }
        // Whatever hangs, CI gets an answer.
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMinutes(25));
            log.Write("TIMED OUT");
            Environment.Exit(3);
        });
        try
        {
            await RunAsync(log);
        }
        catch (Exception e)
        {
            log.Fail($"unexpected: {e}");
        }
        log.Write(log.Failures == 0 ? "PASSED" : $"FAILED ({log.Failures})");
        var delete = Task.Run(() =>
        {
            try { Directory.Delete(_root, recursive: true); } catch (Exception) { }
        });
        await Task.WhenAny(delete, Task.Delay(60_000));
        // Application.Exit can end the process before Main returns its code.
        Environment.Exit(log.Failures == 0 ? 0 : 1);
    }

    private static async Task RunAsync(UiTestLog log)
    {
        var photos = Path.Combine(_root, "photos");
        log.Note($"{_count:N0} photos in {photos}, data in {PhotoLibrary.DataDir}");

        var watch = Stopwatch.StartNew();
        await Task.Run(() => WritePhotosAsync(photos, _count));
        log.Note($"wrote {_count:N0} photos in {watch.Elapsed.TotalSeconds:F1} s");

        // 1. Scanning.
        List<PhotoEntry> found;
        using (var library = new PhotoLibrary(seedPictures: false))
        {
            log.Check(library.AddFolder(photos), "added the photo folder");
            watch.Restart();
            found = await library.ScanAsync();
            double cold = watch.Elapsed.TotalSeconds;
            log.Check(found.Count == _count, $"first scan found {found.Count:N0} photos (want {_count:N0})");
            log.Check(cold < MaxColdScanSeconds, $"first scan took {cold:F1} s (limit {MaxColdScanSeconds} s)");
            watch.Restart();
            var again = await library.ScanAsync();
            double warm = watch.Elapsed.TotalSeconds;
            log.Check(again.Count == found.Count, $"second scan found {again.Count:N0} photos");
            log.Check(warm < MaxWarmScanSeconds, $"second scan took {warm:F1} s (limit {MaxWarmScanSeconds} s)");
            int months = found.Select(Month).Distinct().Count();
            int exif = found.Count(p => p.Taken.Year == 2008);
            log.Note($"{months} months; {exif} photos dated 2008 by EXIF");
            log.Check(exif == Math.Min(Dated, _count), $"EXIF dates read ({exif}, want {Math.Min(Dated, _count)})");
            foreach (var p in found.Where(p => p.Path.Contains("相机") && p.Taken.Year != 2008).Take(4))
                log.Note($"no EXIF date: {Path.GetFileName(p.Path)} {new FileInfo(p.Path).Length} bytes, " +
                         $"read again {SPhoto.Library.Exif.Read(p.Path).Taken?.ToString("O") ?? "none"}, " +
                         $"markers {Markers(p.Path)}");
        }

        // 2. The grid.
        var window = new Window { Title = "SPhoto stress test" };
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 900));
        var grid = new PhotoGrid(180, 180, openOnClick: true);
        window.Content = new Grid { Background = new SolidColorBrush(Colors.White), Children = { grid.View } };
        window.Activate();
        watch.Restart();
        grid.SetPhotos(found, Month);
        log.Note($"SetPhotos took {watch.Elapsed.TotalMilliseconds:F0} ms on the UI thread");

        ScrollViewer? scroller = null;
        log.Check(await UiTestLog.WaitAsync(() => (scroller = Find<ScrollViewer>(grid.View)) is { ScrollableHeight: > 0 }, 30_000),
                  "the grid shows its scroll viewer");
        if (scroller is null) return;
        log.Note($"window loaded after {watch.Elapsed.TotalMilliseconds:F0} ms; extent {scroller.ExtentHeight:F0} px");

        var gaps = new List<double>();
        var clock = Stopwatch.StartNew();
        double last = -1;
        void OnFrame(object? sender, object e)
        {
            double t = clock.Elapsed.TotalMilliseconds;
            if (last >= 0) gaps.Add(t - last);
            last = t;
        }
        long peak = WorkingSet();
        var within1s = new List<double>();
        CompositionTarget.Rendering += OnFrame;
        try
        {
            // 3. Top to bottom, stopping a moment at each step.
            for (int step = 1; step <= Steps; step++)
            {
                scroller.ChangeView(null, scroller.ScrollableHeight * step / Steps, null, disableAnimation: true);
                var shown = await SettleAsync(grid.View, scroller, 1000);
                within1s.Add(shown);
                peak = Math.Max(peak, WorkingSet());
                if (step % 20 == 0)
                {
                    // Stopped: give it the full 2 s.
                    shown = await SettleAsync(grid.View, scroller, 1000);
                    log.Check(shown >= MinShownFraction,
                              $"step {step}/{Steps}: {shown:P0} of the visible tiles show a thumbnail within 2 s (want {MinShownFraction:P0})");
                }
            }
            var stats = ThumbnailLoader.Stats;
            log.Note($"cold pass: thumbnails within 1 s at each step {Summary(within1s)}; " +
                     $"loader {stats.Hits} hits, {stats.Misses} made, {stats.Cancelled} cancelled unstarted, {stats.Failed} failed");

            // 4. Back to the top: recent thumbnails, then the disk cache.
            scroller.ChangeView(null, 0, null, disableAnimation: true);
            var top = await SettleAsync(grid.View, scroller, 2000);
            log.Check(top >= MinShownFraction, $"back at the top: {top:P0} of the visible tiles show a thumbnail within 2 s");

            // 5. Again, now that thumbnails are cached on disk.
            var hitsBefore = ThumbnailLoader.Stats.Hits;
            var warm = new List<double>();
            for (int step = 1; step <= Steps / 2; step++)
            {
                scroller.ChangeView(null, scroller.ScrollableHeight * step * 2 / Steps, null, disableAnimation: true);
                warm.Add(await SettleAsync(grid.View, scroller, 500));
                peak = Math.Max(peak, WorkingSet());
            }
            log.Note($"warm pass: thumbnails within 0.5 s {Summary(warm)}; {ThumbnailLoader.Stats.Hits - hitsBefore} disk cache hits");
        }
        finally
        {
            CompositionTarget.Rendering -= OnFrame;
        }

        // 6. Results.
        if (gaps.Count > 0)
        {
            gaps.Sort();
            double p50 = Percentile(gaps, 0.5), p95 = Percentile(gaps, 0.95), max = gaps[^1];
            log.Note($"frame gaps over {gaps.Count} frames: p50 {p50:F1} ms, p95 {p95:F1} ms, max {max:F1} ms, " +
                     $"{gaps.Count(g => g > 100)} over 100 ms");
            log.Check(p95 < MaxP95FrameGapMs, $"p95 frame gap {p95:F1} ms (limit {MaxP95FrameGapMs} ms)");
        }
        else
        {
            log.Fail("no frames were rendered while scrolling");
        }
        log.Check(peak < MaxWorkingSet, $"peak working set {Shell.FormatBytes(peak)} (limit {Shell.FormatBytes(MaxWorkingSet)})");
        await Task.Delay(1000);  // background cache writes
        var (files, bytes) = await Task.Run(ThumbnailCache.Measure);
        log.Check(files > 0, $"thumbnail cache holds {files:N0} files, {Shell.FormatBytes(bytes)}");
        window.Close();
    }

    private static string Month(PhotoEntry p) => $"{p.Taken.Year} 年 {p.Taken.Month} 月";

    private static long WorkingSet()
    {
        using var process = Process.GetCurrentProcess();
        return process.WorkingSet64;
    }

    private static double Percentile(List<double> sorted, double p) => sorted[(int)Math.Round(p * (sorted.Count - 1))];

    private static string Summary(List<double> fractions)
    {
        if (fractions.Count == 0) return "(none)";
        var sorted = fractions.OrderBy(f => f).ToList();
        return $"median {Percentile(sorted, 0.5):P0}, worst {sorted[0]:P0}, mean {fractions.Average():P0}";
    }

    /// <summary>
    /// Waits up to timeoutMs for the visible tiles to show thumbnails;
    /// returns the fraction that do (1 when no tile is visible yet).
    /// </summary>
    private static async Task<double> SettleAsync(ListViewBase view, ScrollViewer scroller, int timeoutMs)
    {
        var start = Environment.TickCount64;
        while (true)
        {
            await Task.Delay(100);
            var (shown, visible) = VisibleTiles(view, scroller);
            double fraction = visible == 0 ? 1 : (double)shown / visible;
            if ((visible > 0 && shown == visible) || Environment.TickCount64 - start >= timeoutMs) return fraction;
        }
    }

    /// Realised tiles inside the viewport, and how many have a thumbnail.
    private static (int Shown, int Visible) VisibleTiles(ListViewBase view, ScrollViewer scroller)
    {
        int shown = 0, visible = 0;
        IEnumerable<DependencyObject> containers = view.ItemsPanelRoot is { } panel
            ? panel.Children
            : Descendants(view);
        foreach (var element in containers)
        {
            if (element is not SelectorItem { Content: PhotoItem item } container) continue;
            if (container.Visibility != Visibility.Visible || container.ActualHeight <= 0) continue;
            Rect bounds;
            try
            {
                bounds = container.TransformToVisual(scroller)
                    .TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
            }
            catch (Exception)
            {
                continue;  // not in the tree any more
            }
            if (bounds.Bottom <= 0 || bounds.Top >= scroller.ViewportHeight) continue;
            visible++;
            if (item.Thumbnail is not null) shown++;
        }
        return (shown, visible);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    private static T? Find<T>(DependencyObject root) where T : DependencyObject =>
        Descendants(root).OfType<T>().FirstOrDefault();

    /// The JPEG segment markers and sizes up to the image data, for diagnosis.
    private static string Markers(string path)
    {
        try
        {
            var b = File.ReadAllBytes(path);
            var parts = new List<string>();
            int at = 2;
            while (at + 4 <= b.Length && parts.Count < 12 && b[at] == 0xFF)
            {
                byte m = b[at + 1];
                int size = (b[at + 2] << 8) | b[at + 3];
                parts.Add($"{m:X2}:{size}" + (m == 0xE1 ? "(" + System.Text.Encoding.ASCII.GetString(b, at + 4, Math.Min(4, b.Length - at - 4)) + ")" : ""));
                if (m == 0xDA) break;
                at += 2 + size;
            }
            return string.Join(" ", parts);
        }
        catch (Exception e)
        {
            return e.Message;
        }
    }

    /// <summary>
    /// Writes count photos fast: a few distinct small JPEGs, copied under
    /// varied names into nested folders with spread-out file dates, plus a
    /// few hundred camera JPEGs with real EXIF dates (all in 2008, before
    /// any file date).
    /// </summary>
    private static async Task WritePhotosAsync(string folder, int count)
    {
        Directory.CreateDirectory(folder);
        var bases = new List<byte[]>();
        for (int i = 0; i < 8; i++)
        {
            var file = Path.Combine(folder, $"base{i}.tmp.jpg");
            await ImageIO.EncodeAsync(Pattern(640, 480, i), file, null);
            bases.Add(await File.ReadAllBytesAsync(file));
            File.Delete(file);
        }

        int dated = Math.Min(Dated, count);
        var camera = Path.Combine(folder, "2008", "相机");
        Directory.CreateDirectory(camera);
        for (int i = 0; i < dated; i++)
        {
            var taken = new DateTimeOffset(2008, 1, 1, 9, 0, 0, TimeSpan.Zero).AddDays(i * 364.0 / dated);
            await SelfTest.WriteCameraJpegAsync(Path.Combine(camera, $"IMG_{i + 1:0000}.JPG"), Pattern(320, 240, i), taken);
        }

        // Spread over ten years so the grid has ~120 month groups.
        var first = new DateTime(2010, 1, 1, 12, 0, 0);
        int copies = count - dated;
        const int perFolder = 100;
        string[] events = { "旅行", "Birthday", "家庭", "Hike", "Wedding", "日常" };
        Parallel.For(0, (copies + perFolder - 1) / perFolder, new ParallelOptions { MaxDegreeOfParallelism = 8 }, f =>
        {
            var date = first.AddDays(f * perFolder * 3650.0 / Math.Max(1, copies));
            var dir = Path.Combine(folder, date.Year.ToString(), $"{date:yyyy-MM} {events[f % events.Length]}");
            if (f % 3 == 0) dir = Path.Combine(dir, $"Day {f % 28 + 1}");
            Directory.CreateDirectory(dir);
            for (int j = 0; j < perFolder; j++)
            {
                int n = f * perFolder + j;
                if (n >= copies) break;
                var name = (n % 5) switch
                {
                    0 => $"IMG_{n:00000}.jpg",
                    1 => $"DSC{n:00000}.JPG",
                    2 => $"Photo {date:yyyy-MM-dd} ({n}).jpeg",
                    3 => $"微信图片_{n}.jpg",
                    _ => $"P{n}.jfif",
                };
                using var stream = new FileStream(Path.Combine(dir, name), FileMode.Create, FileAccess.Write);
                stream.Write(bases[n % bases.Count]);
                stream.Flush();
                File.SetLastWriteTime(stream.SafeFileHandle, date.AddMinutes(j * 37));
            }
        });
    }

    /// A smooth pattern (cheap to encode, small on disk), distinct per seed.
    private static Pixels Pattern(int w, int h, int seed)
    {
        var data = new byte[w * h * 4];
        int r0 = seed * 73 % 256, g0 = seed * 151 % 256, b0 = seed * 211 % 256;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                bool block = ((x / 80) + (y / 80) + seed) % 3 == 0;
                data[i] = (byte)((r0 + x * 255 / w) % 256);
                data[i + 1] = (byte)((g0 + y * 255 / h) % 256);
                data[i + 2] = block ? (byte)255 : (byte)b0;
                data[i + 3] = 255;
            }
        }
        return new Pixels(data, w, h);
    }
}

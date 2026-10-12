using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using FileAttributes = System.IO.FileAttributes;

namespace SPhoto.Services;

/// <summary>One upright, 384 px JPEG per file version, shared by every view.</summary>
internal static class ThumbnailCache
{
    internal const uint Side = 384;
    internal static string Dir => Path.Combine(PhotoLibrary.DataDir, "thumbs");

    // Windows marks files a sync provider such as OneDrive keeps only in
    // the cloud; reading their bytes downloads them.
    private const FileAttributes RecallOnOpen = (FileAttributes)0x40000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x400000;

    /// File metadata and this recipe identify the result. Called off the UI thread.
    internal static string? FileFor(string path, string? directory = null)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            return FileFor(path, info.Length, info.LastWriteTimeUtc.Ticks, IsCloudOnly(info.Attributes), directory);
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// The cache file for a photo whose size and last change are already
    /// known, without touching the disk. A cloud-only file gets its own
    /// recipe, so the Windows thumbnail it shows meanwhile is replaced by
    /// one made from the original once the file is on this PC.
    /// </summary>
    internal static string FileFor(string path, long length, long mtimeTicks, bool cloudOnly, string? directory = null)
    {
        var recipe = cloudOnly ? "shell-v1" : "original-v1";
        var key = $"{recipe}|{Path.GetFullPath(path).ToLowerInvariant()}|{length}|{mtimeTicks}|{Side}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)), 0, 16).ToLowerInvariant();
        return Path.Combine(directory ?? Dir, hash[..2], hash + ".jpg");
    }

    internal static bool IsCloudOnly(FileAttributes attributes) =>
        (attributes & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0;

    internal static bool IsCloudOnly(string path)
    {
        try { return IsCloudOnly(File.GetAttributes(path)); }
        catch (Exception) { return false; }
    }

    /// The names of every thumbnail in a cache directory, read once.
    internal static HashSet<string> Names(string directory)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!Directory.Exists(directory)) return names;
            foreach (var file in Directory.EnumerateFiles(directory, "*.jpg", SearchOption.AllDirectories))
                names.Add(Path.GetFileName(file));
        }
        catch (Exception) { }
        return names;
    }

    internal static byte[]? TryRead(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            var bytes = File.ReadAllBytes(file);
            if (bytes.Length < 4 || bytes[^2] != 0xFF || bytes[^1] != 0xD9 || JpegSize(bytes) is null)
            {
                TryDelete(file);
                return null;
            }
            return bytes;
        }
        catch (Exception) { return null; }
    }

    /// Finish the atomic write before releasing the shared generation task.
    internal static bool Write(string file, byte[] jpeg)
    {
        var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(temp, jpeg);
            File.Move(temp, file, overwrite: true);
            return true;
        }
        catch (Exception) { return false; }
        finally { TryDelete(temp); }
    }

    /// Keep the prepared library intact. Only obsolete versions are cleaned up;
    /// recent files may belong to an in-flight scan or another open window.
    internal static void RemoveObsolete(string directory, ISet<string> current)
    {
        try
        {
            if (!Directory.Exists(directory)) return;
            var olderThan = DateTime.UtcNow.AddDays(-1);
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.jpg", SearchOption.AllDirectories))
            {
                if (!current.Contains(file.Name) && file.LastWriteTimeUtc < olderThan) TryDelete(file.FullName);
            }
        }
        catch (Exception) { }
    }

    internal static (int Files, long Bytes) Measure()
    {
        try
        {
            if (!Directory.Exists(Dir)) return (0, 0);
            var files = new DirectoryInfo(Dir).EnumerateFiles("*.jpg", SearchOption.AllDirectories).ToList();
            return (files.Count, files.Sum(f => f.Length));
        }
        catch (Exception) { return (0, 0); }
    }

    internal static async Task<byte[]?> CreateAsync(string path)
    {
        double started = Perf.Now;
        try
        {
            // Never read a cloud-only original: that would download it.
            var pixels = IsCloudOnly(path)
                ? await ShellAsync(path).ConfigureAwait(false)
                : await ImageIO.DecodeAsync(path, (int)Side, bgra: true).ConfigureAwait(false);
            if (pixels is null)
            {
                Perf.Line("thumb", $"no Windows thumbnail for a cloud-only file after {Perf.Now - started:F0} ms");
                return null;
            }
            var jpeg = await EncodeAsync(Flatten(pixels)).ConfigureAwait(false);
            Perf.Line("thumb", $"generated from {(IsCloudOnly(path) ? "Windows thumbnail" : "original")} in {Perf.Now - started:F0} ms");
            return jpeg;
        }
        catch (Exception e)
        {
            Perf.Line("thumb", $"generation failed (0x{e.HResult:X8}) after {Perf.Now - started:F0} ms");
            return null;
        }
    }

    /// <summary>
    /// The thumbnail Windows shows for a file, fitted into Side. Sync
    /// providers serve these for cloud-only files. Null when Windows only
    /// has an icon.
    /// </summary>
    private static async Task<Pixels?> ShellAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var thumb = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, Side, ThumbnailOptions.ResizeThumbnail);
        if (thumb is null || thumb.Type != ThumbnailType.Image) return null;
        var decoder = await BitmapDecoder.CreateAsync(thumb);
        int w = (int)decoder.PixelWidth, h = (int)decoder.PixelHeight;
        double scale = Math.Min(1.0, (double)Side / Math.Max(w, h));
        int sw = Math.Max(1, (int)Math.Round(w * scale)), sh = Math.Max(1, (int)Math.Round(h * scale));
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)sw,
            ScaledHeight = (uint)sh,
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        // Windows thumbnails are upright already.
        var data = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.ColorManageToSRgb);
        return new Pixels(data.DetachPixelData(), sw, sh);
    }

    /// Premultiplied transparency over the gallery's white background.
    private static Pixels Flatten(Pixels bgra)
    {
        var d = bgra.Data;
        for (int i = 3; i < d.Length; i += 4)
        {
            int missing = 255 - d[i];
            if (missing == 0) continue;
            d[i - 3] = (byte)Math.Min(255, d[i - 3] + missing);
            d[i - 2] = (byte)Math.Min(255, d[i - 2] + missing);
            d[i - 1] = (byte)Math.Min(255, d[i - 1] + missing);
            d[i] = 255;
        }
        return bgra;
    }

    private static async Task<byte[]> EncodeAsync(Pixels bgra)
    {
        using var memory = new MemoryStream();
        using var stream = memory.AsRandomAccessStream();
        var options = new BitmapPropertySet
        {
            { "ImageQuality", new BitmapTypedValue(0.85f, Windows.Foundation.PropertyType.Single) },
        };
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream, options);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
            (uint)bgra.Width, (uint)bgra.Height, 96, 96, bgra.Data);
        await encoder.FlushAsync();
        return memory.ToArray();
    }

    internal static (int Width, int Height)? JpegSize(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return null;
        int pos = 2;
        while (pos + 9 < jpeg.Length && jpeg[pos] == 0xFF)
        {
            byte marker = jpeg[pos + 1];
            int size = (jpeg[pos + 2] << 8) | jpeg[pos + 3];
            if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
            {
                int h = (jpeg[pos + 5] << 8) | jpeg[pos + 6];
                int w = (jpeg[pos + 7] << 8) | jpeg[pos + 8];
                return w > 0 && h > 0 ? (w, h) : null;
            }
            if (marker == 0xDA || size < 2) break;
            pos += 2 + size;
        }
        return null;
    }

    internal static bool TryDelete(string file)
    {
        try { File.Delete(file); return true; }
        catch (Exception) { return false; }
    }
}

/// <summary>
/// Cache reads never wait for generation. Misses join one FIFO worker;
/// preparation and all views share one task for each file version.
/// A recycled tile cancels its wait, not the shared generation.
/// </summary>
internal static class ThumbnailLoader
{
    private sealed record Job(string Path, string File, string Directory)
    {
        internal TaskCompletionSource<byte[]?> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Job> Pending = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<Job> Queue = new();
    private static bool _running;
    private static long _hits, _misses, _cancelled, _failed;

    internal static (long Hits, long Misses, long Cancelled, long Failed, int Queued) Stats
    {
        get { lock (Gate) return (_hits, _misses, _cancelled, _failed, Queue.Count); }
    }

    internal static async Task<byte[]?> FetchAsync(string path, CancellationToken cancel = default, string? directory = null)
    {
        cancel.ThrowIfCancellationRequested();
        directory ??= ThumbnailCache.Dir;
        var probe = await Task.Run(() =>
        {
            var file = ThumbnailCache.FileFor(path, directory);
            return (File: file, Bytes: file is null ? null : ThumbnailCache.TryRead(file));
        }, cancel).ConfigureAwait(false);
        if (probe.Bytes is { } cached)
        {
            Interlocked.Increment(ref _hits);
            return cached;
        }
        if (probe.File is null) return null;
        cancel.ThrowIfCancellationRequested();
        Job job;
        lock (Gate)
        {
            if (!Pending.TryGetValue(probe.File, out job!))
            {
                job = new Job(path, probe.File, directory);
                Pending.Add(probe.File, job);
                Queue.Enqueue(job);
                if (!_running)
                {
                    _running = true;
                    _ = Task.Run(RunAsync);
                }
            }
        }
        try { return await job.Done.Task.WaitAsync(cancel).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            Interlocked.Increment(ref _cancelled);
            throw;
        }
    }

    private static async Task RunAsync()
    {
        while (true)
        {
            Job job;
            lock (Gate)
            {
                if (!Queue.TryDequeue(out job!)) { _running = false; return; }
            }
            byte[]? bytes = null;
            try
            {
                // A probe may have raced a just-completed atomic write.
                bytes = ThumbnailCache.TryRead(job.File);
                if (bytes is null)
                {
                    Interlocked.Increment(ref _misses);
                    bytes = await ThumbnailCache.CreateAsync(job.Path).ConfigureAwait(false);
                    // Never save pixels under an old file version's key.
                    if (ThumbnailCache.FileFor(job.Path, job.Directory) != job.File) bytes = null;
                    if (bytes is not null) ThumbnailCache.Write(job.File, bytes);
                    else Interlocked.Increment(ref _failed);
                }
                else Interlocked.Increment(ref _hits);
            }
            catch (Exception) { Interlocked.Increment(ref _failed); }
            finally
            {
                lock (Gate)
                {
                    job.Done.TrySetResult(bytes);
                    Pending.Remove(job.File);
                }
            }
        }
    }
}

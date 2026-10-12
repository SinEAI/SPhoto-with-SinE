using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SPhoto.Editing;
using SPhoto.Services;
using Windows.Foundation;
using Windows.Graphics.Imaging;

namespace SPhoto;

/// <summary>
/// "SPhoto.exe --self-test LOG" runs the app's whole photo path without a
/// window — make photos, scan them into a library, read their dates, decode
/// them upright, render an edit on the GPU and the CPU, save a copy and read
/// it back — and writes what happened to LOG. CI runs it on every change.
/// </summary>
internal static class SelfTest
{
    private static readonly StringBuilder Log = new();
    private static int _failures;

    internal static int Run(string logPath)
    {
        try
        {
            // On a worker (MTA) thread: blocking the STA main thread while
            // WinRT calls might want to come back to it could deadlock.
            Task.Run(RunAsync).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            Fail($"unexpected: {e}");
        }
        Log.AppendLine(_failures == 0 ? "PASSED" : $"FAILED ({_failures})");
        File.WriteAllText(logPath, Log.ToString());
        return _failures == 0 ? 0 : 1;
    }

    private static void Pass(string what) => Log.AppendLine($"ok    {what}");

    private static void Fail(string what)
    {
        _failures++;
        Log.AppendLine($"FAIL  {what}");
    }

    private static void Check(bool ok, string what)
    {
        if (ok) Pass(what);
        else Fail(what);
    }

    private static void Note(string what) => Log.AppendLine($"note  {what}");

    // Quadrants: red, green / blue, white.
    internal static Pixels Quadrants(int w, int h)
    {
        var data = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                bool right = x >= w / 2, bottom = y >= h / 2;
                var (r, g, b) = (right, bottom) switch
                {
                    (false, false) => (255, 0, 0),
                    (true, false) => (0, 255, 0),
                    (false, true) => (0, 0, 255),
                    _ => (255, 255, 255),
                };
                int i = (y * w + x) * 4;
                data[i] = (byte)r;
                data[i + 1] = (byte)g;
                data[i + 2] = (byte)b;
                data[i + 3] = 255;
            }
        }
        return new Pixels(data, w, h);
    }

    private static (int R, int G, int B) At(Pixels p, double fx, double fy)
    {
        int x = (int)(fx * (p.Width - 1)), y = (int)(fy * (p.Height - 1));
        int i = (y * p.Width + x) * 4;
        return (p.Data[i], p.Data[i + 1], p.Data[i + 2]);
    }

    private static bool Near((int R, int G, int B) c, (int R, int G, int B) want) =>
        Math.Abs(c.R - want.R) < 40 && Math.Abs(c.G - want.G) < 40 && Math.Abs(c.B - want.B) < 40;

    /// A JPEG as a camera writes it: stored sideways with EXIF orientation 6
    /// (turn 90 degrees clockwise to view) and a shooting date.
    internal static async Task WriteCameraJpegAsync(string path, Pixels stored, DateTimeOffset taken, string? camera = null)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream.AsRandomAccessStream());
        encoder.SetPixelData(BitmapPixelFormat.Rgba8, BitmapAlphaMode.Ignore,
                             (uint)stored.Width, (uint)stored.Height, 96, 96, stored.Data);
        await encoder.BitmapProperties.SetPropertiesAsync(new BitmapPropertySet
        {
            { "System.Photo.Orientation", new BitmapTypedValue((ushort)6, PropertyType.UInt16) },
            { "System.Photo.DateTaken", new BitmapTypedValue(taken, PropertyType.DateTime) },
        });
        if (camera is not null)
        {
            await encoder.BitmapProperties.SetPropertiesAsync(new BitmapPropertySet
            {
                { "System.Photo.CameraManufacturer", new BitmapTypedValue("SPhoto", PropertyType.String) },
                { "System.Photo.CameraModel", new BitmapTypedValue(camera, PropertyType.String) },
            });
        }
        await encoder.FlushAsync();
    }

    private static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "SPhotoSelfTest-" + Guid.NewGuid().ToString("N")[..8]);
        var photos = Path.Combine(root, "photos");
        Directory.CreateDirectory(Path.Combine(photos, "2021"));
        Note($"core {Native.Version}, {Native.sp_param_count()} adjustments, {Native.sp_filter_count()} filters");
        Check(Native.sp_param_count() == Edit.ParamCount, "core and app agree on the adjustment count");
        Check(System.Runtime.InteropServices.Marshal.SizeOf<Edit>() == Edit.NativeSize,
              "Edit marshals to the size of sp_edit");

        // 1. Photos on disk.
        var camera = Path.Combine(photos, "2021", "IMG_0001.jpg");
        // Windows writes System.Photo.DateTaken into EXIF in the machine's
        // own time zone; give the moment at local 07:08:09 so the EXIF reads
        // 07:08:09 on any machine, not only on a UTC one.
        var local = new DateTime(2021, 5, 6, 7, 8, 9);
        var taken = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
        await WriteCameraJpegAsync(camera, Quadrants(1200, 800), taken);
        var png = Path.Combine(photos, "screenshot.png");
        await ImageIO.EncodeAsync(Quadrants(320, 240), png, null);
        File.WriteAllText(Path.Combine(photos, "notes.txt"), "not a photo");
        Check(File.Exists(camera) && File.Exists(png), "wrote a camera JPEG and a PNG");

        // 2. The library finds both, with the JPEG's shooting date.
        using (var library = new PhotoLibrary(Path.Combine(root, "data"), seedPictures: false))
        {
            Check(library.Folders.Count == 0, "a test library starts with no folders");
            Check(library.AddFolder(photos), "added the photo folder");
            Check(!library.AddFolder(Path.Combine(photos, "2021")), "a folder inside an added one is refused");
            var found = await library.ScanAsync();
            Check(found.Count == 2, $"scan found {found.Count} photos (want 2)");
            var jpeg = found.FirstOrDefault(p => p.Path == camera);
            // Windows writes DateTaken's local wall clock into EXIF, which
            // has no time-zone offset. Keep the fixture valid outside UTC.
            Check(jpeg is not null && jpeg.Taken == taken.LocalDateTime,
                  $"JPEG date from EXIF: {jpeg?.Taken:O}");
            Check(found.Count == 2 && found[0].Path == png, "newest photo first");
            await library.Thumbnails.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            Check(library.Thumbnails.Progress is { Running: false, Completed: 2, Failed: 0 },
                "background preparation persists every photo without visiting a gallery");
            long generated = ThumbnailLoader.Stats.Misses;
            var again = await library.ScanAsync();
            await library.Thumbnails.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            Check(again.Select(p => p.Path).SequenceEqual(found.Select(p => p.Path)), "a second scan from the index agrees");
            Check(ThumbnailLoader.Stats.Misses == generated, "prepared photos are read without generating them again");
            Check((await library.CachedPhotosAsync()).Count == 2, "saved library is available before a folder scan");

            var added = Path.Combine(root, "added-folder");
            Directory.CreateDirectory(added);
            await ImageIO.EncodeAsync(Quadrants(640, 480), Path.Combine(added, "new.png"), null);
            library.AddFolder(added);
            await library.ScanAsync();
            await library.Thumbnails.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            Check(library.Thumbnails.Progress is { Completed: 3, Failed: 0 }
                && ThumbnailLoader.Stats.Misses == generated + 1, "adding a folder generates only its new image");
        }
        using (var reopened = new PhotoLibrary(Path.Combine(root, "data"), seedPictures: false))
        {
            Check((await reopened.CachedPhotosAsync()).Count == 3, "a reopened library immediately reads its saved index");
            long generated = ThumbnailLoader.Stats.Misses;
            await reopened.ScanAsync();
            await reopened.Thumbnails.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            Check(ThumbnailLoader.Stats.Misses == generated, "reopening a prepared library generates no thumbnails");
        }
        await CheckThumbnailsAsync(root, camera);

        // 3. Decoding turns the camera JPEG upright.
        var (w, h) = await ImageIO.SizeAsync(camera);
        Check((w, h) == (800, 1200), $"upright size {w}x{h} (want 800x1200)");
        var upright = await ImageIO.DecodeAsync(camera, 600, bgra: false);
        Check((upright.Width, upright.Height) == (400, 600), $"scaled decode {upright.Width}x{upright.Height} (want 400x600)");
        // Stored red|green over blue|white, turned clockwise: blue|red over white|green.
        bool upright_ok = Near(At(upright, 0.1, 0.1), (0, 0, 255)) && Near(At(upright, 0.9, 0.1), (255, 0, 0)) &&
                          Near(At(upright, 0.1, 0.9), (255, 255, 255)) && Near(At(upright, 0.9, 0.9), (0, 255, 0));
        Check(upright_ok, $"orientation applied: corners {At(upright, 0.1, 0.1)} {At(upright, 0.9, 0.1)} " +
                          $"{At(upright, 0.1, 0.9)} {At(upright, 0.9, 0.9)}");
        var details = await ImageIO.DetailsAsync(camera);
        Note($"details: {details.Width}x{details.Height}, {details.Bytes} bytes, taken {details.Taken:O}");
        var thumb = await ThumbnailCache.CreateAsync(camera);
        Check(thumb is not null && ThumbnailCache.JpegSize(thumb) == (256, 384),
            "app-generated thumbnail is scaled and upright");

        // 4. Turn and flip in the editor match the core.
        CheckGeometryMath();

        // 5. The GPU preview path, all on this thread (it owns the context).
        var full = await ImageIO.DecodeAsync(camera, ImageIO.MaxEditSide, bgra: false);
        var edit = EditState.Original
            .WithValue(Adjustment.ByKey("exposure").Index, 0.3f)
            .WithFilter("film", 0.8f)
            .WithGeometry(CropMath.FitRatio(CropMath.Turn(default, clockwise: true), 1.0, 1200, 800));
        RenderPreview(full, edit);

        // 6. Save a copy at full resolution and read it back.
        var copy = Path.Combine(photos, "2021", "IMG_0001_已编辑.jpg");
        var rendered = PhotoRenderer.RenderFull(full, edit);
        Check((rendered.Width, rendered.Height) == (800, 800), $"full render {rendered.Width}x{rendered.Height} (want 800x800)");
        await ImageIO.EncodeAsync(rendered, copy, camera);
        var (cw, ch) = await ImageIO.SizeAsync(copy);
        Check((cw, ch) == (800, 800), $"saved copy is {cw}x{ch}");
        var copyDate = ExifDate.Read(copy);
        Check(copyDate == taken.LocalDateTime, $"copy keeps the shooting date: {copyDate:O}");
        Check(File.Exists(camera) && (await ImageIO.SizeAsync(camera)) == (800, 1200), "original untouched");
        Check(!Directory.EnumerateFiles(photos, "*.sphoto-tmp", SearchOption.AllDirectories).Any(), "no temporary files left");

        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception)
        {
        }
    }

    private static async Task CheckThumbnailsAsync(string root, string camera)
    {
        var cache = Path.Combine(root, "shared-thumbnails");
        long before = ThumbnailLoader.Stats.Misses;
        using var cancelled = new CancellationTokenSource();
        var abandoned = ThumbnailLoader.FetchAsync(camera, cancelled.Token, cache);
        var requests = Enumerable.Range(0, 32).Select(_ => ThumbnailLoader.FetchAsync(camera, directory: cache)).ToArray();
        cancelled.Cancel();
        try { await abandoned; } catch (OperationCanceledException) { }
        var results = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(30));
        Check(results.All(b => b is not null) && ThumbnailLoader.Stats.Misses == before + 1,
            "32 concurrent consumers and a cancelled wait share one original decode");
        var file = ThumbnailCache.FileFor(camera, cache)!;
        Check(File.Exists(file) && ThumbnailCache.JpegSize(File.ReadAllBytes(file)) == (256, 384),
            "shared completion includes an atomically persisted, upright thumbnail");
        Check(!Directory.EnumerateFiles(cache, "*.tmp", SearchOption.AllDirectories).Any(),
            "thumbnail writes leave no temporary files");

        File.WriteAllBytes(file, new byte[] { 0xFF, 0xD8, 1, 2, 0xFF, 0xD9 });
        var repaired = await ThumbnailLoader.FetchAsync(camera, directory: cache);
        Check(repaired is not null && ThumbnailCache.JpegSize(repaired) == (256, 384)
            && ThumbnailLoader.Stats.Misses == before + 2, "a damaged cache entry is regenerated once");

        var mutable = Path.Combine(root, "modified.png");
        await ImageIO.EncodeAsync(Quadrants(640, 480), mutable, null);
        await ThumbnailLoader.FetchAsync(mutable, directory: cache);
        var oldKey = ThumbnailCache.FileFor(mutable, cache);
        await ImageIO.EncodeAsync(Quadrants(320, 640), mutable, null);
        var changed = await ThumbnailLoader.FetchAsync(mutable, directory: cache);
        Check(oldKey != ThumbnailCache.FileFor(mutable, cache) && changed is not null
            && ThumbnailCache.JpegSize(changed) == (192, 384), "changing a source file invalidates its cached pixels");

        var broken = Path.Combine(root, "broken.png");
        File.WriteAllText(broken, "synthetic invalid image");
        var invalid = await ThumbnailLoader.FetchAsync(broken, directory: cache);
        var healthy = await ThumbnailLoader.FetchAsync(camera, directory: cache);
        Check(invalid is null && healthy is not null, "an invalid image does not stop subsequent cached images");

        var transparent = Path.Combine(root, "transparent.png");
        var pixels = new Pixels(new byte[32 * 32 * 4], 32, 32);
        await ImageIO.EncodeAsync(pixels, transparent, null);
        await ThumbnailLoader.FetchAsync(transparent, directory: cache);
        var white = await ImageIO.DecodeAsync(ThumbnailCache.FileFor(transparent, cache)!, 32, bgra: false);
        Check(Near(At(white, 0.5, 0.5), (255, 255, 255)), "transparent thumbnails use the gallery's white background");

        var obsolete = Path.Combine(cache, "obsolete.jpg");
        var recent = Path.Combine(cache, "recent.jpg");
        File.WriteAllBytes(obsolete, healthy!);
        File.WriteAllBytes(recent, healthy!);
        File.SetLastWriteTimeUtc(obsolete, DateTime.UtcNow.AddDays(-3));
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-3));
        ThumbnailCache.RemoveObsolete(cache, new HashSet<string> { Path.GetFileName(file) });
        Check(File.Exists(file) && File.Exists(recent) && !File.Exists(obsolete),
            "cache cleanup keeps prepared photos and in-flight writes, removing only old obsolete entries");

        var resumeRoot = Path.Combine(root, "resume");
        Directory.CreateDirectory(resumeRoot);
        var entries = Enumerable.Range(0, 64).Select(i =>
        {
            var copy = Path.Combine(resumeRoot, $"photo-{i:D3}.jpg");
            File.Copy(camera, copy);
            return new PhotoEntry(copy, DateTime.Today, new FileInfo(copy).Length);
        }).ToList();
        long started = ThumbnailLoader.Stats.Misses;
        using (var preparation = new ThumbnailPreparation(resumeRoot))
        {
            preparation.Start(entries);
            while (preparation.Progress.Completed == 0) await Task.Delay(5);
            preparation.Dispose();
            await preparation.Completion;
            Check(preparation.Progress.Completed is > 0 and < 64, "organization can stop partway through the folder");
        }
        using (var resumed = new ThumbnailPreparation(resumeRoot))
        {
            resumed.Start(entries);
            await resumed.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            Check(resumed.Progress is { Completed: 64, Failed: 0 } && ThumbnailLoader.Stats.Misses == started + 64,
                "resuming organization reuses every completed thumbnail without duplicate generation");
        }
        var blocked = Path.Combine(root, "unwritable-profile");
        Directory.CreateDirectory(blocked);
        File.WriteAllText(Path.Combine(blocked, "thumbs"), "synthetic obstruction");
        using var unwritable = new ThumbnailPreparation(blocked);
        unwritable.Start(new[] { new PhotoEntry(camera, DateTime.Today, 0) });
        await unwritable.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        Check(unwritable.Progress is { Completed: 1, Failed: 1 }, "a failed cache write is reported as unavailable, not ready");
    }

    private static void RenderPreview(Pixels full, EditState edit)
    {
        using var renderer = new PhotoRenderer();
        Note(renderer.IsGpu ? "renderer: GPU (OpenGL ES through ANGLE)" : "renderer: CPU fallback");
        renderer.SetImage(full);
        renderer.SetEdit(edit);
        var (ow, oh) = renderer.OutputSize(edit.Geometry);
        Check((ow, oh) == (800, 800), $"output size {ow}x{oh} (want 800x800)");
        var preview = new byte[200 * 200 * 4];
        Check(renderer.Render(preview, 200, 200), "preview rendered");
        var cpu = PhotoRenderer.RenderFull(full, edit);
        // Compare the preview with the CPU render at matching points.
        int worst = 0;
        for (int y = 10; y < 200; y += 20)
        {
            for (int x = 10; x < 200; x += 20)
            {
                if (Math.Abs(x - 100) < 12 || Math.Abs(y - 100) < 12) continue;  // quadrant edges
                int i = (y * 200 + x) * 4;
                int j = ((y * 4 + 2) * 800 + x * 4 + 2) * 4;
                for (int c = 0; c < 3; c++) worst = Math.Max(worst, Math.Abs(preview[i + c] - cpu.Data[j + c]));
            }
        }
        Check(worst <= 12, $"preview matches the CPU render (worst difference {worst})");
    }

    private static void CheckGeometryMath()
    {
        var src = Quadrants(60, 40);
        var start = new Geometry { Straighten = 6, Vertical = 0.3f, Horizontal = -0.2f, CropX = 0.1f, CropY = 0.2f, CropWidth = 0.5f, CropHeight = 0.6f };
        var before = PhotoRenderer.RenderFull(src, EditState.Original.WithGeometry(start));
        var ops = new (string Name, Func<Geometry, Geometry> Op, Func<int, int, int, int, (int, int)> Map)[]
        {
            // For output pixel (x, y) of the result: where it was before.
            ("turn right", g => CropMath.Turn(g, true), (x, y, w, h) => (y, h - 1 - x)),
            ("turn left", g => CropMath.Turn(g, false), (x, y, w, h) => (w - 1 - y, x)),
            ("flip", CropMath.Flip, (x, y, w, h) => (w - 1 - x, y)),
        };
        foreach (var (name, op, map) in ops)
        {
            var after = PhotoRenderer.RenderFull(src, EditState.Original.WithGeometry(op(start)));
            int worst = 0;
            for (int y = 0; y < after.Height; y++)
            {
                for (int x = 0; x < after.Width; x++)
                {
                    var (bx, by) = map(x, y, before.Width, before.Height);
                    if (bx < 0 || by < 0 || bx >= before.Width || by >= before.Height) { worst = 255; continue; }
                    for (int c = 0; c < 3; c++)
                    {
                        worst = Math.Max(worst, Math.Abs(after.Data[(y * after.Width + x) * 4 + c] -
                                                         before.Data[(by * before.Width + bx) * 4 + c]));
                    }
                }
            }
            Check(worst <= 2, $"{name} turns what the user sees (worst difference {worst})");
        }
    }
}

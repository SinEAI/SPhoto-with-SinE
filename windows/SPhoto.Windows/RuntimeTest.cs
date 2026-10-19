using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using SPhoto.Editing;
using SPhoto.Library;
using SPhoto.Services;

namespace SPhoto;

/// <summary>Opt-in measurements of production paths using generated data only.</summary>
internal static class RuntimeTest
{
    private sealed record Check(string Name, bool Passed, object Details);
    private static readonly List<Check> Checks = new();
    private static readonly List<object> Measurements = new();

    internal static int Run(string output)
    {
        string root = Path.Combine(Path.GetTempPath(), "SPhotoRuntime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { Task.Run(() => RunAsync(root)).GetAwaiter().GetResult(); }
        catch (Exception e) { Checks.Add(new("runtime_completed", false, new { exception = e.GetType().Name, e.HResult })); }
        var report = new
        {
            source_commit = Environment.GetEnvironmentVariable("GITHUB_SHA"),
            environment = new { os = RuntimeInformation.OSDescription, cpu_count = Environment.ProcessorCount,
                scope = "Generated images on a hosted Windows runner; no physical GPU or power claim" },
            checks = Checks, measurements = Measurements,
            passed = Checks.All(c => c.Passed)
        };
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        return report.passed ? 0 : 1;
    }

    private static async Task RunAsync(string root)
    {
        await Section("library_measurements", () => Task.Run(() => LibraryMeasurements(root)));
        await Section("identity_check", () => IdentityCheck(root));
        await Section("rendering_measurements", RenderingMeasurements);
        await Section("export_check", () => ExportCheck(root));
        foreach (string name in new[] { "valid_recipe", "non_finite_recipe", "deep_recipe" })
        {
            using var process = new Process { StartInfo = new ProcessStartInfo(Environment.ProcessPath!)
                { UseShellExecute = false, CreateNoWindow = true } };
            process.StartInfo.ArgumentList.Add("--runtime-probe");
            process.StartInfo.ArgumentList.Add(name);
            process.Start();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            bool timedOut = false;
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { timedOut = true; process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            Checks.Add(new(name == "valid_recipe" ? "valid_recipe_accepted" : name + "_rejected_safely", !timedOut && process.ExitCode == 0,
                new { exit_code = process.ExitCode, timed_out = timedOut }));
        }
    }

    private static async Task Section(string name, Func<Task> run)
    {
        try { await run(); Checks.Add(new(name + "_completed", true, new { })); }
        catch (Exception e) { Checks.Add(new(name + "_completed", false, new { exception = e.GetType().Name, e.HResult })); }
    }

    private static void LibraryMeasurements(string root)
    {
        foreach (int count in new[] { 1000, 10000 })
        {
            string folder = Path.Combine(root, "library-" + count);
            Directory.CreateDirectory(folder);
            byte[] bmp = Bitmap(64, 64);
            for (int i = 0; i < count; i++)
            {
                BitConverter.GetBytes(i).CopyTo(bmp, 54);
                string path = Path.Combine(folder, $"synthetic-{i:D5}.bmp");
                File.WriteAllBytes(path, bmp);
                File.SetLastWriteTimeUtc(path, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(i));
            }
            using var index = LibraryIndex.Open(Path.Combine(root, "index-" + count + ".db"));
            ScanResult? found = null;
            Measurements.Add(Measure("library_cold_scan_" + count, 1, () => found = Scanner.Scan(index, new[] { folder })));
            Checks.Add(new("cold_scan_count_" + count, found?.Files.Count == count, new { expected = count, actual = found?.Files.Count }));
            Measurements.Add(Measure("library_warm_scan_" + count, 5, () => found = Scanner.Scan(index, new[] { folder })));
            int hashed = 0;
            Measurements.Add(Measure("library_fingerprint_" + count, 1, () => hashed = Fingerprinter.Run(index)));
            Checks.Add(new("fingerprint_count_" + count, hashed == count, new { expected = count, actual = hashed }));
            Catalog? catalog = null;
            // Warm JIT separately. The measured loop still constructs a fresh snapshot each time.
            index.Catalog();
            Measurements.Add(Measure("library_catalog_" + count, 5, () => catalog = index.Catalog()));
            int actual = catalog!.Items(CatalogScope.All).Count;
            Checks.Add(new("catalog_count_" + count, actual == count, new { expected = count, actual }));
        }
    }

    private static async Task IdentityCheck(string root)
    {
        byte[] first = Bitmap(256, 256), second = (byte[])first.Clone();
        int offset = 54 + (128 * 256 + 128) * 3;
        second[offset] = 255;
        string a = Path.Combine(root, "identity-a.bmp"), b = Path.Combine(root, "identity-b.bmp");
        File.WriteAllBytes(a, first);
        File.WriteAllBytes(b, second);
        var pa = await ImageIO.DecodeAsync(a, 256, false);
        var pb = await ImageIO.DecodeAsync(b, 256, false);
        bool pixelsDiffer = !pa.Data.AsSpan().SequenceEqual(pb.Data);
        bool bytesDiffer = !SHA256.HashData(first).AsSpan().SequenceEqual(SHA256.HashData(second));
        using var index = LibraryIndex.Open(Path.Combine(root, "identity.db"));
        index.UpsertMany(new[] { a, b }.Select(p => Scanner.ReadFile(new FileInfo(p))));
        Fingerprinter.Run(index);
        var files = new[] { a, b }.Select(p => index.Known(p)!).ToArray();
        int distinct = index.Catalog().Items(CatalogScope.All).Count;
        Checks.Add(new("identity_fixture_has_different_decodable_pixels", pixelsDiffer && bytesDiffer, new { pixels_differ = pixelsDiffer, bytes_differ = bytesDiffer }));
        Checks.Add(new("different_photos_are_not_merged", distinct == 2, new { expected = 2, actual = distinct, fingerprints_equal = files[0].Fingerprint == files[1].Fingerprint }));
    }

    private static async Task RenderingMeasurements()
    {
        using var worker = new RenderWorker();
        bool gpu = await worker.IsGpuAsync;
        string? rendererName = await worker.RunAsync(_ => gpu ? Marshal.PtrToStringAnsi(GlGetString(0x1F01)) : "CPU fallback");
        Measurements.Add(new { name = "renderer", gles_available = gpu, renderer = rendererName });
        foreach (var (width, height) in new[] { (4000, 3000), (6000, 4000) })
        {
            var source = SelfTest.Quadrants(width, height);
            var state = EditState.Original.WithValue(0, 0.2f).WithFilter("film", 0.5f);
            foreach (int side in new[] { 1100, 2560 })
            {
                int h = (int)Math.Round((double)side * height / width);
                await worker.RunAsync(renderer =>
                {
                    if (RenderWorker.RenderNow(renderer, source, state, side, h) is null)
                        throw new InvalidOperationException("preview warmup failed");
                    Measurements.Add(Measure($"preview_{width}x{height}_{side}x{h}", 16, () =>
                    {
                        var frame = RenderWorker.RenderNow(renderer, source, state, side, h);
                        if (frame is null) throw new InvalidOperationException("preview failed");
                        GC.KeepAlive(frame);
                    }));
                    // Control: production renderer with a preallocated RGBA destination;
                    // excludes worker conversion and the UI bitmap copy, not a replacement implementation.
                    var buffer = new byte[side * h * 4];
                    Measurements.Add(Measure($"render_reused_rgba_{width}x{height}_{side}x{h}", 16, () =>
                    {
                        if (!renderer.Render(buffer, side, h)) throw new InvalidOperationException("render failed");
                    }));
                    var edit = state.ToNative();
                    if (Native.sp_render_cpu(ref edit, source.Data, width, height, buffer, side, h) != 0)
                        throw new InvalidOperationException("CPU warmup failed");
                    Measurements.Add(Measure($"cpu_reused_rgba_{width}x{height}_{side}x{h}", 8, () =>
                    {
                        if (Native.sp_render_cpu(ref edit, source.Data, width, height, buffer, side, h) != 0)
                            throw new InvalidOperationException("CPU render failed");
                    }));
                    return true;
                });
            }
            Measurements.Add(await worker.RunAsync(_ => Measure($"cpu_full_export_{width}x{height}", 1,
                () => GC.KeepAlive(PhotoRenderer.RenderFull(source, state)))));
        }

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var blocking = worker.RunAsync(_ => { entered.Set(); return release.Wait(TimeSpan.FromSeconds(20)); });
        if (!await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(10)))) throw new TimeoutException("worker barrier timed out");
        int callbacks = 0;
        var done = new TaskCompletionSource<RenderedFrame?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var image = SelfTest.Quadrants(64, 64);
        var latest = EditState.Original.WithValue(0, 0.5f);
        object owner = new();
        try
        {
            for (int i = 0; i < 100; i++) worker.RequestPreview(owner, image, i == 99 ? latest : EditState.Original, 64, 64,
                frame => { Interlocked.Increment(ref callbacks); done.TrySetResult(frame); });
        }
        finally { release.Set(); }
        await blocking;
        var result = await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await worker.RunAsync(_ => true);
        Checks.Add(new("preview_coalesces_100_pending_requests", callbacks == 1 && result?.State.Equals(latest) == true,
            new { submitted = 100, completed = callbacks }));
    }

    private static async Task ExportCheck(string root)
    {
        string original = Path.Combine(root, "wide-original.png"), copy = Path.Combine(root, "wide-copy.png");
        await ImageIO.EncodeAsync(SelfTest.Quadrants(17000, 64), original, null);
        var done = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = DispatcherQueueController.CreateOnDedicatedThread();
        controller.DispatcherQueue.TryEnqueue(async () =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(controller.DispatcherQueue));
            try
            {
                var session = new EditSession(original);
                await session.LoadAsync();
                await session.SaveToAsync(copy);
                var before = await ImageIO.SizeAsync(original);
                var after = await ImageIO.SizeAsync(copy);
                Checks.Add(new("unchanged_export_keeps_original_dimensions", before == after,
                    new { original_width = before.Width, original_height = before.Height, output_width = after.Width, output_height = after.Height }));
                Checks.Add(new("preview_geometry_uses_original_dimensions", session.OutputSize(default) == before,
                    new { expected_width = before.Width, expected_height = before.Height }));
                session.Turn(clockwise: true);
                string turned = System.IO.Path.Combine(root, "wide-turned.png");
                await session.SaveToAsync(turned);
                var rotated = await ImageIO.SizeAsync(turned);
                Checks.Add(new("rotated_large_export_keeps_original_resolution", rotated == (before.Height, before.Width),
                    new { width = rotated.Width, height = rotated.Height }));
                File.SetLastWriteTimeUtc(original, DateTime.UtcNow.AddMinutes(1));
                bool rejected = false;
                try { await session.SaveToAsync(System.IO.Path.Combine(root, "changed-copy.png")); }
                catch (InvalidOperationException) { rejected = true; }
                Checks.Add(new("changed_original_is_not_silently_redecoded", rejected, new { rejected }));
                done.SetResult(true);
            }
            catch (Exception e) { done.SetException(e); }
        });
        try { await done.Task.WaitAsync(TimeSpan.FromSeconds(60)); }
        finally { await controller.ShutdownQueueAsync(); }
    }

    private static object Measure(string name, int iterations, Action action)
    {
        var samples = new double[iterations];
        using var process = Process.GetCurrentProcess();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        var cpu = process.TotalProcessorTime;
        int gen2 = GC.CollectionCount(2);
        long peak = 0;
        for (int i = 0; i < iterations; i++)
        {
            long started = Stopwatch.GetTimestamp();
            action();
            samples[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            process.Refresh();
            peak = Math.Max(peak, process.WorkingSet64);
        }
        long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        double cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
        Array.Sort(samples);
        return new { name, iterations, median_ms = samples[iterations / 2], p95_ms = samples[(int)Math.Ceiling(iterations * 0.95) - 1],
            mean_ms = samples.Average(), managed_allocated_bytes_per_iteration = bytes / iterations,
            process_cpu_ms = cpuMs, gen2_collections = GC.CollectionCount(2) - gen2, sampled_working_set_bytes = peak,
            memory_scope = "Current measuring thread managed allocations; sampled whole-process working set includes retained resources" };
    }

    private static byte[] Bitmap(int width, int height)
    {
        // These fixture widths make RGB rows naturally four-byte aligned.
        var bmp = new byte[54 + width * height * 3];
        using var writer = new BinaryWriter(new MemoryStream(bmp));
        writer.Write((ushort)0x4D42); writer.Write(bmp.Length); writer.Write(0); writer.Write(54);
        writer.Write(40); writer.Write(width); writer.Write(height); writer.Write((ushort)1); writer.Write((ushort)24);
        return bmp;
    }

    // Crash-prone parser checks run in disposable children, never in the measurement process.
    internal static int Probe(string name)
    {
        SetErrorMode(0x0001 | 0x0002);
        string json = name switch
        {
            "non_finite_recipe" => "{\"format\":\"sphoto-recipe\",\"version\":1,\"filter\":{\"id\":\"film\",\"strength\":0e400}}",
            "deep_recipe" => "{\"format\":\"sphoto-recipe\",\"version\":1,\"unknown\":" + new string('[', 100000) + "0" + new string(']', 100000) + "}",
            "valid_recipe" => "{\"format\":\"sphoto-recipe\",\"version\":1}",
            _ => throw new ArgumentException("unknown probe")
        };
        var edit = EditState.Original.ToNative();
        int code = ReadRecipe(json, ref edit);
        if (code != 0) return name == "valid_recipe" ? 1 : 0;
        if (name == "non_finite_recipe" && (!float.IsFinite(edit.FilterStrength) || edit.Adjust.Any(v => !float.IsFinite(v)))) return 1;
        // Accepting excessive nesting also fails the bounded-input contract.
        return name == "deep_recipe" ? 1 : 0;
    }

    [DllImport("sphoto_core", EntryPoint = "sp_recipe_read_edit", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ReadRecipe([MarshalAs(UnmanagedType.LPStr)] string json, ref Edit edit);
    [DllImport("libGLESv2", EntryPoint = "glGetString", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr GlGetString(uint name);
    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);
}

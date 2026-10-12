using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using SPhoto.Editing;
using SPhoto.Services;

namespace SPhoto.Ui;

/// <summary>
/// "SPhoto.exe --ui-test LOG" opens the real window and walks the app the
/// way a user would: library and folders, stacks, copies, tags, pages,
/// viewer, editor (filter, slider, curves, HSL, rotate, crop, undo, save a
/// version), back out, settings, and opening a file from Explorer. CI runs
/// it on every change; LOG says what happened.
/// </summary>
internal static class UiTest
{
    /// Steps worth a line in the UI test log (null outside the test).
    internal static Action<string>? Trace { get; private set; }

    internal static async Task<int> RunAsync(string logPath)
    {
        var log = new UiTestLog();
        File.WriteAllText(logPath, "");
        log.FilePath = logPath;
        Trace = line => log.Write("trace " + line);
        Application.Current.DebugSettings.BindingFailed += (_, e) => log.Write("binding failed: " + e.Message);
        Application.Current.DebugSettings.XamlResourceReferenceFailed += (_, e) => log.Write("resource failed: " + e.Message);
        // Every exception thrown, caught or not, so a crash says where.
        int seen = 0;
        AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
        {
            if (Interlocked.Increment(ref seen) <= 40) log.Write($"exception {e.Exception.GetType().Name}: {e.Exception.Message}\n{e.Exception.StackTrace}");
        };
        // A dialog nobody answers would hang the test: give up after a while.
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMinutes(8));
            log.Write("TIMED OUT");
            Environment.Exit(3);
        });
        try
        {
            await RunAsync(log);
            // Source-presence checks miss a one-frame resize flash. Exercise
            // the return animation with rendering measurements as well.
            int returnCode = await EditReturnTest.RunAsync(Path.ChangeExtension(logPath, "edit-return.log"));
            log.Check(returnCode == 0, "editor return keeps continuous image geometry across rendered frames");
        }
        catch (Exception e)
        {
            log.Fail($"unexpected: {e}");
        }
        App.Tray?.Dispose();
        log.Write(log.Failures == 0 ? "PASSED" : $"FAILED ({log.Failures})");
        return log.Failures == 0 ? 0 : 1;
    }

    private static async Task RunAsync(UiTestLog log)
    {
        var root = Path.Combine(Path.GetTempPath(), "SPhotoUiTest-" + Guid.NewGuid().ToString("N")[..8]);
        var photos = Path.Combine(root, "照片");
        Directory.CreateDirectory(Path.Combine(photos, "旅行"));
        var camera = Path.Combine(photos, "旅行", "IMG_0001.jpg");
        await SelfTest.WriteCameraJpegAsync(camera, SelfTest.Quadrants(1200, 800), new DateTimeOffset(2021, 5, 6, 7, 8, 9, TimeSpan.Zero));
        await SelfTest.WriteCameraJpegAsync(Path.Combine(photos, "旅行", "IMG_0002.jpg"), SelfTest.Quadrants(900, 600), new DateTimeOffset(2021, 5, 6, 9, 0, 0, TimeSpan.Zero));
        await ImageIO.EncodeAsync(SelfTest.Quadrants(320, 240), Path.Combine(photos, "截图.png"), null);
        await ImageIO.EncodeAsync(SelfTest.Quadrants(160, 120), Path.Combine(photos, "000-first.png"), null);
        // Large enough that fitting it needs a zoom far below 1 (the
        // limit that crashed the viewer).
        var big = Path.Combine(photos, "全景.jpg");
        await ImageIO.EncodeAsync(SelfTest.Quadrants(6000, 4000), big, null);
        // A burst: two near-identical shots a second apart from one camera
        // (should stack), and a copy of a photo in another folder (one
        // photo in the library, two files in the folders).
        Directory.CreateDirectory(Path.Combine(photos, "连拍"));
        var burstAt = new DateTimeOffset(2022, 8, 1, 10, 0, 0, TimeSpan.Zero);
        await SelfTest.WriteCameraJpegAsync(Path.Combine(photos, "连拍", "DSC_0101.jpg"), SelfTest.Quadrants(1200, 800), burstAt, "Test 1");
        var burst2 = SelfTest.Quadrants(1200, 800);
        for (int i = 0; i < 4000; i += 4) burst2.Data[i] = (byte)(burst2.Data[i] ^ 0x10);
        await SelfTest.WriteCameraJpegAsync(Path.Combine(photos, "连拍", "DSC_0102.jpg"), burst2, burstAt.AddSeconds(1), "Test 1");
        Directory.CreateDirectory(Path.Combine(photos, "备份"));
        File.Copy(Path.Combine(photos, "旅行", "IMG_0002.jpg"), Path.Combine(photos, "备份", "IMG_0002.jpg"));
        foreach (var f in App.Library.Folders.ToList()) App.Library.RemoveFolder(f);
        log.Check(App.Library.AddFolder(photos), "added the test folder to the library");
        log.Note($"core {Native.Version}, GPU {(await App.Worker.IsGpuAsync ? "yes" : "no")}");

        foreach (var design in Designs.All)
        {
            log.Note($"--- design {design.Id} ({design.Title})");
            log.Note($"{design.Id}: creating");
            // main is already active when the test starts. Recreate it
            // with the fixture folders in place to exercise automatic
            // startup loading, rather than the same-design switch no-op.
            if (App.Design?.Id == design.Id) App.Window.SetDesign(design.Create());
            log.Check(await App.SwitchDesignAsync(design.Id) && App.Design?.Id == design.Id, $"{design.Id}: switched to it");
            var shell = App.Design!;
            log.Note($"{design.Id}: created");
            await Task.Delay(300);
            log.Note($"{design.Id}: laid out");
            try
            {
                await shell.SelfTestAsync(log, camera);
            }
            catch (Exception e)
            {
                log.Fail($"{design.Id}: {e}");
            }
            log.Note($"{design.Id}: settings");
            shell.ShowSettings();
            await Task.Delay(200);
            log.Check(shell.SettingsOpen, $"{design.Id}: settings open");
            shell.CloseSettings();

            // Opened from Explorer.
            log.Note($"{design.Id}: open a file from Explorer");
            shell.OpenFile(big);
            log.Check(await UiTestLog.WaitAsync(() => string.Equals(shell.ShownPhoto, big, StringComparison.OrdinalIgnoreCase)),
                      $"{design.Id}: opening a file from Explorer shows that photo");
            await Task.Delay(500);
            if (shell is Main.MainDesign main) await ViewerTest.RunAsync(log, main, photos, big);
            log.Note($"{design.Id}: done");
            log.Check(await shell.CanCloseAsync(), $"{design.Id}: nothing left unsaved");
        }
        log.Check(await App.SwitchDesignAsync("main"), "back to the main design");
        try
        {
            // The cold-start check reuses this generated library in a new
            // process. Its caller can retain the synthetic photo fixture.
            if (Environment.GetEnvironmentVariable("SPHOTO_KEEP_UI_FIXTURE") != "1")
                Directory.Delete(root, recursive: true);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// The same edits in every design's editor, through its session, with
    /// the preview expected to keep up.
    /// </summary>
    internal static async Task ExerciseEditorAsync(UiTestLog log, string design, Func<EditSession?> session,
                                                   PhotoPreview preview, Func<Task> save, Action<bool> showCrop)
    {
        log.Check(await UiTestLog.WaitAsync(() => session()?.IsLoaded == true), $"{design}: the editor opens the photo");
        var s = session();
        if (s is null || !s.IsLoaded) return;
        log.Check(await UiTestLog.WaitAsync(() => preview.FramesShown > 0), $"{design}: the editor shows a preview");

        // A slider drag: many changes, each returning at once.
        int frames = preview.FramesShown;
        int exposure = Adjustment.ByKey("exposure").Index;
        var watch = Stopwatch.StartNew();
        double slowest = 0;
        for (int i = 1; i <= 30; i++)
        {
            var t = watch.Elapsed.TotalMilliseconds;
            s.Apply(s.State.WithValue(exposure, i / 30f), continuous: true);
            slowest = Math.Max(slowest, watch.Elapsed.TotalMilliseconds - t);
            await Task.Delay(16);
        }
        log.Note($"{design}: slowest slider step on the UI thread {slowest:F1} ms");
        log.Check(slowest < 50, $"{design}: slider steps do not block the UI thread");
        log.Check(await UiTestLog.WaitAsync(() => preview.FramesShown > frames), $"{design}: the preview follows the slider");
        log.Check(s.IsDirty, $"{design}: the edit counts as unsaved");

        frames = preview.FramesShown;
        s.Apply(s.State.WithFilter("film", 1f));
        log.Check(await UiTestLog.WaitAsync(() => preview.FramesShown > frames), $"{design}: choosing a filter updates the preview");

        var (w, h) = s.OutputSize(s.State.Geometry);
        s.Turn(clockwise: true);
        var (w2, h2) = s.OutputSize(s.State.Geometry);
        log.Check(w2 == h && h2 == w, $"{design}: rotating turns the photo");
        showCrop(true);
        await Task.Delay(200);
        log.Check(preview.CropMode, $"{design}: the crop tool shows the crop frame");
        showCrop(false);
        await Task.Delay(200);
        log.Check(!preview.CropMode, $"{design}: leaving the crop tool hides the crop frame");
        s.Undo();
        log.Check(s.State.Geometry.QuarterTurns == 0, $"{design}: undo takes the rotation back");
        s.Redo();
        log.Check(s.State.Geometry.QuarterTurns == 1, $"{design}: redo puts it back");

        await save();
        log.Check(s.LastSaved is { } copy && File.Exists(copy) && !s.IsDirty, $"{design}: saving writes a copy");
    }
}

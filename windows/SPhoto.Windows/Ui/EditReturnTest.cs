using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SPhoto.Services;
using SPhoto.Ui.Main;
using Windows.Foundation;
using Windows.Graphics.Imaging;

namespace SPhoto.Ui;

/// <summary>
/// Observes the first window while an unchanged edit returns to its viewer.
/// Frame measurements and stage pixels are collected before writing them,
/// so synchronous log and PNG I/O do not interrupt the transition itself.
/// </summary>
internal static class EditReturnTest
{
    private sealed record StagePixels(string Name, double StartedMs, double FinishedMs,
                                      int Width, int Height, double Dpi, byte[] Pixels);

    internal static async Task<int> RunAsync(string logPath)
    {
        logPath = Path.GetFullPath(logPath);
        var dir = Path.GetDirectoryName(logPath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(logPath, "");
        var log = new UiTestLog { FilePath = logPath };
        log.Note($"edit-return diagnostic: Motion.Enabled={Motion.Enabled}");
        try
        {
            if (App.Design is not MainDesign design)
            {
                log.Fail("the first window contains MainDesign");
            }
            else
            {
                log.Check(await UiTestLog.WaitAsync(() => design.XamlRoot is not null && design.ActualWidth > 0),
                          "the first window is laid out");
                var photos = Path.Combine(dir, "photos");
                Directory.CreateDirectory(photos);
                // Keep an earlier sibling present before opening the first
                // requested photo: populating the filmstrip must not select it.
                await WriteGrayFixtureAsync(dir);
                foreach (var (width, height) in new[] { (2400, 1600), (6000, 4000) })
                {
                    var name = $"unchanged-{width}x{height}";
                    var path = Path.Combine(photos, name + ".jpg");
                    await ImageIO.EncodeAsync(SelfTest.Quadrants(width, height), path, null);
                    await RunScenarioAsync(log, design, path, name, dir);
                }
                var first = Path.Combine(photos, "unchanged-2400x1600.jpg");
                await RunScenarioAsync(log, design, first, "after-navigation", dir, navigate: true);
                await RunScenarioAsync(log, design, first, "quick-cancel", dir, quick: true);
                await WriteGrayFixtureAsync(dir);
                await RunScenarioAsync(log, design, Path.Combine(photos, "dark-gray.png"),
                                       "gray-cancel", dir, checkGrayRoi: true);
            }
        }
        catch (Exception e)
        {
            log.Fail($"unexpected edit-return diagnostic error: {e}");
        }
        log.Write(log.Failures == 0 ? "PASSED" : $"FAILED ({log.Failures})");
        return log.Failures == 0 ? 0 : 1;
    }

    private static async Task RunScenarioAsync(UiTestLog log, MainDesign design, string path,
                                              string name, string outputDir, bool navigate = false, bool quick = false,
                                              bool checkGrayRoi = false)
    {
        var viewer = design.Viewer;
        var editor = design.Editor;
        log.Note($"--- {name}; Motion.Enabled={Motion.Enabled}; scale={design.XamlRoot?.RasterizationScale}");
        design.OpenFile(path);
        bool opened = await UiTestLog.WaitAsync(() =>
            viewer.Photo.IsShowingPhoto && viewer.Photo.PictureForTest.Source is not null &&
            string.Equals(viewer.Photo.Current?.Path, path, StringComparison.OrdinalIgnoreCase));
        log.Check(opened, $"{name}: the generated photo is loaded");
        if (!opened) return;
        await Task.Delay(500);
        log.Check(string.Equals(viewer.Photo.Current?.Path, path, StringComparison.OrdinalIgnoreCase),
                  $"{name}: populating the folder filmstrip keeps the requested photo");
        if (navigate)
        {
            viewer.Photo.Go(1);
            await Task.Delay(500);
            path = viewer.Photo.Current!.Path;
        }
        viewer.EditCurrent();
        bool previewReady = await UiTestLog.WaitAsync(() => editor.Session?.IsLoaded == true && editor.Preview.HasFrame);
        log.Check(previewReady, $"{name}: an unchanged editing session has a preview");
        if (!previewReady) return;
        if (!quick) await Task.Delay(500);
        log.Check(editor.Session?.IsDirty == false, $"{name}: the session has no changes");

        var samples = new List<string>();
        var pictures = new List<StagePixels>();
        var clock = Stopwatch.StartNew();
        pictures.Add(await CaptureAsync(viewer.StageForTest, name + "-before", clock));
        clock.Restart();
        int frameCount = 0;
        double previousWidth = viewer.Photo.PictureForTest.ActualWidth;
        double largestBackwardStep = 0;
        Exception? measurementError = null;
        void OnFrame(object? sender, object e)
        {
            try
            {
                if (frameCount >= 600) return;
                samples.Add(DescribeFrame(name, ++frameCount, "rendering", clock.Elapsed.TotalMilliseconds, design));
                var image = viewer.Photo.PictureForTest;
                double width = image.TransformToVisual(viewer).TransformBounds(new Rect(0, 0, image.ActualWidth, image.ActualHeight)).Width;
                if (editor.Visibility == Visibility.Collapsed)
                    largestBackwardStep = Math.Max(largestBackwardStep, previousWidth - width);
                previousWidth = width;
            }
            catch (Exception error)
            {
                measurementError ??= error;
            }
        }

        // Subscription and cleanup stay on the window's UI thread.
        CompositionTarget.Rendering += OnFrame;
        try
        {
            samples.Add(DescribeFrame(name, 0, "cancel-request", clock.Elapsed.TotalMilliseconds, design));
            var cancel = editor.CancelAsync();
            var capture = CaptureBurstAsync(viewer.StageForTest, name, clock, pictures);
            await cancel.WaitAsync(TimeSpan.FromSeconds(20));
            samples.Add(DescribeFrame(name, frameCount, "cancel-complete", clock.Elapsed.TotalMilliseconds, design));
            await capture;
            await Task.Delay(500);
            samples.Add(DescribeFrame(name, frameCount, "settled", clock.Elapsed.TotalMilliseconds, design));
            pictures.Add(await CaptureAsync(viewer.StageForTest, name + "-after", clock));
        }
        finally
        {
            CompositionTarget.Rendering -= OnFrame;
            foreach (var sample in samples) log.Write(sample);
            log.Note($"{name}: observed {frameCount} rendering events over {clock.Elapsed.TotalMilliseconds:F1} ms");
        }

        if (measurementError is not null) log.Fail($"{name}: frame measurement failed: {measurementError}");
        log.Check(largestBackwardStep <= 3, $"{name}: the photo grows continuously during return (largest backward step {largestBackwardStep:F2} DIP)");
        log.Check(viewer.Photo.IsShowingPhoto && viewer.Photo.PictureForTest.Source is not null &&
                  string.Equals(viewer.Photo.Current?.Path, path, StringComparison.OrdinalIgnoreCase) &&
                  editor.Visibility == Visibility.Collapsed && editor.Stage.Visibility == Visibility.Collapsed,
                  $"{name}: cancelling returns to the original loaded photo");
        // Pixel ratios are observations, not a fixed-frame or fade-duration assertion.
        // The PNGs preserve evidence when the two layers look available in XAML
        // but an opaque stage, a resize or a composition update paints a flash.
        foreach (var picture in pictures) await SaveAsync(log, outputDir, picture);
        if (checkGrayRoi) CheckGrayReturn(log, name, pictures);
    }

    private static void CheckGrayReturn(UiTestLog log, string name, IReadOnlyList<StagePixels> pictures)
    {
        double Mean(StagePixels picture)
        {
            var (red, green, blue, pixels) = CenterRgb(picture);
            double mean = (red + green + blue) / 3;
            log.Write(FormattableString.Invariant($"gray-cancel-roi {picture.Name} tStart={picture.StartedMs:F3} tEnd={picture.FinishedMs:F3} pixels={pixels} meanR={red:F4} meanG={green:F4} meanB={blue:F4} meanRgb={mean:F4}"));
            return mean;
        }
        log.Check(pictures.Count > 2, $"{name}: actual CancelAsync produced return samples");
        if (pictures.Count <= 2) return;
        double before = Mean(pictures[0]), after = Mean(pictures[pictures.Count - 1]);
        double peak = double.MinValue;
        for (int i = 1; i < pictures.Count - 1; i++) peak = Math.Max(peak, Mean(pictures[i]));
        double allowed = Math.Max(before, after) + 3;
        log.Check(peak <= allowed,
                  FormattableString.Invariant($"{name}: actual CancelAsync does not wash the image toward white (before={before:F4}, returnMax={peak:F4}, after={after:F4}, allowed={allowed:F4})"));
    }

    private static async Task WriteGrayFixtureAsync(string outputDir)
    {
        const int width = 2400, height = 1600;
        var bytes = new byte[width * height * 4];
        for (int at = 0; at < bytes.Length; at += 4)
        {
            bytes[at] = bytes[at + 1] = bytes[at + 2] = 24;
            bytes[at + 3] = 255;
        }
        var path = Path.Combine(outputDir, "photos", "dark-gray.png");
        await ImageIO.EncodeAsync(new Pixels(bytes, width, height), path, null);
    }

    private static (double Red, double Green, double Blue, int Pixels) CenterRgb(StagePixels frame)
    {
        int roiWidth = Math.Min(64, frame.Width), roiHeight = Math.Min(64, frame.Height);
        int left = (frame.Width - roiWidth) / 2, top = (frame.Height - roiHeight) / 2;
        long red = 0, green = 0, blue = 0;
        int pixels = 0;
        for (int y = top; y < top + roiHeight; y++)
        {
            for (int x = left; x < left + roiWidth; x++)
            {
                int at = (y * frame.Width + x) * 4;
                blue += frame.Pixels[at];
                green += frame.Pixels[at + 1];
                red += frame.Pixels[at + 2];
                pixels++;
            }
        }
        if (pixels == 0) throw new InvalidOperationException("Return capture produced an empty stage bitmap.");
        return ((double)red / pixels, (double)green / pixels, (double)blue / pixels, pixels);
    }

    private static async Task CaptureBurstAsync(Grid stage, string name, Stopwatch clock,
                                                List<StagePixels> pictures)
    {
        // RenderAsync/GetPixelsAsync are sequential. No 400 ms screenshot
        // settling delay and no PNG encoder run during the measured return.
        for (int i = 0; i < 7 && clock.ElapsedMilliseconds < 600; i++)
        {
            pictures.Add(await CaptureAsync(stage, $"{name}-return-{i:00}", clock));
            await Task.Delay(10);
        }
    }

    private static string DescribeFrame(string name, int number, string point, double milliseconds,
                                        MainDesign design)
    {
        var viewer = design.Viewer;
        var editor = design.Editor;
        var photo = viewer.Photo;
        var picture = photo.PictureForTest;
        var preview = editor.Preview;
        var previewPicture = preview.PreviewPictureForTest;
        return FormattableString.Invariant($"frame {name} n={number} t={milliseconds:F3} point={point} editorStage={editor.Stage.Visibility} editorStageOpacity={editor.Stage.Opacity:F5} editorPane={editor.Visibility} editorPaneWidth={editor.ActualWidth:F3} photo={photo.Visibility} photoOpacity={photo.Opacity:F5} photoWidth={photo.ActualWidth:F3} photoHeight={photo.ActualHeight:F3} pictureSource={picture.Source is not null} pictureSourceType={picture.Source?.GetType().Name ?? "none"} picture={picture.Visibility} pictureOpacity={picture.Opacity:F5} pictureWidth={picture.ActualWidth:F3} pictureHeight={picture.ActualHeight:F3} pictureDeclaredWidth={picture.Width:F3} pictureDeclaredHeight={picture.Height:F3} pictureTransform={Transform(picture)} pictureBounds={Bounds(picture, viewer)} previewWidth={preview.ActualWidth:F3} previewHeight={preview.ActualHeight:F3} previewHasFrame={preview.HasFrame} previewPictureSource={previewPicture.Source is not null} previewPictureOpacity={previewPicture.Opacity:F5} previewPictureWidth={previewPicture.ActualWidth:F3} previewPictureHeight={previewPicture.ActualHeight:F3} previewBounds={Bounds(previewPicture, viewer)} viewerStageWidth={viewer.StageForTest.ActualWidth:F3} viewerStageHeight={viewer.StageForTest.ActualHeight:F3}");
    }

    private static string Transform(FrameworkElement element) => element.RenderTransform is CompositeTransform transform
        ? FormattableString.Invariant($"(scaleX={transform.ScaleX:F5},scaleY={transform.ScaleY:F5},translateX={transform.TranslateX:F3},translateY={transform.TranslateY:F3})")
        : element.RenderTransform?.GetType().Name ?? "none";

    private static string Bounds(FrameworkElement element, UIElement ancestor)
    {
        try
        {
            Rect bounds = element.TransformToVisual(ancestor).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            return FormattableString.Invariant($"({bounds.X:F3},{bounds.Y:F3},{bounds.Width:F3},{bounds.Height:F3})");
        }
        catch (Exception e)
        {
            return $"unavailable({e.GetType().Name})";
        }
    }

    private static async Task<StagePixels> CaptureAsync(Grid stage, string name, Stopwatch clock)
    {
        double started = clock.Elapsed.TotalMilliseconds;
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(stage);
        var pixels = (await bitmap.GetPixelsAsync()).ToArray();
        return new StagePixels(name, started, clock.Elapsed.TotalMilliseconds, bitmap.PixelWidth, bitmap.PixelHeight,
                               (stage.XamlRoot?.RasterizationScale ?? 1) * 96, pixels);
    }

    private static async Task SaveAsync(UiTestLog log, string outputDir, StagePixels frame)
    {
        var dir = Path.Combine(outputDir, "edit-return-shots");
        Directory.CreateDirectory(dir);
        using var stream = new FileStream(Path.Combine(dir, frame.Name + ".png"), FileMode.Create, FileAccess.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream.AsRandomAccessStream());
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                             (uint)frame.Width, (uint)frame.Height, frame.Dpi, frame.Dpi, frame.Pixels);
        await encoder.FlushAsync();
        int sampled = 0, white = 0, colored = 0;
        for (int at = 0; at + 3 < frame.Pixels.Length; at += 64)
        {
            if (frame.Pixels[at + 3] < 240) continue;
            int b = frame.Pixels[at], g = frame.Pixels[at + 1], r = frame.Pixels[at + 2];
            sampled++;
            if (r >= 247 && g >= 247 && b >= 247) white++;
            if (Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) > 40) colored++;
        }
        log.Write(FormattableString.Invariant($"pixels {frame.Name} tStart={frame.StartedMs:F3} tEnd={frame.FinishedMs:F3} size={frame.Width}x{frame.Height} sampled={sampled} whiteRatio={(sampled == 0 ? 0 : (double)white / sampled):F6} coloredRatio={(sampled == 0 ? 0 : (double)colored / sampled):F6}"));
    }
}

using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;
using SPhoto.Services;
using SPhoto.Ui.Main;

namespace SPhoto.Ui;

/// <summary>
/// A fresh process using an isolated cold or prepared fixture. Unlike UiTest,
/// this never recreates the original design or refreshes it before checking.
/// Run with SPHOTO_DATA_DIR pointing at isolated test data.
/// </summary>
internal static class StartupTest
{
    internal static async Task<int> RunAsync(string path)
    {
        File.WriteAllText(path, "");
        var log = new UiTestLog { FilePath = path };
        try
        {
            if (App.Design is not MainDesign design)
            {
                log.Fail("startup fixture requires the main design");
                return 1;
            }
            log.Check(await UiTestLog.WaitAsync(() => design.Grid.Entries.Count > 0),
                "the original first window lists the library automatically");
            bool warm = Environment.GetEnvironmentVariable("SPHOTO_EXPECT_PREPARED") == "1";
            // Initial organization may take time; prepared libraries must show
            // immediately without waiting for any original-image generation.
            bool shown = await UiTestLog.WaitAsync(() => design.Grid.TilesOnScreen() is { Tiles: > 0, Blank: 0 }, warm ? 10000 : 180000);
            int firstScreenTiles = design.Grid.TilesOnScreen().Tiles;
            log.Note(design.Grid.StartupState() + $", loader {ThumbnailLoader.Stats}");
            log.Check(shown, "first-screen thumbnails appear without navigation or refresh");
            log.Note($"process start to first decoded viewport: {(DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalMilliseconds:F0} ms");
            log.Check(App.Tray is { Visible: true }, "the system tray status icon was registered");
            App.Tray?.Update();
            log.Note("tray status: " + App.Tray?.Summary);
            log.Check(await UiTestLog.WaitAsync(() => !design.IsBusy, 30000), "the background folder scan completes");
            await App.Library.Thumbnails.Completion.WaitAsync(TimeSpan.FromMinutes(10));
            var prepared = App.Library.Thumbnails.Progress;
            log.Check(!prepared.Running && prepared.Completed == prepared.Total && prepared.Failed == 0,
                $"all {prepared.Total} files have persisted thumbnails, including unvisited photos");
            log.Note($"preparation: {prepared}, loader {ThumbnailLoader.Stats}");
            if (Environment.GetEnvironmentVariable("SPHOTO_EXPECT_PREPARED") == "1")
                log.Check(ThumbnailLoader.Stats.Misses == 0, "a prepared launch generates no thumbnails");
            App.Tray?.Update();
            log.Check(await UiTestLog.WaitAsync(() => !App.Library.IsOrganizing, 120000), "background library organization finishes");
            App.Tray?.Update();
            log.Check(App.Tray?.Summary == "照片已就绪", "the tray reports readiness after organization");
            await Screenshots.SaveAsync(log, design.XamlRoot?.Content, "startup");
            if (shown && design.Grid.Entries.Count > 50)
            {
                var entries = design.Grid.Entries;
                foreach (int index in new[] { entries.Count - 1, entries.Count / 2, 0 })
                {
                    design.Grid.ScrollTo(entries[index].Path);
                    await CheckTargetAsync(log, design, entries[index].Path, $"scroll to item {index}");
                }

                // Overlap successive viewports so a loaded edge or middle
                // sample cannot hide missing thumbnails elsewhere in the library.
                int stride = Math.Max(1, (int)(firstScreenTiles * 0.8));
                int checkedPositions = 0;
                bool walkedAll = false;
                for (int index = 0; ; index = Math.Min(index + stride, entries.Count - 1))
                {
                    design.Grid.ScrollTo(entries[index].Path);
                    if (!await CheckTargetAsync(log, design, entries[index].Path, $"full-gallery scroll to item {index}")) break;
                    checkedPositions++;
                    if (index == entries.Count - 1)
                    {
                        walkedAll = true;
                        break;
                    }
                }
                log.Note($"full-gallery walk: {entries.Count} items, stride {stride}, {checkedPositions} checked positions, completed {walkedAll}");

                // Queue changes faster than a cold thumbnail can normally load.
                // Only the final requested position may satisfy this check.
                int finalIndex = entries.Count * 2 / 3;
                foreach (int index in new[] { entries.Count / 3, entries.Count - 1, entries.Count / 4, 0, finalIndex })
                {
                    design.Grid.ScrollTo(entries[index].Path);
                    await Task.Delay(40);
                }
                await CheckTargetAsync(log, design, entries[finalIndex].Path, $"rapid scroll ends at item {finalIndex}");

                // Exercise the reported sidebar workaround in this same window,
                // then prove a later recycled viewport also displays its images.
                design.Go("tags");
                await Task.Delay(100);
                design.Go("all");
                log.Check(await UiTestLog.WaitAsync(() => design.Grid.TilesOnScreen() is { Tiles: > 0, Blank: 0 }, 10000),
                    "sidebar round trip returns to a loaded photo viewport");
                int returnedIndex = entries.Count / 2;
                design.Grid.ScrollTo(entries[returnedIndex].Path);
                await CheckTargetAsync(log, design, entries[returnedIndex].Path, $"after sidebar return: scroll to item {returnedIndex}");
            }
            if (!shown)
            {
                // Check the reported workaround only after preserving the failure.
                design.Go("tags");
                design.Go("all");
                log.Check(await UiTestLog.WaitAsync(() => design.Grid.TilesOnScreen() is { Tiles: > 0, Blank: 0 }),
                    "sidebar navigation restores the thumbnails");
                log.Note(design.Grid.StartupState() + $", loader {ThumbnailLoader.Stats}");
                await Screenshots.SaveAsync(log, design.XamlRoot?.Content, "after-navigation");
            }
        }
        catch (Exception e)
        {
            log.Fail($"unexpected: {e}");
        }
        if (Environment.GetEnvironmentVariable("SPHOTO_EXPECT_PREPARED") == "1")
            log.Check(ThumbnailLoader.Stats.Misses == 0, "prepared scrolling and sidebar navigation generate no thumbnails");
        App.Tray?.Dispose();
        log.Write(log.Failures == 0 ? "PASSED" : $"FAILED ({log.Failures})");
        return log.Failures == 0 ? 0 : 1;
    }

    private static async Task<bool> CheckTargetAsync(UiTestLog log, MainDesign design, string path, string step)
    {
        var grid = design.Grid;
        bool reached = await UiTestLog.WaitAsync(() => grid.IsOnScreen(path), 10000);
        log.Check(reached, step + ": the requested photo enters the viewport");
        bool decoded = reached && await UiTestLog.WaitAsync(() =>
            grid.IsOnScreen(path) && grid.ImageOf(path)?.Source is BitmapImage { PixelWidth: > 0, PixelHeight: > 0 }, 10000);
        log.Check(decoded, step + ": the requested photo has a decoded thumbnail");
        bool complete = decoded && await UiTestLog.WaitAsync(() =>
            grid.IsOnScreen(path) && grid.TilesOnScreen() is { Tiles: > 0, Blank: 0 }, 10000);
        log.Check(complete, step + ": every visible tile has a decoded thumbnail");
        log.Note(grid.StartupState() + $", loader {ThumbnailLoader.Stats}");
        return complete;
    }
}

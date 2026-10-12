using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using SPhoto.Editing;

namespace SPhoto.Ui.Main;

/// <summary>The UI test's walk through the main design (see UiTest).</summary>
internal static class MainSelfTest
{
    internal static async Task RunAsync(MainDesign d, UiTestLog log, string photo)
    {
        var root = Path.GetDirectoryName(Path.GetDirectoryName(photo))!;
        var burst = Path.Combine(root, "连拍", "DSC_0101.jpg");
        var copy = Path.Combine(root, "旅行", "IMG_0002.jpg");

        // ----- 图库: everything, newest first.
        // No navigation or manual refresh: neither may repair the first
        // screen before this regression check has seen it.
        log.Check(await UiTestLog.WaitAsync(() => !d.IsBusy && d.Grid.Entries.Count > 0), "main: first launch automatically lists the test photos");
        log.Note($"main: {d.Grid.Entries.Count} tiles in 所有照片");
        log.Check(await UiTestLog.WaitAsync(() => d.Grid.TilesOnScreen() is { Tiles: > 0, Blank: 0 }, 10000),
            $"main: first launch shows thumbnails without navigation ({d.Grid.TilesOnScreen()}, loader {SPhoto.Services.ThumbnailLoader.Stats})");

        // Fingerprints and the look of each photo arrive in the background.
        bool indexed = await UiTestLog.WaitAsync(() =>
            d.Model?.ShotOf(burst) is { Stack.Count: 2 } && d.Model.CopiesOf(copy).Count == 2, 90000);
        if (!indexed) await d.ReloadModelAsync();
        log.Check(d.Model?.CopiesOf(copy).Count == 2, "main: identical files are one photo with two copies");
        log.Check(d.Model?.ShotOf(burst) is { Stack.Count: 2 }, "main: a burst of near-identical shots stacks");
        int library = d.Grid.Entries.Count;
        log.Check(await UiTestLog.WaitAsync(() => d.Grid.TilesOnScreen() is { Tiles: > 0, Blank: 0 }, 10000),
            $"main: every tile on screen has its thumbnail ({d.Grid.BlankTilesOnScreen()} blank, loader {SPhoto.Services.ThumbnailLoader.Stats})");
        log.Note($"main: tiles healed after list changes: {d.Grid.Healed}");
        await Screenshots.SaveAsync(log, d.XamlRoot?.Content, "1-all-photos");

        // ----- 文件夹: every file as it is.
        d.Go("folder:" + root);
        log.Check(d.Grid.Entries.Count == library + 2, $"main: the folder shows every file ({d.Grid.Entries.Count}, library {library})");
        d.Go("all");

        // The folders open upwards from 文件夹; nothing else in the sidebar moves.
        var before = d.SidebarRowsForTest();
        d.ToggleFoldersForTest();
        await Task.Delay(400);
        var open = d.SidebarRowsForTest();
        log.Check(d.FoldersOpen && Math.Abs(open.All - before.All) < 1 && Math.Abs(open.Settings - before.Settings) < 1,
            $"main: opening the folders leaves the sidebar in place (top {before.All}->{open.All}, 设置 {before.Settings}->{open.Settings})");
        log.Check(d.FirstFolderTopForTest < open.Folders, $"main: the folders open above 文件夹 ({d.FirstFolderTopForTest} < {open.Folders})");
        await Screenshots.SaveAsync(log, d.XamlRoot?.Content, "1b-folders-open");
        d.ToggleFoldersForTest();
        await Task.Delay(400);
        d.ToggleFoldersForTest();
        await Task.Delay(200);
        log.Check(d.FoldersOpen, "main: the folders open again after closing");
        d.ToggleFoldersForTest();
        await Task.Delay(200);

        var key = d.Model!.KeyOf(photo);
        log.Check(key is not null, "main: the photo has a fingerprint");

        // ----- Viewer: beside the sidebar, chrome fades, info and tags.
        var viewer = d.Viewer;
        d.OpenViewer(d.Grid.Entries.First(p => string.Equals(p.Path, photo, StringComparison.OrdinalIgnoreCase)));
        log.Check(await UiTestLog.WaitAsync(() => viewer.Photo.IsShowingPhoto), "main: a photo opens in the viewer");
        log.Check(viewer.TransformToVisual(null).TransformPoint(default).Y >= 32, "main: the viewer starts under the window's buttons");
        log.Check(d.PaneShown && viewer.TransformToVisual(null).TransformPoint(default).X >= 200, "main: the sidebar stays beside the viewer");
        d.SetPaneShown(false);
        await Task.Delay(400);
        // NavigationView keeps a 1px edge on its content.
        log.Check(!d.PaneShown && viewer.TransformToVisual(null).TransformPoint(default).X < 8, "main: the sidebar folds away");
        await Screenshots.SaveAsync(log, d.XamlRoot?.Content, "8-viewer-sidebar-folded");
        d.SetPaneShown(true);
        await Task.Delay(400);
        // The sidebar wins over the photo: a click there closes the viewer.
        await d.ClickSidebarForTest("tags");
        log.Check(viewer.Visibility == Visibility.Collapsed && d.PageTitle == "标签", "main: a sidebar click while viewing goes to that page");
        d.Go("all");
        d.OpenViewer(d.Grid.Entries.First(p => string.Equals(p.Path, photo, StringComparison.OrdinalIgnoreCase)));
        log.Check(await UiTestLog.WaitAsync(() => viewer.Photo.IsShowingPhoto), "main: the photo opens again from the grid");
        viewer.HideChrome();
        log.Check(!viewer.ChromeShown, "main: the controls fade away");
        viewer.ShowChrome();
        log.Check(viewer.ChromeShown, "main: moving the mouse brings them back");
        viewer.ShowInfoForTest(true);
        log.Check(viewer.InfoShown, "main: 信息 opens the information pane");
        await Screenshots.SaveAsync(log, d.XamlRoot?.Content, "4-viewer-info");
        await viewer.Tags.AddAsync("测试标签");
        log.Check(viewer.Tags.Count == 1, "main: a tag shows on the photo");
        await d.ReloadModelAsync();
        log.Check(d.Model?.Tags.Any(t => t.Name == "测试标签") == true, "main: the tag is in the library's tag list");
        if (d.Model?.KeyOf(copy) is { } otherKey && otherKey != key)
        {
            App.Library.Index.AddTag(otherKey, "旅途", SPhoto.Library.TagSource.Manual);
            log.Check(viewer.Tags.SuggestForTest("旅") == 1, "main: typing a tag suggests the library's matching tags");
            log.Check(viewer.Tags.SuggestForTest("") >= 1 && viewer.Tags.SuggestForTest("测试") == 0,
                "main: the plus lists the library's tags, without the photo's own");
            App.Library.Index.RemoveTag(otherKey, "旅途");
        }
        viewer.ShowInfoForTest(false);
        viewer.Close();

        // ----- Pages: a set of tags, one card per tag or all the photos.
        long pageId = d.Model!.Index.CreatePage("测试页面", new[] { "测试标签" });
        await d.ReloadModelAsync();
        d.Go("page:" + pageId);
        log.Check(d.PageTitle == "测试页面" && d.CardsShown && d.CardCount == 1, "main: a page shows a card for each of its tags");
        await d.SetCardSizeForTest(pageId, "测试标签", SPhoto.Library.CardSize.Large);
        log.Check(d.CardCount == 1 && d.FirstCardColumnsForTest == 2, "main: a tag's card on a page can be made large");
        await Screenshots.SaveAsync(log, d.XamlRoot?.Content, "2-page-by-tag");
        d.ShowPageAllForTest(true);
        log.Check(!d.CardsShown && d.Grid.Entries.Count == 1, "main: the page can show all its photos instead");
        d.ShowPageAllForTest(false);
        d.Go("tag:测试标签");
        log.Check(d.PageTitle == "测试标签" && d.Grid.Entries.Count == 1, "main: a tag lists its photos, its name without #");
        log.Check(File.Exists(photo), "main: tags and pages leave the file where it is");
        // 添加照片 inside a tag: pick from 所有照片, back in the tag with it.
        d.StartPickingForTest();
        log.Check(d.Picking && d.Grid.Selecting && d.Grid.Entries.Count > 1, "main: 添加照片 in a tag picks from all photos");
        var picked = d.Model?.ShotOf(copy)?.Cover.Path ?? copy;
        d.Grid.SelectPath(picked);
        await d.FinishPickingForTest();
        log.Check(!d.Picking && d.PageTitle == "测试标签" && d.Grid.Entries.Count == 2, "main: picked photos join the tag, and the tag shows again");
        if (d.Model?.KeyOf(picked) is { } pickedKey)
        {
            App.Library.Index.RemoveTag(pickedKey, "测试标签");
            await d.ReloadModelAsync();
        }
        d.Go("tags");
        log.Check(d.CardsShown && d.CardCount >= 1, "main: 标签 shows a card per tag");
        await Screenshots.SaveAsync(log, d.XamlRoot?.Content, "3-tags");
        d.Go("all");
        d.OpenViewer(d.Grid.Entries.First(p => string.Equals(p.Path, photo, StringComparison.OrdinalIgnoreCase)));
        log.Check(await UiTestLog.WaitAsync(() => viewer.Photo.IsShowingPhoto), "main: the photo opens again");
        viewer.ShowChrome();
        await Screenshots.SaveAsync(log, d.XamlRoot?.Content, "5-viewer");
        // The test photo is the oldest, last in the list: step back and forward.
        int step = viewer.Photo.Index > 0 ? -1 : 1;
        viewer.Photo.Go(step);
        await Task.Delay(300);
        viewer.Photo.Go(-step);
        log.Check(await UiTestLog.WaitAsync(() => string.Equals(viewer.Photo.Current?.Path, photo, StringComparison.OrdinalIgnoreCase) && viewer.Photo.IsShowingPhoto),
                  "main: next and previous move through the page");

        // ----- Editor.
        viewer.EditCurrent();
        var editor = d.Editor;
        log.Check(editor.Visibility == Visibility.Visible && editor.Stage.Visibility == Visibility.Visible, "main: Edit opens the editing pane beside the photo");
        await Screenshots.SaveAsync(log, d.XamlRoot?.Content, "6-editor-filters");
        await UiTest.ExerciseEditorAsync(log, "main", () => editor.Session, editor.Preview,
            async () => await editor.SaveAsync(close: false), crop => editor.ShowCropForTest(crop));
        var saved = editor.Session?.LastSaved;
        editor.OpenToolForTest("exposure");
        log.Check(editor.SliderForTest("exposure") is { Slider.Maximum: 10 }, "main: 调整 lists stepped sliders");
        await Screenshots.SaveAsync(log, d.XamlRoot?.Content, "7-editor-adjust");
        int frames = editor.Preview.FramesShown;
        editor.OpenToolForTest("curves");
        log.Check(editor.ToolContent is CurvePanel, "main: the curves tool opens");
        (editor.ToolContent as CurvePanel)?.BrightenForTest();
        log.Check(await UiTestLog.WaitAsync(() => editor.Preview.FramesShown > frames), "main: a curve updates the preview");
        editor.CloseTool(accept: false);
        editor.OpenToolForTest("hsl");
        log.Check(editor.ToolContent is HslPanel, "main: the HSL tool opens");
        editor.CloseTool(accept: false);
        log.Check(editor.Session?.State.Curves.All(c => c.Length == 0) == true, "main: ✕ takes the tool's change back");
        var closing = editor.CancelAsync();
        log.Check(editor.Stage.Visibility != Visibility.Visible || editor.Preview.HasFrame,
            "main: the editor keeps its rendered image during the return to the saved version");
        await closing;
        log.Check(await UiTestLog.WaitAsync(() => editor.Visibility == Visibility.Collapsed), "main: the editor closes");
        log.Check(saved is not null && await UiTestLog.WaitAsync(() => string.Equals(viewer.Photo.Current?.Path, saved, StringComparison.OrdinalIgnoreCase)),
                  "main: the viewer shows the saved version");
        log.Check(viewer.Photo.IsShowingPhoto && editor.Stage.Visibility == Visibility.Collapsed,
            "main: the saved image is loaded before the editor preview is removed");
        viewer.EditCurrent();
        await editor.CancelAsync();
        log.Check(editor.Visibility == Visibility.Collapsed && viewer.ControlsVisible && viewer.ChromeShown && viewer.Photo.IsShowingPhoto,
            "main: immediately cancelling an edit keeps the viewer controls and photo visible");
        viewer.EditCurrent();
        log.Check(await UiTestLog.WaitAsync(() => editor.Session?.IsLoaded == true && editor.Preview.HasFrame),
            "main: the saved version can be edited again");
        var unchanged = viewer.Photo.Current?.Path;
        await editor.CancelAsync();
        log.Check(viewer.Photo.IsShowingPhoto && viewer.Photo.Current?.Path == unchanged && editor.Stage.Visibility == Visibility.Collapsed,
            "main: cancelling an unchanged edit returns to the same visible photo");
        await CheckConcurrentSaveAsync(d, log, photo);
        bool folded = await UiTestLog.WaitAsync(() => d.Model?.ShotOf(photo) is { Versions.Count: > 0 }, 60000);
        log.Check(folded, "main: the saved version folds under its original");
        if (!folded && saved is not null)
        {
            var catalog = App.Library.Catalog();
            catalog.ByPath.TryGetValue(saved, out var version);
            catalog.ByPath.TryGetValue(photo, out var original);
            log.Note($"main: version {saved} -> {version?.Fingerprint ?? "not in catalog"}, original {original?.Fingerprint ?? "not in catalog"}, " +
                     $"original of version {(version is null ? "-" : catalog.OriginalOf(version.Fingerprint)?.Path ?? "none")}, " +
                     $"shot of photo: {d.Model?.ShotOf(photo)?.Key ?? "none"} with {d.Model?.ShotOf(photo)?.Versions.Count} versions, " +
                     $"catalog paths: {string.Join(" | ", catalog.ByPath.Keys)}");
        }
        log.Check(!folded || viewer.VersionChoices >= 2, "main: the viewer offers 原图 and the version");
        viewer.Close();
        log.Check(viewer.Visibility == Visibility.Collapsed, "main: closing the viewer returns to the grid");
        log.Check(d.Grid.Entries.All(p => !string.Equals(p.Path, saved, StringComparison.OrdinalIgnoreCase)),
                  "main: the version is not a tile of its own");

        // ----- A stack in the viewer.
        if (d.Model?.ShotOf(burst) is { } stack && stack.Stack.Count > 1)
        {
            d.OpenViewer(stack.Cover);
            log.Check(await UiTestLog.WaitAsync(() => viewer.Photo.IsShowingPhoto) && viewer.StackShown, "main: a stack shows its shots in the viewer");
            viewer.Close();
        }

        // ----- Batch filter on one photo.
        d.Go("all");
        d.SelectForTest(true);
        log.Check(d.SelectionActionsInHeader, "main: selecting puts its actions in the header row");
        d.Grid.SelectPath(d.Model?.ShotOf(copy)?.Cover.Path ?? copy);
        int made = await d.FilterSelectionForTest("mono");
        log.Check(made == 1, "main: batch filter saves a filtered version");
    }

    private static async Task CheckConcurrentSaveAsync(MainDesign design, UiTestLog log, string original)
    {
        var viewer = design.Viewer;
        var editor = design.Editor;
        viewer.EditCurrent();
        bool loaded = await UiTestLog.WaitAsync(() => editor.Session?.IsLoaded == true && editor.Preview.HasFrame);
        log.Check(loaded, "main: concurrent save regression opens a real editing session");
        if (!loaded || editor.Session is not { } session) return;
        editor.OpenToolForTest("exposure");
        session.Apply(session.State.WithValue(Adjustment.ByKey("exposure").Index, 0.5f));
        var folder = Path.GetDirectoryName(session.Path)!;
        var before = Directory.GetFiles(folder);
        var saving = editor.SaveAsync(close: false);
        var duplicate = editor.SaveAsync(close: false);
        log.Check(ReferenceEquals(saving, duplicate), "main: simultaneous save requests share one task");
        log.Check(editor.IsFinishing && editor.SliderForTest("exposure")?.Slider.IsEnabled == false,
            "main: saving blocks further adjustment input");
        editor.Open(viewer.Photo.Current!);
        log.Check(ReferenceEquals(editor.Session, session), "main: saving blocks opening a replacement session");
        var cancelling = editor.CancelAsync();
        var leaving = editor.LeaveAsync();
        var canClose = editor.CanCloseAsync();
        await Task.WhenAll(saving, duplicate, cancelling, leaving, canClose).WaitAsync(TimeSpan.FromSeconds(30));
        log.Check(leaving.Result && canClose.Result, "main: navigation and window-close checks await the active save");
        var saved = session.LastSaved;
        log.Check(saved is not null && File.Exists(saved) &&
                  Directory.GetFiles(folder).Except(before, StringComparer.OrdinalIgnoreCase).Count() == 1,
            "main: concurrent save and cancel produce exactly one saved copy");
        log.Check(saved is not null && viewer.Photo.Current?.Path == saved && viewer.Photo.IsShowingPhoto &&
                  editor.Session is null && editor.Stage.Visibility == Visibility.Collapsed,
            "main: cancel waits for saving and returns to its loaded result");
        log.Check(saved is not null && await UiTestLog.WaitAsync(() =>
            design.Model?.ShotOf(original)?.Versions.Any(v => string.Equals(v.Path, saved, StringComparison.OrdinalIgnoreCase)) == true, 60000),
            "main: the concurrent saved result folds under its original as a version");
    }
}

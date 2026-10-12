using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SPhoto.Editing;
using SPhoto.Library;
using SPhoto.Services;
using SPhoto.Views;
using Windows.Storage.Pickers;
using Windows.System;

namespace SPhoto.Ui;

/// <summary>
/// Settings, over the app, laid out like the Windows 10 Settings app: a
/// back arrow, a title and plain sections.
/// </summary>
internal sealed class SettingsView : Grid
{
    private readonly Action _close;
    private readonly StackPanel _folders = new() { Spacing = 4 };
    private readonly RadioButtons _openIn = new();
    private readonly TextBlock _defaultApp = Kit.Text("", 13, secondary: true);
    private readonly TextBlock _gpu = Kit.Text("正在检查…", 14, secondary: true);
    private readonly ComboBox _exportTag = new() { MinWidth = 240, PlaceholderText = "选择标签" };
    private readonly RadioButtons _exportMode = new();
    private readonly Button _export;
    private readonly TextBlock _exportStatus = Kit.Text("", 13, secondary: true);
    private readonly TextBlock _missing = Kit.Text("正在检查…", 14, secondary: true);
    private readonly Button _forget;
    private IReadOnlyList<string> _missingKeys = Array.Empty<string>();

    internal SettingsView(Action close)
    {
        _close = close;
        Background = Kit.White;
        var layout = Kit.Rows(Kit.Auto, Kit.Star());
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Padding = new Thickness(12, 12, 12, 0) };
        header.Children.Add(Kit.IconButton(Kit.G.Back, "返回（Esc）", _close));
        var title = Kit.Title("设置", 28);
        title.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(title);
        layout.Children.Add(header);

        var body = new StackPanel { Spacing = 8, Padding = new Thickness(56, 16, 40, 40), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };

        body.Children.Add(Section("图库文件夹"));
        body.Children.Add(Kit.Paragraph("SPhoto 显示这些文件夹（包括子文件夹）里的照片。移除文件夹只是不再显示，文件不会被删除。"));
        body.Children.Add(_folders);
        var add = Kit.TextButton("添加文件夹", async () =>
        {
            if (await LibraryActions.AddFolderAsync(XamlRoot) is not null) BuildFolders();
        }, glyph: Kit.G.FolderAdd);
        add.Margin = new Thickness(0, 4, 0, 0);
        body.Children.Add(add);

        body.Children.Add(Section("导出标签"));
        body.Children.Add(Kit.Paragraph("把一个标签里的照片，不管原来在哪个文件夹，复制或移动到同一个文件夹里。只做这一次，之后不会同步；重名的文件自动加上 (1)，不会覆盖。照片的标签保持不变。"));
        var exportRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 4, 0, 0) };
        exportRow.Children.Add(_exportTag);
        _exportMode.MaxColumns = 2;
        _exportMode.Items.Add(new RadioButton { Content = "复制", Tag = false });
        _exportMode.Items.Add(new RadioButton { Content = "移动", Tag = true });
        _exportMode.SelectedIndex = 0;
        exportRow.Children.Add(_exportMode);
        body.Children.Add(exportRow);
        _export = Kit.TextButton("选择文件夹并导出…", async () => await ExportAsync());
        _export.Margin = new Thickness(0, 4, 0, 0);
        _export.IsEnabled = false;
        _exportTag.SelectionChanged += (_, _) => _export.IsEnabled = _exportTag.SelectedItem is not null;
        body.Children.Add(_export);
        body.Children.Add(_exportStatus);

        body.Children.Add(Section("图库维护"));
        body.Children.Add(Kit.Paragraph("标签跟着照片的内容走，照片改名、换文件夹都不会丢。照片移出图库（或所在的移动硬盘没插上）时，它的标签仍然记着，等照片回来会自动出现。确定不再需要时，可以在这里清除。"));
        body.Children.Add(_missing);
        _forget = Kit.TextButton("清除这些标签记录…", async () => await ForgetMissingAsync());
        _forget.Margin = new Thickness(0, 4, 0, 0);
        _forget.Visibility = Visibility.Collapsed;
        body.Children.Add(_forget);

        body.Children.Add(Section("默认应用"));
        body.Children.Add(Kit.Paragraph("让 Windows 用 SPhoto 打开照片：先把正在运行的这份 SPhoto 登记到 Windows，再在随后打开的 Windows 设置里把它选为照片的默认应用。"));
        var register = Kit.TextButton("设为默认看图应用…", async () =>
        {
            try
            {
                DefaultApp.Register();
                _defaultApp.Text = "";
                await Launcher.LaunchUriAsync(DefaultApp.SettingsUri);
            }
            catch (Exception)
            {
                _defaultApp.Text = "没能登记到 Windows。";
            }
        });
        register.Margin = new Thickness(0, 4, 0, 0);
        body.Children.Add(register);
        body.Children.Add(_defaultApp);

        body.Children.Add(Section("已有窗口时打开照片"));
        body.Children.Add(Kit.Paragraph("SPhoto 已经开着时，从资源管理器打开的照片显示在哪里。"));
        _openIn.Items.Add(new RadioButton { Content = "在当前窗口显示", Tag = false });
        _openIn.Items.Add(new RadioButton { Content = "新开一个窗口", Tag = true });
        _openIn.SelectedIndex = App.Prefs.OpenInNewWindow ? 1 : 0;
        _openIn.SelectionChanged += (_, _) =>
        {
            if (_openIn.SelectedItem is not RadioButton { Tag: bool newWindow }) return;
            App.Prefs.OpenInNewWindow = newWindow;
            App.Prefs.Save();
        };
        body.Children.Add(_openIn);

        body.Children.Add(Section("关于"));
        body.Children.Add(Kit.Text($"SPhoto {AppInfo.Version} · 修图内核 {Native.Version}", 14));
        body.Children.Add(_gpu);
        body.Children.Add(Kit.Paragraph("编辑永远另存为新版本，原图不会被修改。标签和页面只记在 SPhoto 的索引里，不写进照片。"));

        var scroll = new ScrollViewer { Content = body };
        Grid.SetRow(scroll, 1);
        layout.Children.Add(scroll);
        Children.Add(layout);

        KeyboardAcceleratorPlacementMode = Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;

        KeyboardAccelerators.Add(Kit.Key(VirtualKey.Escape, VirtualKeyModifiers.None, _close));
        BuildFolders();
        Loaded += async (_, _) =>
        {
            await LoadLibraryAsync();
            try
            {
                _gpu.Text = await App.Worker.IsGpuAsync ? "预览使用显卡加速（OpenGL ES，经 ANGLE 转到 Direct3D）。" : "没有找到可用的显卡加速，预览使用处理器计算。";
            }
            catch (Exception)
            {
                _gpu.Text = "预览渲染器启动失败。";
            }
        };
    }

    private static TextBlock Section(string text)
    {
        var t = Kit.Title(text, 20);
        t.Margin = new Thickness(0, 24, 0, 4);
        return t;
    }

    /// The tags to export and what the maintenance section finds, read from the index.
    private async Task LoadLibraryAsync()
    {
        try
        {
            var catalog = await Task.Run(() => App.Library.Catalog());
            var selected = (_exportTag.SelectedItem as ComboBoxItem)?.Tag as string;
            _exportTag.Items.Clear();
            foreach (var t in catalog.TagCounts())
            {
                var item = new ComboBoxItem { Content = $"{t.Name}（{t.Count} 张）", Tag = t.Name };
                _exportTag.Items.Add(item);
                if (t.Name == selected) _exportTag.SelectedItem = item;
            }
            _exportTag.IsEnabled = _exportTag.Items.Count > 0;
            _exportTag.PlaceholderText = _exportTag.Items.Count > 0 ? "选择标签" : "还没有标签";
            _missingKeys = catalog.MissingTagged();
            _missing.Text = _missingKeys.Count == 0
                ? "没有需要清理的记录。"
                : $"有 {_missingKeys.Count} 张照片已不在图库中，仍记着它们的标签。";
            _forget.Visibility = _missingKeys.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception e)
        {
            Crash.Log(e);
            _missing.Text = "没能读取图库。";
        }
    }

    private async Task ExportAsync()
    {
        if ((_exportTag.SelectedItem as ComboBoxItem)?.Tag is not string tag) return;
        bool move = _exportMode.SelectedItem is RadioButton { Tag: true };
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.Window.Hwnd);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        var catalog = await Task.Run(() => App.Library.Catalog());
        // One file per photo: identical copies elsewhere are the same photo.
        var paths = catalog.PhotosWithTag(tag).Select(p => p.Path).ToList();
        if (paths.Count == 0) return;
        if (move && !await Dialogs.ConfirmAsync(XamlRoot, $"把“{tag}”的 {paths.Count} 张照片移动到这里？",
                folder.Path + "\n\n照片会从原来的文件夹移走。标签保持不变；如果这个文件夹不在图库里，这些照片会暂时不在 SPhoto 中显示，添加这个文件夹后会带着标签回来。", "移动"))
            return;
        _export.IsEnabled = false;
        _exportTag.IsEnabled = false;
        var verb = move ? "移动" : "复制";
        _exportStatus.Text = $"正在{verb} 0 / {paths.Count}…";
        try
        {
            var progress = new Progress<int>(n => _exportStatus.Text = $"正在{verb} {n} / {paths.Count}…");
            var result = await TagExport.RunAsync(paths, folder.Path, move, progress);
            var parts = new List<string> { $"已{verb} {result.Done} 张到 {folder.Path}" };
            if (result.Skipped > 0) parts.Add($"{result.Skipped} 张本来就在那里");
            if (result.Failed.Count > 0) parts.Add($"{result.Failed.Count} 张没能{verb}（{string.Join("、", result.Failed.Take(3))}）");
            _exportStatus.Text = string.Join("；", parts) + "。";
        }
        catch (Exception e)
        {
            Crash.Log(e);
            _exportStatus.Text = $"没能{verb}：{e.Message}";
        }
        finally
        {
            _exportTag.IsEnabled = true;
            _export.IsEnabled = _exportTag.SelectedItem is not null;
        }
        if (move) await LoadLibraryAsync();
    }

    private async Task ForgetMissingAsync()
    {
        if (_missingKeys.Count == 0) return;
        if (!await Dialogs.ConfirmAsync(XamlRoot, $"清除 {_missingKeys.Count} 张照片的标签记录？",
                "这些照片现在不在图库里。如果只是移动硬盘没插上，清除后它们回来时就没有标签了。图库里现有的照片和文件都不受影响。", "清除"))
            return;
        var keys = _missingKeys.ToList();
        await Task.Run(() => App.Library.Index.ForgetTags(keys));
        await LoadLibraryAsync();
    }

    private void BuildFolders()
    {
        _folders.Children.Clear();
        if (App.Library.Folders.Count == 0)
        {
            _folders.Children.Add(Kit.Text("还没有文件夹。", 14, secondary: true));
        }
        foreach (var folder in App.Library.Folders)
        {
            var row = Kit.Columns(Kit.Auto, Kit.Star(), Kit.Auto);
            row.Background = Kit.Chrome;
            row.Padding = new Thickness(12, 6, 6, 6);
            row.ColumnSpacing = 12;
            var icon = Kit.Icon(Kit.G.Folder, 18);
            row.Children.Add(icon);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(Kit.Text(LibraryActions.FolderName(folder), 14));
            text.Children.Add(Kit.Text(folder, 12, secondary: true));
            row.Children.Add(Kit.At(text, column: 1));
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            actions.Children.Add(Kit.IconButton(Kit.G.Explorer, "在资源管理器中打开", () => Views.Shell.OpenFolder(folder)));
            var f = folder;
            actions.Children.Add(Kit.IconButton(Kit.G.Close, "从图库中移除", () =>
            {
                App.Library.RemoveFolder(f);
                BuildFolders();
            }));
            row.Children.Add(Kit.At(actions, column: 2));
            _folders.Children.Add(row);
        }
    }
}

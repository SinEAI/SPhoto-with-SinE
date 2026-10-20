using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using SPhoto.Editing;
using SPhoto.Services;
using Windows.System;

namespace SPhoto.Ui.Main;

/// <summary>
/// Editing as a pane on the right of the viewer, the way 信息 opens: the
/// photo stays where it was and its preview takes the place of the photo
/// (Stage), while the pane holds the filters or the adjustments. Crop,
/// curves and HSL open alone in the pane with 取消 / 完成. Saving writes a
/// new version next to the original, which stays untouched.
/// </summary>
internal sealed class MainEditor : Grid
{
    internal const double PaneWidth = 340;

    private readonly MainDesign _owner;
    private readonly PhotoPreview _preview = new();
    private readonly Notice _notice = new();
    private readonly BusyLayer _busy = new();
    private readonly Button _save, _undo;
    private readonly ToggleButton _filtersMode, _adjustMode;
    private readonly ScrollViewer _content = new() { Padding = new Thickness(24, 4, 24, 16) };
    private readonly Grid _modes, _footer, _toolFooter;
    private readonly TextBlock _paneTitle = Kit.Title("编辑", 20), _toolTitle = Kit.Title("", 20);
    private readonly StackPanel _main = new() { Spacing = 4 };
    private readonly ContentPresenter _toolContent = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private EditSession? _session;
    private FilterPanel? _filters;
    private AdjustPanel? _adjust;
    private EditState? _beforeTool;
    private string _original = "";
    private Task? _finish;
    private Task? _saving;

    internal MainEditor(MainDesign owner)
    {
        _owner = owner;
        Width = PaneWidth;
        Background = Kit.White;
        BorderBrush = Kit.Divider;
        BorderThickness = new Thickness(1, 0, 0, 0);
        foreach (var h in new[] { Kit.Auto, Kit.Auto, Kit.Star(), Kit.Auto }) RowDefinitions.Add(new RowDefinition { Height = h });

        // The preview, for the viewer to put where its photo was.
        Stage = new Grid();
        Stage.Children.Add(_preview);
        Stage.Children.Add(_notice);
        Stage.Children.Add(_busy);

        // Header: back, the pane's title, undo and hold-to-compare. Back
        // leaves a tool for the list, or the editor (asking about changes).
        var header = Kit.Columns(Kit.Auto, Kit.Star(), Kit.Auto, Kit.Auto);
        header.Padding = new Thickness(24, 14, 12, 8);
        var back = Kit.IconButton(Kit.G.Back, "返回（Esc）", () =>
        {
            if (ToolOpen) CloseTool(accept: false);
            else _ = CancelAsync();
        }, 14);
        back.Margin = new Thickness(-12, 0, 2, 0);
        header.Children.Add(back);
        _paneTitle.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(Kit.At(_paneTitle, column: 1));
        _toolTitle.VerticalAlignment = VerticalAlignment.Center;
        _toolTitle.Visibility = Visibility.Collapsed;
        header.Children.Add(Kit.At(_toolTitle, column: 1));
        _undo = Kit.IconButton(Kit.G.Undo, "撤销（Ctrl+Z）", () => _session?.Undo());
        header.Children.Add(Kit.At(_undo, column: 2));
        var compare = Kit.IconButton(Kit.G.Compare, "按住查看原图", () => { });
        EditActions.HoldToCompare(compare, _preview);
        header.Children.Add(Kit.At(compare, column: 3));
        Children.Add(header);

        // 滤镜 | 调整.
        _modes = Kit.Columns(Kit.Auto, Kit.Auto, Kit.Star());
        _modes.Padding = new Thickness(24, 0, 24, 8);
        _filtersMode = Pill("滤镜");
        _adjustMode = Pill("调整");
        _filtersMode.Click += (_, _) => ShowMode(filters: true);
        _adjustMode.Click += (_, _) => ShowMode(filters: false);
        _modes.Children.Add(_filtersMode);
        _modes.Children.Add(Kit.At(_adjustMode, column: 1));
        Children.Add(Kit.At(_modes, row: 1));

        _content.Content = _main;
        _toolContent.Visibility = Visibility.Collapsed;
        var body = new Grid();
        body.Children.Add(_content);
        Children.Add(Kit.At(body, row: 2));

        // Footer: 取消 and 保存 for the edit; 取消 and 完成 while a tool is open.
        _footer = Kit.Columns(Kit.Star(), Kit.Auto, Kit.Auto);
        _footer.Padding = new Thickness(24, 12, 24, 16);
        _footer.BorderBrush = Kit.Divider;
        _footer.BorderThickness = new Thickness(0, 1, 0, 0);
        var cancel = Kit.TextButton("取消", async () => await CancelAsync());
        cancel.Margin = new Thickness(0, 0, 8, 0);
        _footer.Children.Add(Kit.At(cancel, column: 1));
        _save = Kit.TextButton("保存", async () => await SaveAsync(close: true), accent: true);
        ToolTipService.SetToolTip(_save, "存为一个新版本，原图不变");
        _footer.Children.Add(Kit.At(_save, column: 2));
        Children.Add(Kit.At(_footer, row: 3));
        _toolFooter = Kit.Columns(Kit.Star(), Kit.Auto, Kit.Auto);
        _toolFooter.Padding = _footer.Padding;
        _toolFooter.BorderBrush = Kit.Divider;
        _toolFooter.BorderThickness = new Thickness(0, 1, 0, 0);
        _toolFooter.Visibility = Visibility.Collapsed;
        var undoTool = Kit.TextButton("取消", () => CloseTool(accept: false));
        ToolTipService.SetToolTip(undoTool, "不要这一项的改动（Esc）");
        undoTool.Margin = new Thickness(0, 0, 8, 0);
        _toolFooter.Children.Add(Kit.At(undoTool, column: 1));
        _toolFooter.Children.Add(Kit.At(Kit.TextButton("完成", () => CloseTool(accept: true), accent: true), column: 2));
        Children.Add(Kit.At(_toolFooter, row: 3));

        var esc = new KeyboardAccelerator { Key = VirtualKey.Escape };
        esc.Invoked += async (_, e) =>
        {
            if (Visibility != Visibility.Visible || _owner.SettingsOpen) return;
            e.Handled = true;
            if (ToolOpen) CloseTool(accept: false);
            else await CancelAsync();
        };
        KeyboardAcceleratorPlacementMode = Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;
        KeyboardAccelerators.Add(esc);
        var undo = new KeyboardAccelerator { Key = VirtualKey.Z, Modifiers = VirtualKeyModifiers.Control };
        undo.Invoked += (_, e) => { if (Visibility == Visibility.Visible && !IsFinishing) { e.Handled = true; _session?.Undo(); } };
        KeyboardAccelerators.Add(undo);
        var redo = new KeyboardAccelerator { Key = VirtualKey.Y, Modifiers = VirtualKeyModifiers.Control };
        redo.Invoked += (_, e) => { if (Visibility == Visibility.Visible && !IsFinishing) { e.Handled = true; _session?.Redo(); } };
        KeyboardAccelerators.Add(redo);
    }

    private static ToggleButton Pill(string text) =>
        Kit.Flat(new ToggleButton { Content = text, Padding = new Thickness(12, 5, 12, 6), CornerRadius = new CornerRadius(14) });

    /// The live preview; the viewer shows it in place of the photo while editing.
    internal Grid Stage { get; }

    internal EditSession? Session => _session;
    internal bool IsFinishing => _saving is { IsCompleted: false } || _finish is { IsCompleted: false };
    internal PhotoPreview Preview => _preview;
    internal bool ToolOpen => _toolFooter.Visibility == Visibility.Visible;
    internal object? ToolContent => _toolContent.Content;

    /// Starts editing a photo; the viewer has already made room for the pane.
    internal async void Open(PhotoEntry entry)
    {
        if (IsFinishing) return;
        _finish = null;
        _original = entry.Path;
        // Background analysis waits so the preview has the machine.
        App.Library.Analyzer.Pause();
        var session = _session = new EditSession(entry.Path);
        session.Changed += _ => Update();
        _preview.Session = session;
        _preview.CropMode = false;
        _filters = new FilterPanel(session, horizontal: false, tileSize: 80);
        _adjust = new AdjustPanel(session);
        CloseTool(accept: true);
        ShowMode(filters: true);
        Update();
        double t0 = Services.Perf.Now;
        bool loaded = await EditActions.LoadAsync(session, _notice, _busy);
        Services.Perf.Line("edit", $"photo loaded for editing in {Services.Perf.Now - t0:F0} ms");
        if (loaded && session == _session) await _filters.RefreshThumbnailsAsync();
        Update();
    }

    private void ShowMode(bool filters)
    {
        if (IsFinishing) return;
        _filtersMode.IsChecked = filters;
        _adjustMode.IsChecked = !filters;
        _main.Children.Clear();
        if (_session is null) return;
        if (filters && _filters is not null)
        {
            _main.Children.Add(_filters);
            _ = _filters.RefreshThumbnailsAsync();
            return;
        }
        // Crop, curves and HSL open on their own; the sliders are all here.
        _main.Children.Add(ToolRow("裁剪与旋转", OpenCrop));
        _main.Children.Add(ToolRow("曲线", () => OpenPanel("曲线", s => new CurvePanel(s))));
        _main.Children.Add(ToolRow("HSL 颜色", () => OpenPanel("HSL 颜色", s => new HslPanel(s))));
        if (_adjust is not null)
        {
            _adjust.Margin = new Thickness(0, 16, 0, 0);
            _main.Children.Add(_adjust);
        }
    }

    /// A full-width line of text that opens a tool, with a chevron at the end.
    private static Button ToolRow(string text, Action open)
    {
        var row = Kit.Columns(Kit.Star(), Kit.Auto);
        row.Children.Add(Kit.Text(text, 15));
        var chevron = Kit.Icon(Kit.G.Right, 12);
        chevron.Foreground = Kit.Tertiary;
        chevron.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(Kit.At(chevron, column: 1));
        var b = Kit.Flat(new Button
        {
            Content = row,
            Padding = new Thickness(8, 10, 8, 10),
            Margin = new Thickness(-8, 0, -8, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        });
        b.Click += (_, _) => open();
        return b;
    }

    private void ShowTool(string title, object content)
    {
        _toolContent.Content = content;
        _toolTitle.Text = title;
        _toolTitle.Visibility = Visibility.Visible;
        _paneTitle.Visibility = Visibility.Collapsed;
        _modes.Visibility = Visibility.Collapsed;
        _footer.Visibility = Visibility.Collapsed;
        _toolFooter.Visibility = Visibility.Visible;
        _main.Children.Clear();
        _toolContent.Visibility = Visibility.Visible;
        _main.Children.Add(_toolContent);
        Motion.Fade(_main, true, start: 0);
    }

    private void OpenPanel(string title, Func<EditSession, UIElement> panel)
    {
        if (IsFinishing) return;
        if (_session is not { IsLoaded: true } s) return;
        _beforeTool = s.State;
        ShowTool(title, panel(s));
    }

    private void OpenCrop()
    {
        if (IsFinishing) return;
        if (_session is not { IsLoaded: true } s) return;
        _beforeTool = s.State;
        ShowTool("裁剪与旋转", new CropPanel(s));
        _preview.CropMode = true;
    }

    /// 完成 keeps what the tool did, 取消 puts it back.
    internal void CloseTool(bool accept)
    {
        if (IsFinishing) return;
        bool wasOpen = ToolOpen;
        if (!accept && _beforeTool is { } before && _session is { } s && !before.Equals(s.State)) s.Apply(before);
        _session?.CommitNow();
        _beforeTool = null;
        _toolContent.Content = null;
        _toolContent.Visibility = Visibility.Collapsed;
        _toolTitle.Visibility = Visibility.Collapsed;
        _paneTitle.Visibility = Visibility.Visible;
        _modes.Visibility = Visibility.Visible;
        _footer.Visibility = Visibility.Visible;
        _toolFooter.Visibility = Visibility.Collapsed;
        _preview.CropMode = false;
        if (wasOpen) ShowMode(filters: _filtersMode.IsChecked == true);
    }

    internal void OpenToolForTest(string key)
    {
        if (key == "curves") OpenPanel("曲线", s => new CurvePanel(s));
        else if (key == "hsl") OpenPanel("HSL 颜色", s => new HslPanel(s));
        else ShowMode(filters: false);
    }

    /// The slider of an adjustment in the 调整 list.
    internal SliderRow? SliderForTest(string key) => _adjust?.Row(key);

    internal void ShowCropForTest(bool crop)
    {
        if (crop) OpenCrop();
        else CloseTool(accept: true);
    }

    private void Update()
    {
        var s = _session;
        bool busy = IsFinishing;
        bool ready = s?.IsLoaded == true && !busy;
        _content.IsEnabled = !busy;
        _filtersMode.IsEnabled = _adjustMode.IsEnabled = !busy;
        _preview.IsHitTestVisible = !busy;
        _save.IsEnabled = ready && !s!.State.IsOriginal;
        _undo.IsEnabled = ready && s!.CanUndo;
    }

    /// Saves a version; close: back to the viewer showing it.
    internal Task SaveAsync(bool close)
    {
        if (_saving is { } saving) return saving;
        if (_finish is { } finishing) return finishing;
        if (_session is not { IsLoaded: true } s || s.State.IsOriginal) return Task.CompletedTask;
        if (ToolOpen) CloseTool(accept: true);
        // Publish the shared task before starting work, so a simultaneous
        // cancel or repeated save cannot finish this session ahead of it.
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _saving = completion.Task;
        Update();
        _ = SaveCoreAsync(s, close, completion);
        return completion.Task;
    }

    private async Task SaveCoreAsync(EditSession session, bool close, TaskCompletionSource completion)
    {
        try
        {
            var saved = await EditActions.SaveCopyAsync(session, _notice, _busy);
            if (saved is not null && close) await FinishAsync(saved);
        }
        catch (Exception e)
        {
            completion.TrySetException(e);
            return;
        }
        finally
        {
            _saving = null;
            Update();
        }
        completion.TrySetResult();
    }

    internal async Task CancelAsync()
    {
        if (_saving is { } saving) await saving;
        if (_finish is { } finishing) { await finishing; return; }
        if (_session is { } s && !await s.ConfirmCloseAsync(XamlRoot)) return;
        await FinishAsync(_session?.LastSaved);
    }

    private Task FinishAsync(string? saved) => _finish ??= FinishCoreAsync(saved);

    private async Task FinishCoreAsync(string? saved)
    {
        // Release the preview only after it has faded out over the loaded
        // viewer image. Clearing Session while Stage is visible paints white.
        try
        {
            await _owner.EditorClosedAsync(_original, saved, _session?.Saved.SuggestedName());
        }
        finally
        {
            App.Library.Analyzer.Resume();
            _session = null;
            _preview.Session = null;
            _filters = null;
            _adjust = null;
            _main.Children.Clear();
        }
    }

    /// Leaves the editor at once (asking about unsaved changes first);
    /// false when the user stayed.
    internal async Task<bool> LeaveAsync()
    {
        if (_saving is { } saving) await saving;
        if (_finish is { } finishing) { await finishing; return true; }
        if (Visibility != Visibility.Visible) return true;
        if (_session is { } s && !await s.ConfirmCloseAsync(XamlRoot)) return false;
        await FinishAsync(_session?.LastSaved);
        Visibility = Visibility.Collapsed;
        return true;
    }

    internal async Task<bool> CanCloseAsync()
    {
        if (_saving is { } saving) await saving;
        if (_finish is { } finishing) await finishing;
        return Visibility != Visibility.Visible || _session is null || await _session.ConfirmCloseAsync(XamlRoot);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SPhoto.Services;
using SPhoto.Views;
using Windows.Foundation;
using Windows.System;

namespace SPhoto.Ui.Main;

/// <summary>
/// The photo beside the sidebar, white. A slim bar above it that never
/// covers the photo: back and the file name on the left, the versions in
/// the middle, a few quiet icons on the right. Below, over the photo, the
/// filmstrip and the shots of a stack, which fade when the mouse rests.
/// 信息 and 编辑 both come in from the right as a pane, and the photo
/// glides to the room left beside it.
/// </summary>
internal sealed class MainViewer : Grid
{
    private readonly MainDesign _owner;
    private readonly MainEditor _editor;
    private readonly PhotoViewer _photo = new();
    private readonly Image _flyer = new()
    {
        Stretch = Stretch.Uniform,
        IsHitTestVisible = false,
        Opacity = 0,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly Grid _top, _bottom, _stage = new();
    private readonly StackPanel _commands = new() { Orientation = Orientation.Horizontal, Spacing = 2, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly TextBlock _name = Kit.Text("", 13), _position = Kit.Text("", 12, secondary: true);
    private readonly StackPanel _versions = new() { Orientation = Orientation.Horizontal, Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _more;
    private readonly MenuFlyout _moreMenu = new() { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
    private readonly MenuFlyout _tagMenu = new() { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
    private readonly PhotoGrid _film = new(56, 56, selection: ListViewSelectionMode.Single, strip: true);
    private readonly PhotoGrid _stackStrip = new(48, 48, selection: ListViewSelectionMode.Single, strip: true);
    private readonly Grid _stackRow = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _stackText = Kit.Text("", 13, secondary: true);
    private readonly Grid _infoHost = new() { Width = MainEditor.PaneWidth, Background = Kit.White, Visibility = Visibility.Collapsed };
    private readonly StackPanel _info = new() { Spacing = 22, Padding = new Thickness(24, 0, 24, 32) };
    private readonly InfoPanel _infoPanel = new();
    private readonly StackPanel _copies = new() { Spacing = 4 };
    private readonly TagEditor _tags;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _hide, _slides;
    private bool _chrome = true, _library, _syncingFilm, _syncingStack;

    internal MainViewer(MainDesign owner, MainEditor editor)
    {
        _owner = owner;
        _editor = editor;
        Background = Kit.White;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // ----- Top: back and name, versions in the middle, icons right.
        _top = Kit.Columns(Kit.Star(), Kit.Auto, Kit.Star());
        _top.Padding = new Thickness(4, 2, 8, 2);
        _top.MinHeight = 44;
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        left.Children.Add(Kit.IconButton(Kit.G.Back, "返回（Esc）", Close, 14));
        _name.VerticalAlignment = VerticalAlignment.Center;
        _name.TextTrimming = TextTrimming.CharacterEllipsis;
        _name.TextWrapping = TextWrapping.NoWrap;
        _name.MaxWidth = 360;
        left.Children.Add(_name);
        _position.VerticalAlignment = VerticalAlignment.Center;
        left.Children.Add(_position);
        _top.Children.Add(left);
        _top.Children.Add(Kit.At(_versions, column: 1));
        _commands.VerticalAlignment = VerticalAlignment.Center;
        _commands.Children.Add(Kit.IconButton(Kit.G.Info, "信息（I）", ToggleInfo));
        _commands.Children.Add(Kit.IconButton(Kit.G.Edit, "编辑（E）", EditCurrent));
        var tag = Kit.IconButton(Kit.G.Tag, "添加标签", () => { });
        tag.Flyout = _tagMenu;
        _tagMenu.Opening += (_, _) => _owner.FillTagMenu(_tagMenu, () => Key is { } k ? new[] { k } : Array.Empty<string>());
        _commands.Children.Add(tag);
        _commands.Children.Add(Kit.IconButton(Kit.G.Delete, "删除（Delete）", () => _ = DeleteAsync()));
        _more = Kit.IconButton(Kit.G.More, "更多", () => { });
        _more.Flyout = _moreMenu;
        _moreMenu.Opening += (_, _) => FillMoreMenu();
        _commands.Children.Add(_more);
        _top.Children.Add(Kit.At(_commands, column: 2));
        Children.Add(_top);

        // ----- The photo, or the editor's preview of it.
        _stage.Children.Add(_photo);
        _stage.Children.Add(_flyer);
        _editor.Stage.Visibility = Visibility.Collapsed;
        _stage.Children.Add(_editor.Stage);

        // ----- Bottom, over the photo: the stack's shots, then the filmstrip.
        _bottom = Kit.Rows(Kit.Auto, Kit.Auto);
        _bottom.VerticalAlignment = VerticalAlignment.Bottom;
        _bottom.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xE6, 255, 255, 255));
        _bottom.Padding = new Thickness(12, 6, 12, 6);
        var stackLayout = Kit.Columns(Kit.Auto, Kit.Star(), Kit.Auto);
        _stackText.VerticalAlignment = VerticalAlignment.Center;
        _stackText.Margin = new Thickness(6, 0, 12, 0);
        stackLayout.Children.Add(_stackText);
        _stackStrip.View.Height = 60;
        stackLayout.Children.Add(Kit.At(_stackStrip.View, column: 1));
        var stackActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var cover = Kit.Flat(Kit.TextButton("设为封面", SetCover));
        ToolTipService.SetToolTip(cover, "在图库里用这张代表整组");
        var apart = Kit.Flat(Kit.TextButton("移出这组", KeepApart));
        ToolTipService.SetToolTip(apart, "这张不再和其他照片叠在一起");
        stackActions.Children.Add(cover);
        stackActions.Children.Add(apart);
        stackLayout.Children.Add(Kit.At(stackActions, column: 2));
        _stackRow.Children.Add(stackLayout);
        _stackRow.Margin = new Thickness(0, 0, 0, 4);
        _bottom.Children.Add(_stackRow);
        _film.View.Height = 70;
        _bottom.Children.Add(Kit.At(_film.View, row: 1));
        _stage.Children.Add(_bottom);
        Children.Add(Kit.At(_stage, row: 1));
        _film.View.SelectionChanged += (_, _) =>
        {
            if (_syncingFilm || _film.Selected.FirstOrDefault() is not { } e) return;
            int index = _photo.Photos.ToList().FindIndex(p => string.Equals(p.Path, e.Path, StringComparison.OrdinalIgnoreCase));
            if (index >= 0 && index != _photo.Index) _photo.Go(index - _photo.Index);
        };
        _stackStrip.View.SelectionChanged += (_, _) =>
        {
            if (_syncingStack || _stackStrip.Selected.FirstOrDefault() is not { } e) return;
            if (!string.Equals(e.Path, _photo.Current?.Path, StringComparison.OrdinalIgnoreCase)) _photo.ReplaceCurrent(e);
        };

        // ----- Right: information or editing, never both.
        _tags = new TagEditor(() => Key, async () => await _owner.ReloadModelAsync());
        _info.Children.Add(PaneHeader("信息", "收起信息（I）", ToggleInfo));
        _info.Children.Add(_infoPanel);
        _info.Children.Add(_copies);
        _info.Children.Add(Section("标签", _tags));
        _infoHost.Children.Add(new ScrollViewer { Content = _info });
        _infoHost.BorderBrush = Kit.Divider;
        _infoHost.BorderThickness = new Thickness(1, 0, 0, 0);
        Children.Add(Kit.At(_infoHost, column: 1, rowSpan: 2));
        Children.Add(Kit.At(_editor, column: 1, rowSpan: 2));

        _photo.CurrentChanged += OnCurrentChanged;
        // Dragged down: the page shows through more the further it goes.
        _photo.DismissProgress += p => Opacity = 1 - 0.6 * p;
        _photo.DismissRequested += Close;
        _photo.PhotoLoaded += _ =>
        {
            _photoLoaded = true;
            HandOver();
        };

        var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _hide = queue.CreateTimer();
        _hide.Interval = TimeSpan.FromSeconds(2.5);
        _hide.IsRepeating = false;
        _hide.Tick += (_, _) => HideChrome();
        _slides = queue.CreateTimer();
        _slides.Interval = TimeSpan.FromSeconds(3);
        _slides.Tick += (_, _) =>
        {
            if (_photo.Index < _photo.Photos.Count - 1) _photo.Go(1);
            else SetSlideshow(false);
        };
        PointerMoved += (_, _) => ShowChrome();

        foreach (var (key, act) in new (VirtualKey, Action)[]
                 {
                     (VirtualKey.Escape, Close), (VirtualKey.Left, () => _photo.Go(-1)), (VirtualKey.Right, () => _photo.Go(1)),
                     (VirtualKey.E, EditCurrent), (VirtualKey.I, ToggleInfo), (VirtualKey.Delete, () => _ = DeleteAsync()),
                 })
        {
            var k = new KeyboardAccelerator { Key = key };
            k.Invoked += (_, e) =>
            {
                if (Visibility != Visibility.Visible || Editing || _editor.IsFinishing || _owner.SettingsOpen) return;
                if (_tags.IsTyping)
                {
                    // Esc leaves the tag box first; the next Esc goes back.
                    if (key != VirtualKey.Escape) return;
                    e.Handled = true;
                    Focus(FocusState.Programmatic);
                    return;
                }
                e.Handled = true;
                act();
            };
            KeyboardAcceleratorPlacementMode = Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;
            KeyboardAccelerators.Add(k);
        }
        var print = new KeyboardAccelerator { Key = VirtualKey.P, Modifiers = VirtualKeyModifiers.Control };
        print.Invoked += (_, e) =>
        {
            if (Visibility != Visibility.Visible || Editing || _editor.IsFinishing) return;
            e.Handled = true;
            Print();
        };
        KeyboardAccelerators.Add(print);
    }

    /// A side pane's title with the way back, an arrow, on its left.
    internal static Grid PaneHeader(string title, string tip, Action back)
    {
        var header = Kit.Columns(Kit.Auto, Kit.Star());
        header.Padding = new Thickness(0, 14, 0, 0);
        var arrow = Kit.IconButton(Kit.G.Back, tip, back, 14);
        arrow.Margin = new Thickness(-12, 0, 2, 0);
        header.Children.Add(arrow);
        var text = Kit.Title(title, 20);
        text.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(Kit.At(text, column: 1));
        return header;
    }

    private static StackPanel Section(string title, UIElement content)
    {
        var p = new StackPanel { Spacing = 8 };
        p.Children.Add(Kit.Text(title, 12, secondary: true));
        p.Children.Add(content);
        return p;
    }

    internal PhotoViewer Photo => _photo;
    internal PhotoGrid FilmForTest => _film;

    internal Grid StageForTest => _stage;

    /// Room on the bar's left for the sidebar's button while the sidebar is folded.
    internal double LeadingSpace
    {
        set => _top.Padding = new Thickness(4 + value, 2, 8, 2);
    }
    internal bool ChromeShown => _chrome;
    internal bool ControlsVisible => new UIElement[] { _commands, _versions, _bottom }
        .All(e => e.Visibility == Visibility.Visible);
    internal bool InfoShown => _infoHost.Visibility == Visibility.Visible;
    internal bool StackShown => _stackRow.Visibility == Visibility.Visible;
    internal int VersionChoices => _versions.Children.Count;
    internal TagEditor Tags => _tags;
    private bool Editing => _editor.Visibility == Visibility.Visible;

    private Shot? Shot => _library && _photo.Current is { } e ? _owner.Model?.ShotOf(e.Path) : null;

    /// The fingerprint of the photo on screen, once the library knows it.
    private string? Key => _photo.Current is { } e ? _owner.Model?.KeyOf(e.Path) : null;

    /// Opens on a photo of a page. library: the page lists photos by
    /// content (stacks, versions and copies apply); otherwise a folder,
    /// file by file.
    internal void Open(List<PhotoEntry> photos, string path, bool library, bool slideshow = false, Image? from = null)
    {
        _library = library;
        Visibility = Visibility.Visible;
        _syncingFilm = true;
        try { _film.SetPhotos(photos, force: true); }
        finally { _syncingFilm = false; }
        // The photo starts loading at once; the thumbnail flies meanwhile.
        UpdateLayout();
        // Before showing: a photo decoded ahead loads at once, inside Show.
        _photoLoaded = false;
        _flying = from?.Source is not null;
        if (_flying) _photo.Opacity = 0;
        _photo.Show(photos, Math.Max(0, photos.FindIndex(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase))));
        if (from?.Source is { } source) _ = FlyInAsync(from, source, path);
        ShowChrome();
        SetSlideshow(slideshow);
        Focus(FocusState.Programmatic);
    }

    private bool _flying, _photoLoaded;

    /// <summary>
    /// The tile's thumbnail grows in one move from where it sits in the
    /// grid to where the photo will be, while the photo itself stays
    /// hidden; the photo loads meanwhile, and once both are done the sharp
    /// photo takes over in the same place.
    /// </summary>
    private async Task FlyInAsync(Image tile, ImageSource placeholder, string path)
    {
        _flyer.Source = placeholder;
        _flyer.Opacity = 0;
        try
        {
            // Where the photo will really sit: fitted, and never past its
            // own pixels, so a small photo is not flown bigger than it shows.
            var (pw, ph) = await Task.Run(() => ImageIO.SizeAsync(path));
            var size = _photo.FitSizeFor(pw, ph);
            if (size.Width <= 0 || !Motion.Enabled || tile.ActualWidth <= 0) throw new InvalidOperationException();
            _flyer.Width = size.Width;
            _flyer.Height = size.Height;
            UpdateLayout();
            // The tile's picture overflows its square cell; its own box has the photo's shape.
            var start = tile.TransformToVisual(null).TransformBounds(new Rect(0, 0, tile.ActualWidth, tile.ActualHeight));
            var end = _flyer.TransformToVisual(null).TransformBounds(new Rect(0, 0, _flyer.Width, _flyer.Height));
            _flyer.Opacity = 1;
            Motion.Settle(_flyer, start.Width / end.Width,
                          start.X + start.Width / 2 - (end.X + end.Width / 2),
                          start.Y + start.Height / 2 - (end.Y + end.Height / 2),
                          TimeSpan.FromMilliseconds(300), () =>
                          {
                              _flying = false;
                              HandOver();
                          });
        }
        catch (Exception)
        {
            // No flight: the photo just appears.
            _flying = false;
            _flyer.Opacity = 0;
            HandOver();
        }
    }

    private void HandOver()
    {
        if (_flying) return;
        _photo.Opacity = 1;
        if (_photoLoaded && _flyer.Opacity > 0) Motion.Fade(_flyer, false, TimeSpan.FromMilliseconds(120));
    }

    /// A file opened from Explorer: it and the rest of its folder.
    internal void OpenFile(string path)
    {
        _library = false;
        Visibility = Visibility.Visible;
        _photo.ListChanged -= OnListChanged;
        _photo.ListChanged += OnListChanged;
        _photo.OpenFile(path);
        ShowChrome();
        Focus(FocusState.Programmatic);
    }

    private void OnListChanged()
    {
        // Replacing the filmstrip can select its first item. This is a
        // list update, so keep the requested photo selected throughout.
        _syncingFilm = true;
        try
        {
            _film.SetPhotos(_photo.Photos.ToList());
            if (_photo.Current is { } current) _film.Reveal(current.Path);
        }
        finally { _syncingFilm = false; }
    }

    internal void Close()
    {
        if (Editing || _editor.IsFinishing) return;
        SetSlideshow(false);
        _hide.Stop();
        var shown = _photo.Current?.Path;
        _infoHost.Visibility = Visibility.Collapsed;
        Visibility = Visibility.Collapsed;
        _owner.ViewerClosed(shown);
    }

    internal void ShowChrome()
    {
        _hide.Stop();
        _hide.Start();
        if (_chrome || Editing) return;
        _chrome = true;
        Motion.Fade(_commands, true);
        Motion.Fade(_versions, true);
        Motion.Fade(_bottom, true);
        _photo.ShowArrows = true;
    }

    /// The bar keeps its place (the photo never moves); its icons, the
    /// filmstrip and the arrows fade until the mouse moves again.
    internal void HideChrome()
    {
        // Not while a menu or a pane is in use.
        if (_moreMenu.IsOpen || _tagMenu.IsOpen || InfoShown || Editing) return;
        _chrome = false;
        Motion.Fade(_commands, false, Motion.Medium);
        Motion.Fade(_versions, false, Motion.Medium);
        Motion.Fade(_bottom, false, Motion.Medium);
    }

    private void FillMoreMenu()
    {
        _moreMenu.Items.Clear();
        var slideshow = new MenuFlyoutItem { Text = _slides.IsRunning ? "停止幻灯片放映" : "幻灯片放映" };
        slideshow.Click += (_, _) => SetSlideshow(!_slides.IsRunning);
        _moreMenu.Items.Add(slideshow);
        var print = new MenuFlyoutItem { Text = "打印…", KeyboardAcceleratorTextOverride = "Ctrl+P" };
        print.Click += (_, _) => Print();
        _moreMenu.Items.Add(print);
        var reveal = new MenuFlyoutItem { Text = "在资源管理器中显示" };
        reveal.Click += (_, _) => { if (_photo.Current is { } e) Shell.Reveal(e.Path); };
        _moreMenu.Items.Add(reveal);
    }

    private void Print()
    {
        if (_photo.Current is { } e && !Editing) _ = PhotoPrinter.PrintAsync(XamlRoot, new[] { e });
    }

    private void SetSlideshow(bool on)
    {
        if (on) _slides.Start();
        else _slides.Stop();
    }

    private void ToggleInfo()
    {
        if (Editing) return;
        bool on = !InfoShown;
        _photo.GlideOnNextResize();
        Motion.SlidePane(_infoHost, on);
        if (on) UpdateInfo();
    }

    internal void EditCurrent()
    {
        if (_photo.Current is not { } entry || !_photo.IsShowingPhoto || Editing || _editor.IsFinishing) return;
        SetSlideshow(false);
        _owner.Edit(entry);
    }

    /// Editing starts: the pane comes in where 信息 would, the preview takes
    /// the photo's place, the viewer's own controls step back.
    internal void BeginEdit(PhotoEntry entry)
    {
        int transition = ++_editTransition;
        _hide.Stop();
        // The photo glides to the room beside the pane; the preview fades
        // in over it once its first frame is drawn and the glide is done.
        if (!InfoShown) _photo.GlideOnNextResize();
        _infoHost.Visibility = Visibility.Collapsed;
        // The viewer's own controls fade away rather than vanish at once.
        foreach (var e in new UIElement[] { _commands, _versions, _bottom }) FadeAway(e, transition);
        _photo.ShowArrows = false;
        var stage = _editor.Stage;
        _editor.Preview.BlendingWithViewer = true;
        stage.Opacity = 0;
        stage.Visibility = Visibility.Visible;
        var started = DateTime.UtcNow;
        double t0 = Perf.Now;
        Perf.Line("edit", "open");
        StopWaitingForFrame();
        async void FirstFrame()
        {
            StopWaitingForFrame();
            Perf.Line("edit", $"first preview frame after {Perf.Now - t0:F0} ms");
            // After the photo's glide has settled, the preview takes over
            // gently, so the hand-over is not seen as a blink.
            var wait = Motion.Medium + TimeSpan.FromMilliseconds(80) - (DateTime.UtcNow - started);
            if (wait > TimeSpan.Zero) await Task.Delay(wait);
            if (Editing && transition == _editTransition)
                Motion.Fade(stage, true, Motion.Medium, start: 0, done: () =>
                {
                    if (Editing && transition == _editTransition) _editor.Preview.BlendingWithViewer = false;
                });
            Perf.Line("edit", $"preview fades in after {Perf.Now - t0:F0} ms");
        }
        _awaitFrame = FirstFrame;
        _editor.Preview.FrameShown += FirstFrame;
        Motion.SlidePane(_editor, true);
        _editor.Open(entry);
    }

    private Action? _awaitFrame;
    private int _editTransition;

    private async void FadeAway(UIElement e, int transition)
    {
        if (e.Visibility != Visibility.Visible) return;
        Motion.Fade(e, false);
        await Task.Delay(Motion.Short);
        if (Editing && transition == _editTransition) e.Visibility = Visibility.Collapsed;
    }

    private void StopWaitingForFrame()
    {
        if (_awaitFrame is { } f) _editor.Preview.FrameShown -= f;
        _awaitFrame = null;
    }

    /// Editing ended; saved is the version it wrote, if any.
    internal async Task EndEditAsync(string? saved)
    {
        _editTransition++;
        StopWaitingForFrame();
        // Keep the rendered preview covering the viewer until the saved
        // file has decoded. Otherwise the original flashes back underneath.
        if (saved is not null) await ShowSavedAsync(saved, animate: false);
        _editor.Preview.BlendingWithViewer = true;
        _photo.GlideOnNextResize();
        var paneClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Motion.SlidePane(_editor, false, () => paneClosed.TrySetResult());
        var stage = _editor.Stage;
        Motion.Fade(stage, false);
        foreach (var e in new UIElement[] { _commands, _versions, _bottom })
        {
            e.Visibility = Visibility.Visible;
            Motion.Fade(e, true);
        }
        _photo.ShowArrows = true;
        _chrome = true;
        ShowChrome();
        Focus(FocusState.Programmatic);
        await paneClosed.Task;
        stage.Visibility = Visibility.Collapsed;
        _editor.Preview.BlendingWithViewer = false;
    }

    private async Task DeleteAsync()
    {
        if (_photo.Current is not { } entry) return;
        var removed = await _owner.DeleteAsync(new[] { entry }, _library, wholeStacks: false);
        if (removed.Count == 0) return;
        _owner.Removed(removed);
        if (!_photo.RemoveCurrent()) Close();
    }

    // ----- What the photo on screen is -----

    private void OnCurrentChanged(PhotoEntry e)
    {
        _name.Text = e.Name;
        ToolTipService.SetToolTip(_name, $"{e.Name}\n{e.Taken:yyyy年M月d日 HH:mm}");
        _position.Text = _photo.Photos.Count > 1 ? $"{_photo.Index + 1} / {_photo.Photos.Count}" : "";
        _syncingFilm = true;
        if (_photo.Index < _photo.Photos.Count) _film.Reveal(_photo.Photos[_photo.Index].Path);
        _syncingFilm = false;
        UpdateShot();
        if (InfoShown) UpdateInfo();
    }

    /// The library learned something (a version saved, a stack formed).
    internal void ModelChanged()
    {
        if (Visibility != Visibility.Visible) return;
        UpdateShot();
        if (InfoShown) UpdateInfo();
    }

    private void UpdateShot()
    {
        var shot = Shot;
        // Versions: 原图 and each saved edit, oldest first, by the names
        // they were given ("版本 N" until then). Right click renames one.
        _versions.Children.Clear();
        if (shot is { Versions.Count: > 0 } && _owner.Model is { } model)
        {
            var choices = new List<(string Label, PhotoEntry Entry, bool Version)> { ("原图", shot.Cover, false) };
            foreach (var v in shot.Versions.Reverse()) choices.Add((model.VersionLabel(shot, v), v, true));
            foreach (var (label, entry, version) in choices)
            {
                var b = Kit.Flat(new ToggleButton { Content = label, Padding = new Thickness(10, 3, 10, 4), CornerRadius = new CornerRadius(12), FontSize = 13, MaxWidth = 180 });
                b.IsChecked = string.Equals(entry.Path, _photo.Current?.Path, StringComparison.OrdinalIgnoreCase);
                var target = entry;
                b.Click += (_, _) => _photo.ReplaceCurrent(target);
                ToolTipService.SetToolTip(b, version ? $"{entry.Name}（右键可重命名）" : entry.Name);
                if (version) b.ContextFlyout = VersionMenu(model, target, label);
                _versions.Children.Add(b);
            }
        }
        // Stack: the similar shots, the one on screen selected.
        bool stacked = shot is { Stack.Count: > 1 };
        _stackRow.Visibility = stacked ? Visibility.Visible : Visibility.Collapsed;
        if (stacked)
        {
            _stackText.Text = $"这一组 {shot!.Stack.Count} 张相似照片";
            _syncingStack = true;
            _stackStrip.SetPhotos(shot.Stack.ToList());
            if (_photo.Current is { } current) _stackStrip.Reveal(current.Path);
            _syncingStack = false;
        }
        // The shots either side in the stack open as fast as the filmstrip's neighbours.
        var ahead = new List<PhotoEntry>();
        if (stacked)
        {
            var stack = shot!.Stack;
            int at = _photo.Current is { } shown ? stack.ToList().FindIndex(p => string.Equals(p.Path, shown.Path, StringComparison.OrdinalIgnoreCase)) : -1;
            if (at < 0) at = 0;
            for (int i = Math.Max(0, at - 1); i <= Math.Min(stack.Count - 1, at + 1); i++)
                if (i != at) ahead.Add(stack[i]);
        }
        _photo.PrefetchAlso(ahead);
    }

    private void UpdateInfo()
    {
        var entry = _photo.Current;
        _infoPanel.Show(entry);
        _copies.Children.Clear();
        if (entry is null) return;
        var copies = _owner.Model?.CopiesOf(entry.Path) ?? new[] { entry.Path };
        if (_library && copies.Count > 1)
        {
            _copies.Children.Add(Kit.Text($"{copies.Count} 个副本", 12, secondary: true));
            _copies.Children.Add(Kit.Paragraph("内容完全相同的文件，在图库里算作一张。", 12));
            foreach (var path in copies)
            {
                var link = new HyperlinkButton { Content = path, Padding = new Thickness(0) };
                ToolTipService.SetToolTip(link, "在资源管理器中显示");
                var p = path;
                link.Click += (_, _) => Shell.Reveal(p);
                _copies.Children.Add(link);
            }
        }
        _tags.Show();
    }

    // ----- Stacks and versions -----

    private MenuFlyout VersionMenu(LibraryModel model, PhotoEntry version, string label)
    {
        var menu = new MenuFlyout();
        var rename = new MenuFlyoutItem { Text = "重命名…" };
        rename.Click += async (_, _) => await RenameVersionAsync(version, label);
        menu.Items.Add(rename);
        if (model.HasVersionName(version))
        {
            var reset = new MenuFlyoutItem { Text = "恢复默认名称" };
            reset.Click += async (_, _) => await SetVersionNameAsync(version, null);
            menu.Items.Add(reset);
        }
        return menu;
    }

    private async Task RenameVersionAsync(PhotoEntry version, string label)
    {
        var box = new TextBox { PlaceholderText = "版本名称，比如“给妈妈的”", Text = label, MaxLength = 30 };
        box.SelectAll();
        var content = new StackPanel { Spacing = 10, MinWidth = 320 };
        content.Children.Add(box);
        content.Children.Add(Kit.Paragraph("名称只记在 SPhoto 里，文件名不变。", 12));
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "重命名版本",
            Content = content,
            PrimaryButtonText = "重命名",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        box.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = box.Text.Trim().Length > 0;
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await SetVersionNameAsync(version, box.Text.Trim());
    }

    private async Task SetVersionNameAsync(PhotoEntry version, string? name)
    {
        if (_owner.Model is not { } model) return;
        if (!model.RenameVersion(version, name))
        {
            await Dialogs.ShowAsync(XamlRoot, "暂时不能重命名", "SPhoto 还在整理这张照片，稍后再试。");
            return;
        }
        await _owner.ReloadModelAsync();
    }

    /// Renames a version without the dialogs; false when it cannot be renamed yet.
    internal async Task<bool> RenameVersionForTest(int oldestFirst, string? name)
    {
        if (Shot is not { } shot || oldestFirst >= shot.Versions.Count || _owner.Model is not { } model) return false;
        if (!model.RenameVersion(shot.Versions.Reverse().ElementAt(oldestFirst), name)) return false;
        await _owner.ReloadModelAsync();
        return true;
    }

    internal string? VersionLabelForTest(int index) =>
        index < _versions.Children.Count ? (_versions.Children[index] as ContentControl)?.Content as string : null;

    private async void SetCover()
    {
        if (Key is not { } key || _owner.Model is not { } model) return;
        model.Index.SetCover(key);
        await _owner.ReloadModelAsync();
    }

    private async void KeepApart()
    {
        if (Key is not { } key || _owner.Model is not { } model) return;
        model.Index.KeepApart(key);
        await _owner.ReloadModelAsync();
    }

    /// The editor saved a version: show it in place of the photo it came from.
    private Task ShowSavedAsync(string saved, bool animate = true)
    {
        var entry = EditActions.EntryFor(saved, _photo.Current?.Taken ?? DateTime.Now);
        return _library ? _photo.ReplaceCurrentAsync(entry, animate) : _photo.InsertAfterCurrentAsync(entry, animate);
    }

    internal void SelectStackMemberForTest(int index)
    {
        if (Shot is { } shot && index < shot.Stack.Count) _photo.ReplaceCurrent(shot.Stack[index]);
    }

    internal void ShowInfoForTest(bool on)
    {
        if (on != InfoShown) ToggleInfo();
    }
}

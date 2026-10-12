using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SPhoto.Services;
using Windows.Foundation;
using Windows.System;

namespace SPhoto.Ui;

/// <summary>
/// Shows one photo of a list, fitted to the space and zoomable to full
/// resolution (wheel, double click), with the neighbours decoded ahead so
/// moving between photos is instant. Hosts add their own chrome.
/// </summary>
public sealed class PhotoViewer : Grid
{
    // The largest decode for viewing; zooming past it loads the full photo.
    private const int ViewSide = 4096;
    private const int MaxFullPixels = 120_000_000;

    private sealed record Decoded(Pixels Pixels, int FullWidth, int FullHeight);

    private readonly ScrollViewer _scroller;
    private readonly Image _picture = new() { Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    // The stand-in thumbnail held under the photo for a moment while the
    // sharp one takes its place, so the swap never shows a blank frame.
    private readonly Image _under = new() { Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly ProgressRing _loading = new() { IsActive = false, Width = 40, Height = 40 };
    private readonly ContentPresenter _failed = new() { Visibility = Visibility.Collapsed };
    private readonly Button _prev, _next;
    private readonly Dictionary<string, Task<Decoded>> _cache = new(StringComparer.OrdinalIgnoreCase);
    // The prefetched decodes made ready for the screen, so a neighbour
    // shows in the same frame it is asked for.
    private readonly Dictionary<string, ImageSource> _sources = new(StringComparer.OrdinalIgnoreCase);
    // Photos outside the list worth decoding ahead (the next shots of a stack).
    private List<string> _extra = new();
    private List<PhotoEntry> _photos = new();
    private int _index, _version;
    private bool _busy;  // a photo is being loaded for the screen
    private double _imageW, _imageH, _fit = 1;
    private Decoded? _shown;
    private bool _fullLoaded;
    private int _slideFrom;  // the side a new photo slides in from: -1, 0 (fade), 1

    // At fit size drag sideways to navigate or down to close; when
    // zoomed, move the viewport.
    private enum Drag { None, Sideways, Down }
    private bool _swipePressed, _panning;
    private Drag _drag;
    private uint _dragPointer;
    private Point _dragStart;
    private double _panX, _panY;

    public PhotoViewer()
    {
        Background = Kit.White;
        _scroller = new ScrollViewer
        {
            ZoomMode = ZoomMode.Enabled,
            MinZoomFactor = 1,
            MaxZoomFactor = 8,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollMode = ScrollMode.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsTabStop = false,
            Content = _picture,
        };
        _scroller.ViewChanged += OnViewChanged;
        _picture.PointerWheelChanged += OnWheel;
        DoubleTapped += OnDoubleTapped;
        Children.Add(_under);
        Children.Add(_scroller);
        Children.Add(_loading);
        Children.Add(_failed);
        _prev = NavButton(Kit.G.Left, "上一张（←）", HorizontalAlignment.Left, () => Go(-1));
        _next = NavButton(Kit.G.Right, "下一张（→）", HorizontalAlignment.Right, () => Go(1));
        Children.Add(_prev);
        Children.Add(_next);
        SizeChanged += (_, _) => Fit(resetZoom: _scroller.ZoomFactor <= 1.001);

        // At fit size the photo follows the pointer. Dragged sideways, let
        // go past a fifth of the width and the next one slides in; dragged
        // down, it shrinks as it goes, and let go far enough the host closes.
        _scroller.AddHandler(PointerPressedEvent, new PointerEventHandler(OnSwipePressed), true);
        AddHandler(PointerMovedEvent, new PointerEventHandler(OnSwipeMoved), true);
        AddHandler(PointerReleasedEvent, new PointerEventHandler(OnSwipeReleased), true);
        AddHandler(PointerCaptureLostEvent, new PointerEventHandler(OnCaptureLost), true);
        AddHandler(PointerCanceledEvent, new PointerEventHandler(OnDragCanceled), true);
        Unloaded += (_, _) => CancelDrag();
    }

    /// The photo was dragged down far enough to close; the host closes at once.
    internal event Action? DismissRequested;

    /// How far a drag down has gone, 0 to 1, so the host can let the page show through.
    internal event Action<double>? DismissProgress;

    private void OnSwipePressed(object sender, PointerRoutedEventArgs e)
    {
        if (_swipePressed || _panning || _shown is null) return;
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed) return;
        if (_scroller.ZoomFactor > 1.01)
        {
            // Touch/pen keep ScrollViewer's native direct manipulation.
            if (e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse) return;
            _panning = CapturePointer(e.Pointer);
            if (!_panning) return;
            _dragPointer = e.Pointer.PointerId;
            _dragStart = point.Position;
            _panX = _scroller.HorizontalOffset;
            _panY = _scroller.VerticalOffset;
            e.Handled = true;
            return;
        }
        _swipePressed = true;
        _drag = Drag.None;
        _dragPointer = e.Pointer.PointerId;
        _dragStart = point.Position;
    }

    private void OnSwipeMoved(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerId != _dragPointer) return;
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
        {
            if (_panning || _swipePressed) UiTest.Trace?.Invoke("viewer: drag ended because the left button is up");
            CancelDrag(); return;
        }
        double dx = point.Position.X - _dragStart.X, dy = point.Position.Y - _dragStart.Y;
        if (_panning)
        {
            _scroller.ChangeView(Math.Clamp(_panX - dx, 0, _scroller.ScrollableWidth),
                                 Math.Clamp(_panY - dy, 0, _scroller.ScrollableHeight),
                                 null, disableAnimation: true);
            e.Handled = true;
            return;
        }
        if (!_swipePressed) return;
        if (_drag == Drag.None)
        {
            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) < 8) return;
            // The first few pixels decide the direction for the whole drag.
            var drag = dy > Math.Abs(dx) ? Drag.Down : Math.Abs(dx) >= Math.Abs(dy) && _photos.Count > 1 ? Drag.Sideways : Drag.None;
            if (drag == Drag.None || !CapturePointer(e.Pointer))
            {
                _swipePressed = false;
                return;
            }
            _drag = drag;
            DropUnder();
        }
        var t = Motion.Transform(_picture);
        if (_drag == Drag.Sideways)
        {
            // Resist at the ends of the list.
            bool edge = (dx > 0 && _index == 0) || (dx < 0 && _index == _photos.Count - 1);
            t.TranslateX = edge ? dx / 4 : dx;
        }
        else
        {
            double down = dy > 0 ? dy : dy / 4;
            double progress = Math.Clamp(down / Math.Max(1, ActualHeight * 0.5), 0, 1);
            _picture.RenderTransformOrigin = new Point(0.5, 0.5);
            t.TranslateX = dx;
            t.TranslateY = down;
            t.ScaleX = t.ScaleY = 1 - 0.3 * progress;
            DismissProgress?.Invoke(progress);
        }
        e.Handled = true;
    }

    private void OnSwipeReleased(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerId != _dragPointer) return;
        if (_panning) { CancelDrag(); e.Handled = true; return; }
        if (!_swipePressed) return;
        _swipePressed = false;
        var drag = _drag;
        _drag = Drag.None;
        if (drag == Drag.None) return;
        ReleasePointerCaptures();
        var t = Motion.Transform(_picture);
        if (drag == Drag.Down)
        {
            if (t.TranslateY > Math.Max(96, ActualHeight / 6) && DismissRequested is { } dismiss)
            {
                dismiss();
                ResetDrag();
                return;
            }
            // Not far enough: back to its place.
            Motion.Settle(_picture, t.ScaleX, t.TranslateX, t.TranslateY, Motion.Short);
            DismissProgress?.Invoke(0);
            return;
        }
        double dx = t.TranslateX;
        int delta = dx < -ActualWidth / 5 ? 1 : dx > ActualWidth / 5 ? -1 : 0;
        int next = _index + delta;
        if (delta == 0 || next < 0 || next >= _photos.Count)
        {
            Motion.SlideX(_picture, dx, 0);
            return;
        }
        // Off the side, then the next photo comes in from the other.
        int version = _version;
        Motion.SlideX(_picture, dx, -Math.Sign(delta) * ActualWidth, Motion.Short,
                      () => { if (version == _version) Go(delta); });
    }

    private void OnDragCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_panning || _swipePressed) UiTest.Trace?.Invoke("viewer: pointer gesture canceled");
        if (e.Pointer.PointerId == _dragPointer) CancelDrag();
    }

    private void OnCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        // Taking capture from ScrollViewer also bubbles its loss event here.
        if (ReferenceEquals(e.OriginalSource, this)) OnDragCanceled(sender, e);
    }

    private void CancelDrag()
    {
        var drag = _drag;
        _swipePressed = _panning = false;
        _drag = Drag.None;
        ReleasePointerCaptures();
        if (drag == Drag.Sideways) Motion.SlideX(_picture, Motion.Transform(_picture).TranslateX, 0, TimeSpan.Zero);
        else if (drag == Drag.Down) ResetDrag();
    }

    /// The photo back in its place, at once (the host has closed over it).
    private void ResetDrag()
    {
        var t = Motion.Transform(_picture);
        t.ScaleX = t.ScaleY = 1;
        t.TranslateX = t.TranslateY = 0;
        DismissProgress?.Invoke(0);
    }

    /// A round, quiet button at the side: white with a hairline edge, a
    /// small chevron, darker only under the pointer.
    private static Button NavButton(string glyph, string tip, HorizontalAlignment side, Action click)
    {
        var b = new Button
        {
            Content = Kit.Icon(glyph, 12),
            Width = 40,
            Height = 40,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(20),
            Margin = new Thickness(16),
            HorizontalAlignment = side,
            VerticalAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(1),
        };
        var face = new SolidColorBrush(Windows.UI.Color.FromArgb(0xE6, 255, 255, 255));
        var edge = new SolidColorBrush(Windows.UI.Color.FromArgb(0x1F, 0, 0, 0));
        b.Resources["ButtonBackground"] = face;
        b.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xF3, 0xF3, 0xF3));
        b.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xE6, 0xE6, 0xE6));
        foreach (var key in new[] { "ButtonBorderBrush", "ButtonBorderBrushPointerOver", "ButtonBorderBrushPressed" }) b.Resources[key] = edge;
        b.Background = face;
        b.BorderBrush = edge;
        ToolTipService.SetToolTip(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    /// Hide the side arrows (a host with its own navigation).
    internal bool ShowArrows { get; set; } = true;

    internal IReadOnlyList<PhotoEntry> Photos => _photos;
    internal int Index => _index;
    internal PhotoEntry? Current => _index >= 0 && _index < _photos.Count ? _photos[_index] : null;

    /// The photo on screen changed (also raised for the first one).
    internal event Action<PhotoEntry>? CurrentChanged;

    /// The photo on screen finished loading; false when it could not be shown.
    internal event Action<bool>? PhotoLoaded;

    internal bool IsShowingPhoto => _shown is not null;

    internal Image PictureForTest => _picture;
    internal ScrollViewer ScrollerForTest => _scroller;
    internal bool IsDraggingForTest => _panning || _swipePressed;

    internal void Show(List<PhotoEntry> photos, int index)
    {
        _photos = new List<PhotoEntry>(photos);
        _index = Math.Clamp(index, 0, Math.Max(0, _photos.Count - 1));
        _ = ShowAsync();
    }

    internal void Go(int delta)
    {
        int next = _index + delta;
        if (next < 0 || next >= _photos.Count) return;
        _index = next;
        _slideFrom = Math.Sign(delta);
        _ = ShowAsync();
    }

    /// Shows another file in the current place of the list (a stacked
    /// shot, an edited version) without moving through the list.
    internal void ReplaceCurrent(PhotoEntry entry)
        => _ = ReplaceCurrentAsync(entry);

    /// An editor hand-over waits for the saved image and skips the normal
    /// navigation fade: its preview already shows the same photo.
    internal Task ReplaceCurrentAsync(PhotoEntry entry, bool animate = true)
    {
        if (Current is null) return Task.CompletedTask;
        _photos[_index] = entry;
        _slideFrom = 0;
        return ShowAsync(animate);
    }

    /// Removes the current photo (after a delete) and shows the next one;
    /// false when none are left.
    internal bool RemoveCurrent()
    {
        if (Current is not { } entry) return false;
        _cache.Remove(entry.Path);
        _sources.Remove(entry.Path);
        _photos.RemoveAt(_index);
        if (_photos.Count == 0)
        {
            ListChanged?.Invoke();
            _picture.Source = null;
            _shown = null;
            return false;
        }
        _index = Math.Min(_index, _photos.Count - 1);
        ListChanged?.Invoke();
        _ = ShowAsync();
        return true;
    }

    /// <summary>
    /// Swaps in a fuller list that still holds the current photo (the rest
    /// of its folder, read after a photo opened from Explorer is showing).
    /// </summary>
    internal void ReplacePhotos(List<PhotoEntry> photos)
    {
        if (Current is not { } entry) return;
        int index = photos.FindIndex(p => string.Equals(p.Path, entry.Path, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return;
        _photos = new List<PhotoEntry>(photos);
        _index = index;
        _prev.Visibility = ShowArrows && _index > 0 ? Visibility.Visible : Visibility.Collapsed;
        _next.Visibility = ShowArrows && _index < _photos.Count - 1 ? Visibility.Visible : Visibility.Collapsed;
        ListChanged?.Invoke();
        Prefetch();
    }

    /// The list changed without the photo on screen changing.
    internal event Action? ListChanged;

    /// Opens a photo from Explorer: shows it at once, then the rest of its
    /// folder in name order.
    internal async void OpenFile(string path)
    {
        var info = new FileInfo(path);
        Show(new List<PhotoEntry> { new(path, info.LastWriteTime, info.Length) }, 0);
        // async void: an exception here would end the app, and the photo the
        // user asked for is already on screen, so the folder list is best effort.
        try
        {
            var all = await Task.Run(() => PhotoLibrary.Siblings(path));
            if (string.Equals(Current?.Path, path, StringComparison.OrdinalIgnoreCase)) ReplacePhotos(all);
        }
        catch (Exception e)
        {
            Crash.Log(e);
        }
    }

    /// Puts a new photo (a saved copy) right after the current one and shows it.
    internal void InsertAfterCurrent(PhotoEntry entry)
        => _ = InsertAfterCurrentAsync(entry);

    internal Task InsertAfterCurrentAsync(PhotoEntry entry, bool animate = true)
    {
        _photos.Insert(Math.Min(_index + 1, _photos.Count), entry);
        _index = Math.Min(_index + 1, _photos.Count - 1);
        ListChanged?.Invoke();
        _slideFrom = animate ? 1 : 0;
        return ShowAsync(animate);
    }

    internal void FitToWindow()
    {
        CancelDrag();
        _scroller.ChangeView(0, 0, 1);
    }

    /// Arrow keys, 0 to fit. Returns true when handled.
    internal bool HandleKey(VirtualKey key)
    {
        switch (key)
        {
            case VirtualKey.Left: Go(-1); return true;
            case VirtualKey.Right: Go(1); return true;
            case VirtualKey.Number0:
            case VirtualKey.NumberPad0: FitToWindow(); return true;
            default: return false;
        }
    }

    private async Task ShowAsync(bool animate = true)
    {
        CancelDrag();
        int version = ++_version;
        if (Current is not { } entry) return;
        _prev.Visibility = ShowArrows && _index > 0 ? Visibility.Visible : Visibility.Collapsed;
        _next.Visibility = ShowArrows && _index < _photos.Count - 1 ? Visibility.Visible : Visibility.Collapsed;
        _failed.Visibility = Visibility.Collapsed;
        DropUnder();
        bool hadPhoto = _picture.Source is not null && _picture.Visibility == Visibility.Visible;
        _busy = true;
        CurrentChanged?.Invoke(entry);
        bool ok = false;
        try
        {
            double t0 = Perf.Now;
            var decoding = DecodeAsync(entry.Path, ScreenSide, view: true);
            bool early = false;
            // An editor hand-over (not animated) already shows this photo.
            if (!decoding.IsCompleted && animate)
            {
                // Not ready yet: the grid's thumbnail stands in at once, and
                // the ring shows only if the wait gets long.
                early = ShowStandIn(entry, animate, hadPhoto);
            }
            if (!decoding.IsCompleted && !early) _ = SpinLaterAsync(version);
            var decoded = await decoding;
            if (version != _version) return;
            double t1 = Perf.Now;
            if (!_sources.TryGetValue(entry.Path, out var source))
            {
                source = await Views.Thumbnails.ToSourceAsync(decoded.Pixels);
                if (version != _version) return;
                _sources[entry.Path] = source;
            }
            Perf.Line("viewer", $"decode {t1 - t0:F0} ms at {decoded.Pixels.Width}x{decoded.Pixels.Height} of {decoded.FullWidth}x{decoded.FullHeight}, to screen {Perf.Now - t1:F0} ms{(early ? ", thumbnail first" : "")}  {entry.Name}");
            _shown = decoded;
            _fullLoaded = decoded.Pixels.Width >= decoded.FullWidth;
            if (early) HoldUnder(version);
            _picture.Source = source;
            _picture.Visibility = Visibility.Visible;
            if (!early) Present(animate, hadPhoto);
            double scale = XamlRoot?.RasterizationScale ?? 1.0;
            _imageW = decoded.FullWidth / scale;
            _imageH = decoded.FullHeight / scale;
            Fit(resetZoom: !early || _scroller.ZoomFactor <= 1.001);
            ok = true;
        }
        catch (Exception ex)
        {
            if (version != _version) return;
            _shown = null;
            _picture.Source = null;
            _picture.Visibility = Visibility.Collapsed;
            _failed.Content = Kit.Message(Kit.G.Pictures, "无法显示这张照片",
                ex is FileNotFoundException
                    ? "文件已被移动或删除。"
                    : "这个格式可能需要安装对应的编解码器（例如 Microsoft Store 中的“HEIF 图像扩展”）。");
            _failed.Visibility = Visibility.Visible;
        }
        finally
        {
            if (version == _version)
            {
                _busy = false;
                _loading.IsActive = false;
            }
        }
        if (version == _version) PhotoLoaded?.Invoke(ok);
        Prefetch();
    }

    /// <summary>
    /// Shows the photo's grid thumbnail, sized as the photo will be, while
    /// the photo itself decodes; false when there is none to show.
    /// </summary>
    private bool ShowStandIn(PhotoEntry entry, bool animate, bool hadPhoto)
    {
        if (Views.Thumbnails.Recent(entry.Path) is not Microsoft.UI.Xaml.Media.Imaging.BitmapImage { PixelWidth: > 0, PixelHeight: > 0 } thumb)
            return false;
        _shown = null;  // no zooming into or dragging a stand-in
        _picture.Source = thumb;
        _picture.Visibility = Visibility.Visible;
        Present(animate, hadPhoto);
        // Only its shape is known: fit it as a large photo would be.
        double scale = XamlRoot?.RasterizationScale ?? 1.0;
        double k = (double)ViewSide / Math.Max(thumb.PixelWidth, thumb.PixelHeight);
        _imageW = thumb.PixelWidth * k / scale;
        _imageH = thumb.PixelHeight * k / scale;
        Fit(resetZoom: true);
        return true;
    }

    /// <summary>
    /// Puts a copy of the stand-in, settled at fit size, under the photo
    /// for a few frames while the sharp image replaces it on top.
    /// </summary>
    private async void HoldUnder(int version)
    {
        var t = Motion.Transform(_picture);
        if (_picture.Source is null || _picture.Opacity < 0.99 || Math.Abs(t.TranslateX) > 0.5 || Math.Abs(t.TranslateY) > 0.5
            || _scroller.ZoomFactor > 1.001) return;
        _under.Source = _picture.Source;
        _under.Width = _picture.Width;
        _under.Height = _picture.Height;
        _under.Visibility = Visibility.Visible;
        await Task.Delay(300);
        if (version == _version) DropUnder();
    }

    private void DropUnder()
    {
        _under.Visibility = Visibility.Collapsed;
        _under.Source = null;
    }

    private async Task SpinLaterAsync(int version)
    {
        await Task.Delay(400);
        if (version == _version && _busy) _loading.IsActive = true;
    }

    /// <summary>
    /// The new photo arrives from the side it was asked for. In place (a
    /// shot of a stack, a version) it simply replaces the one on screen;
    /// only the first photo fades in from nothing.
    /// </summary>
    private void Present(bool animate, bool hadPhoto)
    {
        int from = _slideFrom;
        _slideFrom = 0;
        var t = Motion.Transform(_picture);
        if (!animate || (from == 0 && hadPhoto))
        {
            t.TranslateX = 0;
            Motion.Fade(_picture, true, TimeSpan.Zero);
            return;
        }
        if (from != 0) Motion.SlideX(_picture, from * Math.Min(120, ActualWidth / 6), 0);
        else t.TranslateX = 0;
        Motion.Fade(_picture, true, start: hadPhoto ? 0.6 : 0);
    }

    /// <summary>
    /// Also decode these ahead (the shots next to the one on screen in its
    /// stack), after the photo on screen has loaded.
    /// </summary>
    internal void PrefetchAlso(IEnumerable<PhotoEntry> photos)
    {
        _extra = photos.Select(p => p.Path).ToList();
        if (!_busy) Prefetch();
    }

    /// <summary>
    /// The size a photo is decoded at for viewing: the viewer's longer side
    /// in pixels, so the photo appears quickly; zooming in past it loads
    /// the full photo.
    /// </summary>
    private int ScreenSide
    {
        get
        {
            double side = Math.Max(ActualWidth, ActualHeight) * (XamlRoot?.RasterizationScale ?? 1.0);
            if (side <= 0) side = 2560;
            return (int)Math.Clamp(Math.Ceiling(side / 256) * 256, 1024, ViewSide);
        }
    }

    /// Where a photo of this many pixels sits at fit size, in this viewer's units.
    internal Size FitSizeFor(double pixelWidth, double pixelHeight)
    {
        double scale = XamlRoot?.RasterizationScale ?? 1.0;
        double w = pixelWidth / scale, h = pixelHeight / scale;
        if (w <= 0 || h <= 0 || ActualWidth <= 0 || ActualHeight <= 0) return default;
        double fit = Math.Min(1.0, Math.Min(ActualWidth / w, ActualHeight / h));
        return new Size(w * fit, h * fit);
    }

    private Task<Decoded> DecodeAsync(string path, int side, bool view = false)
    {
        if (view && _cache.TryGetValue(path, out var cached)) return cached;
        var task = Task.Run(async () =>
        {
            var (pixels, fw, fh) = await ImageIO.DecodeForViewAsync(path, side);
            return new Decoded(pixels, fw, fh);
        });
        if (view) _cache[path] = task;
        return task;
    }

    private void Prefetch()
    {
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = Math.Max(0, _index - 1); i <= Math.Min(_photos.Count - 1, _index + 1); i++) keep.Add(_photos[i].Path);
        foreach (var path in _extra) keep.Add(path);
        foreach (var path in _cache.Keys.Where(p => !keep.Contains(p)).ToList()) _cache.Remove(path);
        foreach (var path in _sources.Keys.Where(p => !keep.Contains(p)).ToList()) _sources.Remove(path);
        foreach (var path in keep)
        {
            var task = DecodeAsync(path, ScreenSide, view: true);
            task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            if (!_sources.ContainsKey(path)) _ = PrepareSourceAsync(path, task);
        }
    }

    /// Turns a prefetched decode into an image for the screen ahead of time.
    private async Task PrepareSourceAsync(string path, Task<Decoded> task)
    {
        try
        {
            var decoded = await task;
            if (_sources.ContainsKey(path) || !_cache.TryGetValue(path, out var cached) || cached != task) return;
            var source = await Views.Thumbnails.ToSourceAsync(decoded.Pixels);
            if (_cache.TryGetValue(path, out cached) && cached == task) _sources.TryAdd(path, source);
        }
        catch (Exception)
        {
            // Shown later the normal way, which reports the failure.
        }
    }

    private void Fit(bool resetZoom)
    {
        double sw = ActualWidth, sh = ActualHeight;
        if (_imageW <= 0 || sw <= 0 || sh <= 0) return;
        // Small photos are not blown up past their own size.
        _fit = Math.Min(1.0, Math.Min(sw / _imageW, sh / _imageH));
        _picture.Width = _imageW * _fit;
        _picture.Height = _imageH * _fit;
        _scroller.MinZoomFactor = 1;
        _scroller.MaxZoomFactor = Zoom.SafeMax(8 / _fit);
        if (resetZoom) _scroller.ChangeView(0, 0, 1, disableAnimation: true);
        GlideFromBefore();
    }

    private Rect? _before;
    private DateTime _beforeAt;

    /// <summary>
    /// The space for the photo is about to change (a pane opening, the
    /// sidebar folding): remember where the photo is now, so after the
    /// change it glides to its new place instead of jumping there.
    /// </summary>
    internal void GlideOnNextResize()
    {
        if (!Motion.Enabled || _picture.ActualWidth <= 0 || _scroller.ZoomFactor > 1.01) return;
        try
        {
            _before = _picture.TransformToVisual(null).TransformBounds(new Rect(0, 0, _picture.ActualWidth, _picture.ActualHeight));
            _beforeAt = DateTime.UtcNow;
        }
        catch (ArgumentException)
        {
            _before = null;
        }
    }

    private void GlideFromBefore()
    {
        // A pane that slides out first gives its room back a moment later.
        if (_before is not { } from || DateTime.UtcNow - _beforeAt > TimeSpan.FromSeconds(1)) return;
        _before = null;
        if (_scroller.ZoomFactor > 1.01) return;
        Point origin;
        try
        {
            origin = TransformToVisual(null).TransformPoint(default);
        }
        catch (ArgumentException)
        {
            return;
        }
        // At fit size the photo sits in the middle of the viewer.
        double w = _picture.Width, h = _picture.Height;
        double cx = origin.X + ActualWidth / 2, cy = origin.Y + ActualHeight / 2;
        Motion.Settle(_picture, from.Width / w, from.X + from.Width / 2 - cx, from.Y + from.Height / 2 - cy);
    }

    /// The zoom as a percentage of the photo's own pixels.
    internal double ZoomPercent => _imageW > 0 ? _scroller.ZoomFactor * _fit * 100 : 0;

    internal event Action? ZoomChanged;

    private void ZoomAt(double zoom, Windows.Foundation.Point local, Windows.Foundation.Point viewport)
    {
        zoom = Math.Clamp(zoom, _scroller.MinZoomFactor, _scroller.MaxZoomFactor);
        _scroller.ChangeView(local.X * zoom - viewport.X, local.Y * zoom - viewport.Y, (float)zoom);
    }

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_picture);
        int delta = point.Properties.MouseWheelDelta;
        if (delta == 0) return;
        e.Handled = true;
        ZoomAt(_scroller.ZoomFactor * Math.Pow(1.2, delta / 120.0), point.Position, e.GetCurrentPoint(_scroller).Position);
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        for (var source = e.OriginalSource as DependencyObject; source is not null && source != this; source = VisualTreeHelper.GetParent(source))
            if (source is Button) return;
        e.Handled = true;
        if (_scroller.ZoomFactor > 1.01) FitToWindow();
        else ZoomAt(_fit < 0.99 ? 1 / _fit : 2, e.GetPosition(_picture), e.GetPosition(_scroller));
    }

    private async void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        ZoomChanged?.Invoke();
        if (_scroller.ZoomFactor > 1.001) DropUnder();
        if (e.IsIntermediate || _shown is null || _fullLoaded) return;
        double scale = XamlRoot?.RasterizationScale ?? 1.0;
        if (_picture.Width * _scroller.ZoomFactor * scale <= _shown.Pixels.Width * 1.1) return;
        if ((long)_shown.FullWidth * _shown.FullHeight > MaxFullPixels) return;
        _fullLoaded = true;
        int version = _version;
        try
        {
            var full = await DecodeAsync(Current!.Path, Math.Max(_shown.FullWidth, _shown.FullHeight));
            var source = await Views.Thumbnails.ToSourceAsync(full.Pixels);
            if (version == _version) _picture.Source = source;
        }
        catch (Exception)
        {
            // The smaller decode stays on screen.
        }
    }
}

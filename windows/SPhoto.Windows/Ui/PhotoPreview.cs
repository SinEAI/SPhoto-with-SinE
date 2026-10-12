using System;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using SPhoto.Editing;
using SPhoto.Services;
using Windows.Foundation;
using Windows.UI;

namespace SPhoto.Ui;

/// <summary>A Canvas that can set the mouse cursor.</summary>
public sealed class CropSurface : Canvas
{
    private InputSystemCursorShape? _shape;

    internal void SetCursor(InputSystemCursorShape shape)
    {
        if (_shape == shape) return;
        _shape = shape;
        ProtectedCursor = InputSystemCursor.Create(shape);
    }
}

/// <summary>
/// The edited photo, rendered off the UI thread. While a slider moves it
/// shows a quick, smaller render and sharpens it once the hand pauses. In
/// crop mode it shows the whole frame with a draggable crop over it.
/// </summary>
public sealed class PhotoPreview : Grid
{
    private const int MaxSide = 2560;
    private const int InteractiveSide = 1100;
    private const double CropMargin = 28;

    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    // The original, over the edit, faded in while "compare" is held.
    private readonly Image _original = new() { Stretch = Stretch.Uniform, Opacity = 0, IsHitTestVisible = false };
    private WriteableBitmap? _originalBitmap;
    private (int W, int H)? _originalFor;
    private readonly Grid _host = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly CropSurface _crop = new() { Background = new SolidColorBrush(Colors.Transparent), Visibility = Visibility.Collapsed };
    private readonly Path _shade = new() { Fill = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)) };
    private readonly Rectangle _border = new() { Stroke = new SolidColorBrush(Colors.White), StrokeThickness = 1.5 };
    private readonly Line[] _thirds = new Line[4];
    private readonly Rectangle[] _handles = new Rectangle[4];
    private readonly DispatcherQueueTimer _sharpen;
    private EditSession? _session;
    private WriteableBitmap? _bitmap;
    private bool _cropMode, _comparing;
    private (EditState State, int W, int H)? _last;  // the last request

    // Crop dragging.
    private bool _dragging, _dragMove, _dragLeft, _dragRight, _dragTop, _dragBottom;
    private Point _dragStart;
    private (double X0, double Y0, double X1, double Y1) _dragRect;

    public PhotoPreview()
    {
        // White and edge to edge, like the photo in the viewer, so the
        // preview takes the photo's place without a jump; the crop frame
        // gets a margin for its handles.
        Background = Kit.White;
        _host.Children.Add(_image);
        _host.Children.Add(_original);
        _crop.Children.Add(_shade);
        _crop.Children.Add(_border);
        for (int i = 0; i < 4; i++)
        {
            _thirds[i] = new Line { Stroke = new SolidColorBrush(Color.FromArgb(0x80, 255, 255, 255)), StrokeThickness = 1 };
            _crop.Children.Add(_thirds[i]);
        }
        for (int i = 0; i < 4; i++)
        {
            _handles[i] = new Rectangle
            {
                Width = 14,
                Height = 14,
                Fill = new SolidColorBrush(Colors.White),
                Stroke = new SolidColorBrush(Color.FromArgb(0x60, 0, 0, 0)),
                StrokeThickness = 1,
            };
            _crop.Children.Add(_handles[i]);
        }
        _host.Children.Add(_crop);
        Children.Add(_host);
        _crop.PointerPressed += OnCropPressed;
        _crop.PointerMoved += OnCropMoved;
        _crop.PointerReleased += OnCropReleased;
        _crop.PointerCanceled += OnCropReleased;
        _crop.PointerCaptureLost += OnCropReleased;
        _host.SizeChanged += (_, _) => UpdateCrop();
        SizeChanged += (_, _) => Request(interactive: false);

        _sharpen = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _sharpen.Interval = TimeSpan.FromMilliseconds(160);
        _sharpen.IsRepeating = false;
        _sharpen.Tick += (_, _) => Request(interactive: false);
    }

    /// Frames shown so far; the UI test waits on it.
    internal int FramesShown { get; private set; }

    internal bool HasFrame => _image.Source is not null;

    /// Blend only the picture over the viewer's existing picture. XAML
    /// applies parent opacity to both background and image separately;
    /// fading their white backdrop as well washes even identical photos
    /// toward white. Keep that backdrop during ordinary crop/rotate edits.
    internal bool BlendingWithViewer
    {
        set => Background = value ? null : Kit.White;
    }

    internal Image PreviewPictureForTest => _image;

    /// Raised when a rendered frame is on screen.
    internal event Action? FrameShown;

    /// Raised when a render fails.
    internal event Action? RenderFailed;

    internal EditSession? Session
    {
        get => _session;
        set
        {
            if (_session is not null) _session.Changed -= OnChanged;
            _session = value;
            if (_session is not null) _session.Changed += OnChanged;
            _image.Source = null;
            _bitmap = null;
            _last = null;
            _original.Source = null;
            _originalBitmap = null;
            _originalFor = null;
            Request(interactive: false);
            UpdateCrop();
        }
    }

    /// Show the whole frame with the crop drawn over it.
    internal bool CropMode
    {
        get => _cropMode;
        set
        {
            _cropMode = value;
            _host.Margin = new Thickness(value ? CropMargin : 0);
            Request(interactive: false);
            UpdateCrop();
        }
    }

    /// Show the original photo (held "compare").
    internal bool Comparing
    {
        get => _comparing;
        set
        {
            if (_comparing == value) return;
            _comparing = value;
            if (value) RequestOriginal();
            Motion.Fade(_original, value, Motion.Short);
        }
    }

    private void OnChanged(bool continuous)
    {
        UpdateCrop();
        // A crop drag changes nothing the preview shows (it shows the
        // whole frame), so only the overlay moves.
        if (_session is not null && _last is { } last && DisplayState(_session).Equals(last.State)) return;
        if (continuous)
        {
            Request(interactive: true);
            _sharpen.Stop();
            _sharpen.Start();
        }
        else
        {
            Request(interactive: false);
        }
    }

    private EditState DisplayState(EditSession s) =>
        _cropMode ? s.State.WithGeometry(CropMath.Uncropped(s.State.Geometry)) : s.State;

    /// The original at the size and framing the edit shows (same crop, so
    /// the crossfade lines up), rendered once per size.
    private void RequestOriginal()
    {
        var s = _session;
        if (s?.Source is null || _last is not { } last) return;
        if (_originalFor == (last.W, last.H) && _originalBitmap is not null) return;
        _originalFor = (last.W, last.H);
        var state = EditState.Original.WithGeometry(last.State.Geometry);
        var queue = DispatcherQueue;
        App.Worker.RequestPreview(_original, s.Source, state, last.W, last.H, frame => queue.TryEnqueue(() =>
        {
            if (!ReferenceEquals(s, _session) || frame is null) return;
            _originalBitmap = new WriteableBitmap(frame.Width, frame.Height);
            using (var stream = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsStream(_originalBitmap.PixelBuffer))
            {
                stream.Write(frame.Bgra, 0, frame.Bgra.Length);
            }
            _originalBitmap.Invalidate();
            _original.Source = _originalBitmap;
            FramesShown++;
        }));
    }

    private void Request(bool interactive)
    {
        var s = _session;
        if (s?.Source is null || ActualWidth <= 0 || ActualHeight <= 0) return;
        var state = DisplayState(s);
        var (fw, fh) = s.OutputSize(state.Geometry);
        double scale = XamlRoot?.RasterizationScale ?? 1.0;
        double margin = _cropMode ? 2 * CropMargin : 0;
        double boxW = Math.Max(64, (ActualWidth - margin) * scale);
        double boxH = Math.Max(64, (ActualHeight - margin) * scale);
        // Never larger than the photo's own pixels, as in the viewer, so
        // the preview takes the photo's place without growing.
        _host.MaxWidth = fw / scale;
        _host.MaxHeight = fh / scale;
        double cap = interactive ? InteractiveSide : MaxSide;
        double k = Math.Min(1.0, Math.Min(Math.Min(boxW / fw, boxH / fh), cap / Math.Max(fw, fh)));
        int w = Math.Max(1, (int)Math.Round(fw * k)), h = Math.Max(1, (int)Math.Round(fh * k));
        if (_last is { } last && last.State.Equals(state) && last.W == w && last.H == h) return;
        _last = (state, w, h);
        var queue = DispatcherQueue;
        App.Worker.RequestPreview(this, s.Source, state, w, h, frame => queue.TryEnqueue(() => Show(s, frame)));
    }

    private void Show(EditSession from, RenderedFrame? frame)
    {
        if (!ReferenceEquals(from, _session)) return;
        if (frame is null)
        {
            RenderFailed?.Invoke();
            return;
        }
        if (_bitmap is null || _bitmap.PixelWidth != frame.Width || _bitmap.PixelHeight != frame.Height)
        {
            _bitmap = new WriteableBitmap(frame.Width, frame.Height);
            _image.Source = _bitmap;
        }
        using (var stream = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsStream(_bitmap.PixelBuffer))
        {
            stream.Write(frame.Bgra, 0, frame.Bgra.Length);
        }
        _bitmap.Invalidate();
        FramesShown++;
        if (FramesShown <= 6) Services.Perf.Line("edit", $"preview frame {FramesShown}: {frame.Width}x{frame.Height}, box {ActualWidth:F0}x{ActualHeight:F0}");
        FrameShown?.Invoke();
    }

    // ----- Crop -----

    private void UpdateCrop()
    {
        if (!_cropMode || _session?.Source is null)
        {
            _crop.Visibility = Visibility.Collapsed;
            return;
        }
        _crop.Visibility = Visibility.Visible;
        double W = _host.ActualWidth, H = _host.ActualHeight;
        if (W <= 0 || H <= 0) return;
        var (x, y, w, h) = CropMath.Rect(_session.State.Geometry);
        double left = x * W, top = y * H, right = (x + w) * W, bottom = (y + h) * H;

        var shade = new GeometryGroup { FillRule = FillRule.EvenOdd };
        shade.Children.Add(new RectangleGeometry { Rect = new Rect(0, 0, W, H) });
        shade.Children.Add(new RectangleGeometry { Rect = new Rect(left, top, right - left, bottom - top) });
        _shade.Data = shade;
        Place(_border, left, top);
        _border.Width = Math.Max(0, right - left);
        _border.Height = Math.Max(0, bottom - top);
        for (int i = 0; i < 2; i++)
        {
            double fx = left + (right - left) * (i + 1) / 3, fy = top + (bottom - top) * (i + 1) / 3;
            SetLine(_thirds[i], fx, top, fx, bottom);
            SetLine(_thirds[i + 2], left, fy, right, fy);
        }
        const double r = 7;
        Place(_handles[0], left - r, top - r);
        Place(_handles[1], right - r, top - r);
        Place(_handles[2], right - r, bottom - r);
        Place(_handles[3], left - r, bottom - r);
    }

    private static void Place(FrameworkElement e, double x, double y)
    {
        Canvas.SetLeft(e, x);
        Canvas.SetTop(e, y);
    }

    private static void SetLine(Line line, double x1, double y1, double x2, double y2)
    {
        line.X1 = x1;
        line.Y1 = y1;
        line.X2 = x2;
        line.Y2 = y2;
    }

    /// What dragging at p would do: which edges move, or the whole frame.
    private (bool Move, bool L, bool R, bool T, bool B) HitTest(Point p)
    {
        if (_session is null) return default;
        double W = _host.ActualWidth, H = _host.ActualHeight;
        var (x, y, w, h) = CropMath.Rect(_session.State.Geometry);
        double left = x * W, top = y * H, right = (x + w) * W, bottom = (y + h) * H;
        const double grab = 16;
        bool nearL = Math.Abs(p.X - left) < grab, nearR = Math.Abs(p.X - right) < grab;
        bool nearT = Math.Abs(p.Y - top) < grab, nearB = Math.Abs(p.Y - bottom) < grab;
        bool withinX = p.X > left - grab && p.X < right + grab;
        bool withinY = p.Y > top - grab && p.Y < bottom + grab;
        bool l = nearL && withinY, r = !l && nearR && withinY;
        bool t = nearT && withinX, b = !t && nearB && withinX;
        if (l || r || t || b) return (false, l, r, t, b);
        bool inside = p.X > left && p.X < right && p.Y > top && p.Y < bottom;
        return (inside, false, false, false, false);
    }

    private void OnCropPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_session is null) return;
        var p = e.GetCurrentPoint(_crop).Position;
        var hit = HitTest(p);
        if (!hit.Move && !hit.L && !hit.R && !hit.T && !hit.B) return;
        (_dragMove, _dragLeft, _dragRight, _dragTop, _dragBottom) = hit;
        _dragStart = p;
        var (x, y, w, h) = CropMath.Rect(_session.State.Geometry);
        _dragRect = (x, y, x + w, y + h);
        _dragging = _crop.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnCropMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_session is null) return;
        var p = e.GetCurrentPoint(_crop).Position;
        if (!_dragging)
        {
            var hit = HitTest(p);
            _crop.SetCursor(
                (hit.L && hit.T) || (hit.R && hit.B) ? InputSystemCursorShape.SizeNorthwestSoutheast :
                (hit.R && hit.T) || (hit.L && hit.B) ? InputSystemCursorShape.SizeNortheastSouthwest :
                hit.L || hit.R ? InputSystemCursorShape.SizeWestEast :
                hit.T || hit.B ? InputSystemCursorShape.SizeNorthSouth :
                hit.Move ? InputSystemCursorShape.SizeAll : InputSystemCursorShape.Arrow);
            return;
        }
        double W = _host.ActualWidth, H = _host.ActualHeight;
        if (W <= 0 || H <= 0) return;
        var rect = CropDrag.Resize(_dragRect, (p.X - _dragStart.X) / W, (p.Y - _dragStart.Y) / H,
                                   _dragMove, _dragLeft, _dragRight, _dragTop, _dragBottom,
                                   _session.NormalizedRatio());
        // The preview shows the uncropped frame, so a crop drag needs no
        // render: only the overlay moves.
        _session.Apply(_session.State.WithGeometry(
            CropMath.WithRect(_session.State.Geometry, rect.X0, rect.Y0, rect.X1 - rect.X0, rect.Y1 - rect.Y0)),
            continuous: true);
    }

    private void OnCropReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        _crop.ReleasePointerCaptures();
        _session?.CommitNow();
    }
}

/// <summary>The crop frame's drag rules, kept apart from any control.</summary>
internal static class CropDrag
{
    internal static (double X0, double Y0, double X1, double Y1) Resize(
        (double X0, double Y0, double X1, double Y1) start, double dx, double dy,
        bool move, bool left, bool right, bool top, bool bottom, double ratio)
    {
        var (x0, y0, x1, y1) = start;
        const double min = 0.05;
        if (move)
        {
            double w = x1 - x0, h = y1 - y0;
            x0 = Math.Clamp(x0 + dx, 0, 1 - w);
            y0 = Math.Clamp(y0 + dy, 0, 1 - h);
            return (x0, y0, x0 + w, y0 + h);
        }
        if (left) x0 = Math.Clamp(x0 + dx, 0, x1 - min);
        if (right) x1 = Math.Clamp(x1 + dx, x0 + min, 1);
        if (top) y0 = Math.Clamp(y0 + dy, 0, y1 - min);
        if (bottom) y1 = Math.Clamp(y1 + dy, y0 + min, 1);
        if (ratio <= 0) return (x0, y0, x1, y1);

        // Bend the rect back to the locked ratio, keeping the edges opposite
        // the dragged ones where they are.
        double nw = x1 - x0, nh = y1 - y0;
        bool horizontal = left || right, vertical = top || bottom;
        if (horizontal && vertical)
        {
            if (nw / nh > ratio) nh = nw / ratio;
            else nw = nh * ratio;
            double availW = left ? start.X1 : 1 - start.X0;
            double availH = top ? start.Y1 : 1 - start.Y0;
            if (nw > availW) { nw = availW; nh = nw / ratio; }
            if (nh > availH) { nh = availH; nw = nh * ratio; }
            x0 = left ? start.X1 - nw : start.X0;
            y0 = top ? start.Y1 - nh : start.Y0;
        }
        else if (horizontal)
        {
            double cy = (start.Y0 + start.Y1) / 2;
            nh = nw / ratio;
            double availH = 2 * Math.Min(cy, 1 - cy);
            if (nh > availH) { nh = availH; nw = nh * ratio; }
            x0 = left ? start.X1 - nw : start.X0;
            y0 = cy - nh / 2;
        }
        else
        {
            double cx = (start.X0 + start.X1) / 2;
            nw = nh * ratio;
            double availW = 2 * Math.Min(cx, 1 - cx);
            if (nw > availW) { nw = availW; nh = nw / ratio; }
            x0 = cx - nw / 2;
            y0 = top ? start.Y1 - nh : start.Y0;
        }
        return (x0, y0, x0 + nw, y0 + nh);
    }
}

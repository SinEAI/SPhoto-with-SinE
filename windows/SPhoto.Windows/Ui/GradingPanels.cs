using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using SPhoto.Editing;
using Windows.Foundation;
using Windows.UI;

namespace SPhoto.Ui;

/// <summary>
/// Monotone cubic interpolation (Fritsch and Carlson, 1980), the same rule
/// the core bakes curves with, so the line drawn is the curve applied.
/// Flat outside the first and last points.
/// </summary>
internal static class MonotoneCurve
{
    internal static float[] Sample((float X, float Y)[] points, int count)
    {
        var result = new float[count];
        for (int i = 0; i < count; i++) result[i] = Evaluate(points, i / (float)(count - 1));
        return result;
    }

    internal static float Evaluate((float X, float Y)[] p, float x)
    {
        int n = p.Length;
        if (n == 0) return x;
        if (n == 1 || x <= p[0].X) return p[0].Y;
        if (x >= p[n - 1].X) return p[n - 1].Y;
        // Secant slopes, then tangents limited so the curve never overshoots.
        var d = new float[n - 1];
        for (int i = 0; i < n - 1; i++) d[i] = (p[i + 1].Y - p[i].Y) / Math.Max(1e-6f, p[i + 1].X - p[i].X);
        var m = new float[n];
        m[0] = d[0];
        m[n - 1] = d[n - 2];
        for (int i = 1; i < n - 1; i++) m[i] = d[i - 1] * d[i] <= 0 ? 0 : (d[i - 1] + d[i]) / 2;
        for (int i = 0; i < n - 1; i++)
        {
            if (d[i] == 0)
            {
                m[i] = m[i + 1] = 0;
                continue;
            }
            float a = m[i] / d[i], b = m[i + 1] / d[i], s = a * a + b * b;
            if (s > 9)
            {
                float t = 3 / MathF.Sqrt(s);
                m[i] = t * a * d[i];
                m[i + 1] = t * b * d[i];
            }
        }
        int k = 0;
        while (k < n - 2 && x > p[k + 1].X) k++;
        float h = p[k + 1].X - p[k].X, u = (x - p[k].X) / h;
        float h00 = (2 * u - 3) * u * u + 1, h10 = ((u - 2) * u + 1) * u, h01 = (3 - 2 * u) * u * u, h11 = (u - 1) * u * u;
        return h00 * p[k].Y + h10 * h * m[k] + h01 * p[k + 1].Y + h11 * h * m[k + 1];
    }
}

/// <summary>
/// Tone curves: a channel switch (all, red, green, blue) and a square to
/// draw on. Click the line to add a point, drag a point to move it,
/// double click a point to take it away.
/// </summary>
internal sealed class CurvePanel : SessionPanel
{
    private const double Side = 196;
    private const int MaxPoints = 8;
    private static readonly string[] Names = { "RGB", "红", "绿", "蓝" };
    private static readonly Color[] Colours =
    {
        Color.FromArgb(255, 0x20, 0x20, 0x20), Color.FromArgb(255, 0xD1, 0x34, 0x38),
        Color.FromArgb(255, 0x10, 0x89, 0x3E), Color.FromArgb(255, 0x00, 0x63, 0xB1),
    };

    private readonly Canvas _canvas = new() { Width = Side, Height = Side, Background = new SolidColorBrush(Colors.White) };
    private readonly Polyline _line = new() { StrokeThickness = 2 };
    private readonly List<Ellipse> _dots = new();
    private readonly List<ToggleButton> _channels = new();
    private int _channel;
    private List<(float X, float Y)> _points = new();
    private int _dragging = -1;

    internal CurvePanel(EditSession session) : base(session)
    {
        Orientation = Orientation.Horizontal;
        Spacing = 20;
        HorizontalAlignment = HorizontalAlignment.Center;
        var switches = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        for (int c = 0; c < 4; c++)
        {
            var b = Kit.Flat(new ToggleButton { Content = Names[c], MinWidth = 64, Foreground = new SolidColorBrush(Colours[c]) });
            int channel = c;
            b.Click += (_, _) => SetChannel(channel);
            _channels.Add(b);
            switches.Children.Add(b);
        }
        var reset = Kit.TextButton("重置曲线", () => Session.Apply(Session.State.WithoutCurves()));
        reset.Margin = new Thickness(0, 8, 0, 0);
        switches.Children.Add(reset);
        Children.Add(switches);

        var frame = new Border { BorderBrush = Kit.Divider, BorderThickness = new Thickness(1), Child = _canvas };
        // Quarter grid and the untouched diagonal for reference.
        for (int i = 1; i < 4; i++)
        {
            _canvas.Children.Add(new Line { X1 = Side * i / 4, Y1 = 0, X2 = Side * i / 4, Y2 = Side, Stroke = Kit.Divider, StrokeThickness = 1 });
            _canvas.Children.Add(new Line { X1 = 0, Y1 = Side * i / 4, X2 = Side, Y2 = Side * i / 4, Stroke = Kit.Divider, StrokeThickness = 1 });
        }
        _canvas.Children.Add(new Line { X1 = 0, Y1 = Side, X2 = Side, Y2 = 0, Stroke = Kit.Divider, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 3 } });
        _canvas.Children.Add(_line);
        _canvas.PointerPressed += OnPressed;
        _canvas.PointerMoved += OnMoved;
        _canvas.PointerReleased += OnReleased;
        _canvas.PointerCaptureLost += OnReleased;
        _canvas.DoubleTapped += OnDoubleTapped;
        Children.Add(frame);
        SetChannel(0);
    }

    private void SetChannel(int channel)
    {
        _channel = channel;
        for (int c = 0; c < 4; c++) _channels[c].IsChecked = c == channel;
        _line.Stroke = new SolidColorBrush(Colours[channel]);
        Sync();
    }

    internal override void Sync()
    {
        if (_dragging >= 0) return;  // the drag owns the points
        var stored = Session.State.Curves[_channel];
        _points = stored.Length >= 2 ? stored.ToList() : new List<(float, float)> { (0, 0), (1, 1) };
        Draw();
    }

    private void Draw()
    {
        var samples = MonotoneCurve.Sample(_points.ToArray(), 65);
        var line = new PointCollection();
        for (int i = 0; i < samples.Length; i++) line.Add(new Point(Side * i / 64.0, Side * (1 - samples[i])));
        _line.Points = line;
        foreach (var dot in _dots) _canvas.Children.Remove(dot);
        _dots.Clear();
        foreach (var (x, y) in _points)
        {
            var dot = new Ellipse { Width = 12, Height = 12, Fill = new SolidColorBrush(Colors.White), Stroke = _line.Stroke, StrokeThickness = 2 };
            Canvas.SetLeft(dot, x * Side - 6);
            Canvas.SetTop(dot, (1 - y) * Side - 6);
            _dots.Add(dot);
            _canvas.Children.Add(dot);
        }
    }

    private (float X, float Y) ToCurve(Point p) =>
        ((float)Math.Clamp(p.X / Side, 0, 1), (float)Math.Clamp(1 - p.Y / Side, 0, 1));

    private int Near(Point p)
    {
        for (int i = 0; i < _points.Count; i++)
        {
            double dx = _points[i].X * Side - p.X, dy = (1 - _points[i].Y) * Side - p.Y;
            if (dx * dx + dy * dy < 12 * 12) return i;
        }
        return -1;
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(_canvas).Position;
        int hit = Near(p);
        if (hit < 0)
        {
            if (_points.Count >= MaxPoints) return;
            var c = ToCurve(p);
            _points.Add(c);
            _points.Sort((a, b) => a.X.CompareTo(b.X));
            hit = _points.IndexOf(c);
        }
        _dragging = hit;
        _canvas.CapturePointer(e.Pointer);
        Push(continuous: true);
        e.Handled = true;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging < 0) return;
        var c = ToCurve(e.GetCurrentPoint(_canvas).Position);
        // A point stays between its neighbours.
        float lo = _dragging > 0 ? _points[_dragging - 1].X + 0.01f : 0;
        float hi = _dragging < _points.Count - 1 ? _points[_dragging + 1].X - 0.01f : 1;
        _points[_dragging] = (Math.Clamp(c.X, lo, hi), c.Y);
        Push(continuous: true);
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging < 0) return;
        _dragging = -1;
        _canvas.ReleasePointerCaptures();
        Session.CommitNow();
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        int hit = Near(e.GetPosition(_canvas));
        if (hit <= 0 || hit >= _points.Count - 1) return;  // the ends stay
        _points.RemoveAt(hit);
        Push(continuous: false);
    }

    private void Push(bool continuous)
    {
        Draw();
        Apply(Session.State.WithCurve(_channel, _points), continuous);
    }

    /// For the UI test: lifts the middle of the curve.
    internal void BrightenForTest() =>
        Apply(Session.State.WithCurve(_channel, new[] { (0f, 0f), (0.5f, 0.62f), (1f, 1f) }), continuous: false);
}

/// <summary>
/// Hue, saturation and luminance per colour band: pick a band, then three
/// stepped sliders for it.
/// </summary>
internal sealed class HslPanel : SessionPanel
{
    private static readonly (string Name, Color Colour)[] Bands =
    {
        ("红色", Color.FromArgb(255, 0xE0, 0x3C, 0x31)), ("橙色", Color.FromArgb(255, 0xF0, 0x8A, 0x24)),
        ("黄色", Color.FromArgb(255, 0xE8, 0xC5, 0x1E)), ("绿色", Color.FromArgb(255, 0x3C, 0xA8, 0x4A)),
        ("青色", Color.FromArgb(255, 0x2E, 0xB8, 0xC0)), ("蓝色", Color.FromArgb(255, 0x2F, 0x6F, 0xD8)),
        ("紫色", Color.FromArgb(255, 0x7B, 0x4F, 0xC9)), ("洋红", Color.FromArgb(255, 0xC9, 0x3F, 0xA8)),
    };

    private static readonly StepScale Scale = new(-10, 10, 0.1);
    private readonly List<Border> _rings = new();
    private readonly SliderRow[] _rows = new SliderRow[3];
    private int _band;

    internal HslPanel(EditSession session) : base(session)
    {
        Spacing = 8;
        MaxWidth = 640;
        var swatches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
        for (int b = 0; b < Bands.Length; b++)
        {
            var ring = new Border
            {
                Width = 30,
                Height = 30,
                CornerRadius = new CornerRadius(15),
                BorderThickness = new Thickness(2),
                Padding = new Thickness(3),
                Child = new Border { CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Bands[b].Colour) },
            };
            var button = Kit.Flat(new Button { Content = ring, Padding = new Thickness(2) });
            ToolTipService.SetToolTip(button, Bands[b].Name);
            int band = b;
            button.Click += (_, _) => SetBand(band);
            _rings.Add(ring);
            swatches.Children.Add(button);
        }
        Children.Add(swatches);
        string[] titles = { "色相", "饱和度", "明亮度" };
        var sliders = new Grid { ColumnSpacing = 24 };
        for (int k = 0; k < 3; k++)
        {
            sliders.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            int kind = k;
            _rows[k] = new SliderRow(titles[k], Scale, v => Apply(Session.State.WithBand(kind, _band, Scale.ToValue(v)), continuous: true));
            sliders.Children.Add(Kit.At(_rows[k], column: k));
        }
        Children.Add(sliders);
        var reset = Kit.TextButton("全部颜色复位", () => Session.Apply(Session.State.WithoutHsl()));
        reset.HorizontalAlignment = HorizontalAlignment.Center;
        Children.Add(reset);
        SetBand(0);
    }

    private void SetBand(int band)
    {
        _band = band;
        for (int b = 0; b < _rings.Count; b++)
        {
            _rings[b].BorderBrush = b == band ? new SolidColorBrush(Bands[b].Colour) : new SolidColorBrush(Colors.Transparent);
        }
        Sync();
    }

    internal override void Sync()
    {
        for (int k = 0; k < 3; k++) _rows[k].SetValue(Session.State.Band(k, _band));
    }
}

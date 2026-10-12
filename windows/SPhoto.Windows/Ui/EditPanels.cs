using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SPhoto.Editing;
using SPhoto.Services;

namespace SPhoto.Ui;

/// <summary>
/// A titled slider in whole steps (see <see cref="StepScale"/>), reset by a
/// double click. Leaving 0 takes a slightly longer pull than other steps,
/// so the neutral position is easy to land on and hard to nudge off by
/// accident. Values it sets are never written back to it while dragging.
/// </summary>
internal sealed class SliderRow : StackPanel
{
    private readonly TextBlock _value;
    private readonly StepScale _scale;
    private readonly int _reset;
    private bool _setting, _dragging;
    private double _pointerX;

    internal SliderRow(string title, StepScale scale, Action<int> changed, int reset = 0)
    {
        _scale = scale;
        _reset = reset;
        Spacing = 2;
        _value = Kit.Text(scale.Format(reset), 13, secondary: true);
        _value.HorizontalAlignment = HorizontalAlignment.Right;
        var header = new Grid();
        header.Children.Add(Kit.Text(title, 14));
        header.Children.Add(_value);
        Slider = new Slider
        {
            Minimum = scale.Min,
            Maximum = scale.Max,
            StepFrequency = 1,
            SmallChange = 1,
            LargeChange = 2,
            SnapsTo = SliderSnapsTo.StepValues,
            TickFrequency = scale.Min < 0 ? -scale.Min : 0,
            TickPlacement = scale.Min < 0 ? TickPlacement.Outside : TickPlacement.None,
            IsThumbToolTipEnabled = false,
            Value = reset,
        };
        ToolTipService.SetToolTip(Slider, "双击复位");
        Slider.ValueChanged += (_, e) =>
        {
            int step = (int)Math.Round(e.NewValue);
            if (!_setting && _dragging && (int)Math.Round(e.OldValue) == 0 && Math.Abs(step) == 1 && NearZero())
            {
                Set(0);
                return;
            }
            _value.Text = _scale.Format(step);
            if (!_setting) changed(step);
        };
        // The slider handles pointer input itself: listen to handled events.
        Slider.AddHandler(PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            _dragging = true;
            _pointerX = e.GetCurrentPoint(Slider).Position.X;
        }), true);
        Slider.AddHandler(PointerMovedEvent, new PointerEventHandler((_, e) => _pointerX = e.GetCurrentPoint(Slider).Position.X), true);
        foreach (var ev in new[] { PointerReleasedEvent, PointerCaptureLostEvent, PointerCanceledEvent })
        {
            Slider.AddHandler(ev, new PointerEventHandler((_, _) => _dragging = false), true);
        }
        Slider.AddHandler(DoubleTappedEvent, new DoubleTappedEventHandler((_, _) => Slider.Value = _reset), true);
        Children.Add(header);
        Children.Add(Slider);
    }

    /// Is the pointer within one full step of 0? The slider itself moves
    /// on at half a step.
    private bool NearZero()
    {
        const double thumb = 20;
        double track = Slider.ActualWidth - thumb;
        if (track <= 0) return false;
        double perStep = track / (_scale.Max - _scale.Min);
        double zeroX = thumb / 2 + (0 - _scale.Min) * perStep;
        return Math.Abs(_pointerX - zeroX) < perStep;
    }

    internal Slider Slider { get; }

    internal int Step => (int)Math.Round(Slider.Value);

    /// Sets the value without reporting it as a user change.
    internal void Set(int step)
    {
        _setting = true;
        Slider.Value = step;
        _value.Text = _scale.Format(step);
        _setting = false;
    }

    /// Sets from a core value, rounded to the nearest step.
    internal void SetValue(double value) => Set(_scale.ToStep(value));
}

/// <summary>Groups of the core's adjustments as the panels show them.</summary>
internal static class Scales
{
    internal static readonly (string Title, string Glyph, string[] Keys)[] Groups =
    {
        ("光线", Kit.G.Sun, new[] { "exposure", "contrast", "highlights", "shadows" }),
        ("颜色", Kit.G.Color, new[] { "temperature", "tint", "saturation" }),
        ("效果", Kit.G.Effects, new[] { "clarity", "sharpen", "fade", "vignette", "grain" }),
    };

    /// The adjustments of each group; ones a newer core adds go last.
    internal static List<(string Title, string Glyph, List<Adjustment> Items)> Grouped()
    {
        var result = new List<(string, string, List<Adjustment>)>();
        var placed = new HashSet<string>();
        foreach (var (title, glyph, keys) in Groups)
        {
            var items = keys.Where(k => Adjustment.All.Any(a => a.Key == k)).Select(Adjustment.ByKey).ToList();
            placed.UnionWith(keys);
            result.Add((title, glyph, items));
        }
        result[^1].Item3.AddRange(Adjustment.All.Where(a => !placed.Contains(a.Key)));
        return result;
    }
}

/// <summary>Base for panels that edit a session and follow its changes.</summary>
internal abstract class SessionPanel : StackPanel
{
    private bool _applying;

    protected SessionPanel(EditSession session)
    {
        Session = session;
        Session.Changed += _ =>
        {
            if (!_applying) Sync();
        };
    }

    protected EditSession Session { get; }

    /// Applies a change made in this panel without syncing the panel back.
    protected void Apply(EditState state, bool continuous)
    {
        _applying = true;
        try
        {
            Session.Apply(state, continuous);
        }
        finally
        {
            _applying = false;
        }
    }

    /// Puts the controls in step with the session.
    internal abstract void Sync();
}

/// <summary>
/// The filters, each previewed on the photo as currently edited, plus the
/// strength of the chosen one.
/// </summary>
internal sealed class FilterPanel : SessionPanel
{
    private readonly List<(Filter Filter, Button Tile, Image Image, Border Frame)> _tiles = new();
    private readonly SliderRow _strength;
    private EditState? _thumbsFor;
    private int _tileSize;

    /// horizontal: one scrolling row (a strip under the photo); otherwise
    /// a wrapping grid for a side panel.
    internal FilterPanel(EditSession session, bool horizontal, int tileSize = 84) : base(session)
    {
        _tileSize = tileSize;
        Spacing = 12;
        Panel tiles = horizontal
            ? new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }
            : new VariableSizedWrapGrid
            {
                Orientation = Orientation.Horizontal,
                ItemWidth = tileSize + 12,
                ItemHeight = tileSize + 34,
            };
        foreach (var filter in Filter.All)
        {
            var image = new Image { Stretch = Stretch.UniformToFill, Width = tileSize, Height = tileSize };
            var frame = new Border
            {
                Child = image,
                Width = tileSize + 6,
                Height = tileSize + 6,
                Padding = new Thickness(2),
                BorderThickness = new Thickness(2),
                BorderBrush = new SolidColorBrush(Colors.Transparent),
                Background = Kit.Tile,
            };
            var label = Kit.Text(filter.Title, 12);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            var stack = new StackPanel { Spacing = 4 };
            stack.Children.Add(frame);
            stack.Children.Add(label);
            var tile = new Button
            {
                Content = stack,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
            };
            AutomationName(tile, filter.Title);
            var f = filter;
            tile.Click += (_, _) => Choose(f.Id);
            _tiles.Add((filter, tile, image, frame));
            tiles.Children.Add(tile);
        }
        if (horizontal)
        {
            Children.Add(new ScrollViewer
            {
                Content = tiles,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollMode = ScrollMode.Enabled,
                VerticalScrollMode = ScrollMode.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(0, 0, 0, 6),
            });
        }
        else
        {
            Children.Add(tiles);
        }
        _strength = new SliderRow("强度", Steps.FilterStrength,
            v => Apply(Session.State.WithFilter(Session.State.Filter, Steps.FilterStrength.ToValue(v)), continuous: true), reset: 10);
        Children.Add(_strength);
        Sync();
    }

    private static void AutomationName(UIElement e, string name) =>
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(e, name);

    internal void Choose(string id)
    {
        if (id == Session.State.Filter) return;
        Session.Apply(Session.State.WithFilter(id, 1f));
    }

    internal override void Sync()
    {
        foreach (var (filter, _, _, frame) in _tiles)
        {
            frame.BorderBrush = filter.Id == Session.State.Filter
                ? Kit.AccentBrush
                : new SolidColorBrush(Colors.Transparent);
        }
        _strength.Visibility = Session.State.Filter.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _strength.SetValue(Session.State.Strength);
    }

    /// Renders every filter on the photo as it is now edited (skipped when
    /// only the filter itself changed). Hosts call it when the panel comes
    /// into view, not on every change, so previews stay first in line.
    internal async Task RefreshThumbnailsAsync()
    {
        var source = Session.Source;
        if (source is null) return;
        var basis = Session.State.WithFilter("", 1f);
        if (basis.Equals(_thumbsFor)) return;
        _thumbsFor = basis;
        double scale = XamlRoot?.RasterizationScale ?? 1.0;
        int box = (int)Math.Ceiling(_tileSize * scale);
        var (fw, fh) = Session.OutputSize(basis.Geometry);
        // Fill the square tile: the short side matches it.
        double k = Math.Min(1.0, (double)box / Math.Min(fw, fh));
        int w = Math.Max(1, (int)Math.Round(fw * k)), h = Math.Max(1, (int)Math.Round(fh * k));
        var filters = _tiles.Select(t => t.Filter.Id).ToList();
        var frames = await App.Worker.RunAsync(r =>
            filters.Select(id => RenderWorker.RenderNow(r, source, basis.WithFilter(id, 1f), w, h)).ToList());
        if (!basis.Equals(_thumbsFor)) return;  // a newer refresh is on its way
        for (int i = 0; i < frames.Count && i < _tiles.Count; i++)
        {
            if (frames[i] is { } frame) _tiles[i].Image.Source = ToBitmap(frame);
        }
    }

    internal static WriteableBitmap ToBitmap(RenderedFrame frame)
    {
        var bitmap = new WriteableBitmap(frame.Width, frame.Height);
        using (var stream = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsStream(bitmap.PixelBuffer))
        {
            stream.Write(frame.Bgra, 0, frame.Bgra.Length);
        }
        bitmap.Invalidate();
        return bitmap;
    }
}

/// <summary>All adjustments as sliders, grouped into light, colour and effects.</summary>
internal sealed class AdjustPanel : SessionPanel
{
    private readonly Dictionary<int, SliderRow> _rows = new();

    /// columns: each group side by side (a wide tray under the photo).
    internal AdjustPanel(EditSession session, bool columns = false) : base(session)
    {
        Spacing = columns ? 28 : 6;
        if (columns) Orientation = Orientation.Horizontal;
        foreach (var (title, _, items) in Scales.Grouped())
        {
            if (items.Count == 0) continue;
            Panel target = this;
            if (columns)
            {
                var column = new StackPanel { Spacing = 2, Width = 260 };
                Children.Add(column);
                target = column;
            }
            var header = Kit.Strong(title, 15);
            header.Margin = new Thickness(0, target.Children.Count == 0 ? 0 : 14, 0, 2);
            target.Children.Add(header);
            foreach (var a in items)
            {
                var scale = Steps.For(a);
                int index = a.Index;
                var row = new SliderRow(a.Title, scale,
                    v => Apply(Session.State.WithValue(index, scale.ToValue(v)), continuous: true));
                _rows[index] = row;
                target.Children.Add(row);
            }
        }
        var reset = Kit.TextButton("全部归零", () => Session.Apply(Session.State.WithoutAdjustments()));
        reset.Margin = new Thickness(0, 12, 0, 0);
        reset.VerticalAlignment = VerticalAlignment.Top;
        Children.Add(reset);
        Sync();
    }

    internal SliderRow Row(string key) => _rows[Adjustment.ByKey(key).Index];

    internal override void Sync()
    {
        foreach (var (index, row) in _rows) row.SetValue(Session.State.Values[index]);
    }
}

/// <summary>Rotate, flip, crop ratios, straighten and perspective.</summary>
internal sealed class CropPanel : SessionPanel
{
    private readonly List<(double Ratio, ToggleButton Button)> _ratios = new();
    private readonly SliderRow _straighten, _vertical, _horizontal;

    /// columns: buttons and ratios beside the sliders (a wide tray).
    internal CropPanel(EditSession session, bool columns = false) : base(session)
    {
        Spacing = columns ? 32 : 14;
        if (columns) Orientation = Orientation.Horizontal;
        var first = columns ? new StackPanel { Spacing = 14, Width = 330 } : (StackPanel)this;
        var second = columns ? new StackPanel { Spacing = 6, Width = 300 } : (StackPanel)this;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var left = Kit.IconButton(Kit.G.Rotate, "向左旋转 90°", () => Session.Turn(clockwise: false));
        left.Content = new FontIcon
        {
            Glyph = Kit.G.Rotate,
            FontSize = 16,
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
            RenderTransform = new ScaleTransform { ScaleX = -1 },
        };
        buttons.Children.Add(left);
        buttons.Children.Add(Kit.IconButton(Kit.G.Rotate, "向右旋转 90°", () => Session.Turn(clockwise: true)));
        buttons.Children.Add(Kit.IconButton(Kit.G.Flip, "水平翻转", Session.Flip));
        buttons.Children.Add(Kit.TextButton("重置", Session.ResetGeometry));
        first.Children.Add(buttons);

        first.Children.Add(Kit.Strong("比例", 15));
        var ratios = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemWidth = 74, ItemHeight = 40 };
        foreach (var r in CropMath.Ratios)
        {
            var b = new ToggleButton { Content = r.Title, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 4, 4) };
            double value = r.Value;
            b.Click += (_, _) =>
            {
                Session.SetRatio(value);
                SyncRatios();
            };
            _ratios.Add((value, b));
            ratios.Children.Add(b);
        }
        first.Children.Add(ratios);
        first.Children.Add(Kit.Text("拖动照片上的边框和四角调整裁剪范围。", 12, secondary: true));

        var perspective = Steps.Perspective;
        _straighten = new SliderRow("拉直", Steps.Straighten,
            v => SetGeometry(g => { g.Straighten = Steps.Straighten.ToValue(v); return g; }));
        _vertical = new SliderRow("垂直透视", perspective,
            v => SetGeometry(g => { g.Vertical = perspective.ToValue(v); return g; }));
        _horizontal = new SliderRow("水平透视", perspective,
            v => SetGeometry(g => { g.Horizontal = perspective.ToValue(v); return g; }));
        second.Children.Add(_straighten);
        second.Children.Add(_vertical);
        second.Children.Add(_horizontal);
        if (columns)
        {
            Children.Add(first);
            Children.Add(second);
        }
        Sync();
    }

    private void SetGeometry(Func<Geometry, Geometry> change) =>
        Apply(Session.State.WithGeometry(change(Session.State.Geometry)), continuous: true);

    private void SyncRatios()
    {
        foreach (var (ratio, button) in _ratios) button.IsChecked = ratio == Session.Ratio;
    }

    internal override void Sync()
    {
        var g = Session.State.Geometry;
        _straighten.SetValue(g.Straighten);
        _vertical.SetValue(g.Vertical);
        _horizontal.SetValue(g.Horizontal);
        SyncRatios();
    }
}

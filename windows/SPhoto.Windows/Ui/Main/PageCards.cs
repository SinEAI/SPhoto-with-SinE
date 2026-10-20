using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SPhoto.Library;
using SPhoto.Services;
using SPhoto.Views;
using Windows.Foundation;
using Windows.UI;

namespace SPhoto.Ui.Main;

/// <summary>
/// The cards of a page laid out by CardLayout, like home screen widgets.
/// The cells widen so the columns always fill the width: no gap on the
/// right while a window is made wider until another column fits.
/// </summary>
internal sealed class CardWall : Panel
{
    /// The narrowest a cell gets before a column is dropped.
    internal const double MinCell = 196;
    internal const double Gap = 12;
    /// Under each cover: the tag's name and count.
    internal const double LabelHeight = 52;
    /// A cell's cover is 4:3.
    private const double CoverAspect = 0.75;

    private readonly Dictionary<UIElement, CardSize> _sizes = new();
    private readonly Dictionary<UIElement, Rect> _placed = new();

    internal void Add(UIElement card, CardSize size)
    {
        _sizes[card] = size;
        Children.Add(card);
    }

    internal void Clear()
    {
        _sizes.Clear();
        _placed.Clear();
        Children.Clear();
    }

    /// The columns and rows a card takes now (for the UI test).
    internal (int Columns, int Rows) SpanOf(UIElement card) =>
        CardLayout.Span(_sizes.GetValueOrDefault(card), Columns);

    internal int Columns { get; private set; } = 1;

    /// The room left on the right of the cards (for the UI test).
    internal double Slack { get; private set; }

    protected override Size MeasureOverride(Size available)
    {
        double width = double.IsInfinity(available.Width) ? MinCell * 4 + Gap * 3 : available.Width;
        double scale = XamlRoot?.RasterizationScale ?? 1;
        var (columns, cell) = CardLayout.Fit(width, MinCell, Gap, scale);
        double row = Math.Round((cell * CoverAspect + LabelHeight) * scale) / scale;
        Columns = columns;
        Slack = width - (cell * columns + Gap * (columns - 1));
        var cards = Children.ToList();
        var places = CardLayout.Pack(cards.Select(c => _sizes.GetValueOrDefault(c)).ToList(), columns);
        _placed.Clear();
        int rows = 0;
        for (int i = 0; i < cards.Count; i++)
        {
            var (x, y, c, r) = places[i];
            var rect = new Rect(x * (cell + Gap), y * (row + Gap), c * cell + (c - 1) * Gap, r * row + (r - 1) * Gap);
            _placed[cards[i]] = rect;
            cards[i].Measure(new Size(rect.Width, rect.Height));
            rows = Math.Max(rows, y + r);
        }
        return new Size(width, rows == 0 ? 0 : rows * row + (rows - 1) * Gap);
    }

    protected override Size ArrangeOverride(Size final)
    {
        foreach (var card in Children)
        {
            card.Arrange(_placed.TryGetValue(card, out var rect) ? rect : default);
        }
        return final;
    }
}

/// <summary>
/// The standard components a tag's card can show its photos in. Each
/// draws its own random arrangement of photos picked at random from the
/// tag; Auto picks a component for the card each time the page is shown.
/// All of them keep to the same white seams, edges and cell sizes, so a
/// page looks put together whatever the user mixes.
/// </summary>
internal static class CardStyles
{
    internal static readonly IReadOnlyList<(CardStyle Style, string Title)> All = new[]
    {
        (CardStyle.Auto, "随机样式"),
        (CardStyle.Slant, "斜切拼贴"),
        (CardStyle.Feature, "大图"),
        (CardStyle.Mosaic, "方格拼贴"),
        (CardStyle.Scatter, "散落相片"),
        (CardStyle.Slideshow, "轮播"),
    };

    internal static string TitleOf(CardStyle style) => All.First(s => s.Style == style).Title;

    /// <summary>
    /// What Auto becomes for a card: any component that suits the size
    /// (scattered prints need more than one cell), not the one the card
    /// before it got, so neighbours differ. motion: whether a slideshow may
    /// be among them.
    /// </summary>
    internal static CardStyle Pick(CardSize size, int photos, CardStyle previous, Random random, bool motion = true)
    {
        var choices = new List<CardStyle> { CardStyle.Slant, CardStyle.Feature, CardStyle.Mosaic };
        if (motion) choices.Add(CardStyle.Slideshow);
        if (size != CardSize.Small) choices.Add(CardStyle.Scatter);
        if (photos < 2) choices.RemoveAll(s => s is CardStyle.Mosaic or CardStyle.Scatter or CardStyle.Slideshow);
        if (choices.Count > 1) choices.Remove(previous);
        return choices[random.Next(choices.Count)];
    }

    /// How many photos a component wants at a size.
    internal static int Wants(CardStyle style, CardSize size) => style switch
    {
        CardStyle.Slideshow => 12,
        CardStyle.Scatter => size switch { CardSize.Medium => 3, CardSize.Large => 4, CardSize.ExtraLarge => 5, _ => 2 },
        CardStyle.Mosaic => size switch { CardSize.Small => 3, CardSize.Medium => 4, CardSize.Large => 6, _ => 7 },
        CardStyle.Feature => size switch { CardSize.Small => 1, CardSize.Large => 4, CardSize.ExtraLarge => 5, _ => 3 },
        _ => 4,
    };

    /// A random handful of photos, in random order.
    internal static List<PhotoEntry> Sample(IReadOnlyList<PhotoEntry> photos, int count, Random random)
    {
        var list = photos.ToList();
        for (int i = 0; i < Math.Min(count, list.Count); i++)
        {
            int j = random.Next(i, list.Count);
            (list[i], list[j]) = (list[j], list[i]);
        }
        return list.Take(count).ToList();
    }

    internal static CardCover Create(CardStyle style, CardSize size, IReadOnlyList<PhotoEntry> photos, Random random) => style switch
    {
        CardStyle.Feature => new FeatureCover(photos, size, random),
        CardStyle.Mosaic => new MosaicCover(photos, size, random),
        CardStyle.Scatter => new ScatterCover(photos, size, random),
        CardStyle.Slideshow => new SlideshowCover(photos, random),
        _ => new SlantCover(photos, size, random),
    };
}

/// <summary>
/// A card's cover. Each component decides its arrangement once, in unit
/// coordinates, and lays it out again whenever the card changes size.
/// </summary>
internal abstract class CardCover : Grid
{
    internal const double Seam = 3;

    protected CardCover()
    {
        Background = Kit.Tile;
        SizeChanged += (_, e) =>
        {
            if (e.NewSize.Width <= 0 || e.NewSize.Height <= 0) return;
            Clip = new RectangleGeometry { Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
            Layout(e.NewSize.Width, e.NewSize.Height);
        };
    }

    protected abstract void Layout(double width, double height);

    /// Which component this is (for the UI test).
    internal abstract CardStyle Component { get; }

    /// A photo filling an area, cropped to it.
    protected static Image Picture() => new() { Stretch = Stretch.UniformToFill };

    /// Puts a photo into a rectangle of the canvas.
    protected static void Place(FrameworkElement e, double x, double y, double w, double h)
    {
        Canvas.SetLeft(e, x);
        Canvas.SetTop(e, y);
        e.Width = Math.Max(0, w);
        e.Height = Math.Max(0, h);
    }
}

/// <summary>
/// Shows a photo in an image or brush: its thumbnail at once, then, when the
/// photo is shown larger than a thumbnail, a sharper decode of the photo.
/// </summary>
internal static class CoverPhotos
{
    private sealed class Shown
    {
        internal string Path = "";
        internal int Side;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, Shown> State = new();

    // A few sharp decodes kept for a page shown again or resized.
    private static readonly LinkedList<(string Key, ImageSource Source)> Sharp = new();
    private const int SharpCount = 8;

    /// side: the longer side the photo is shown at, in view pixels.
    internal static void Show(Image image, string path, double side, double scale) =>
        _ = ShowAsync(image, s => image.Source = s, path, side, scale);

    internal static void Show(ImageBrush brush, string path, double side, double scale) =>
        _ = ShowAsync(brush, s => brush.ImageSource = s, path, side, scale);

    private static async Task ShowAsync(object target, Action<ImageSource?> set, string path, double side, double scale)
    {
        var shown = State.GetOrCreateValue(target);
        int pixels = (int)Math.Ceiling(side * scale);
        // Sharp copies come in steps, so a resize does not decode every frame.
        int wanted = pixels <= (int)(ThumbnailCache.Side * 1.15) ? 0 : Math.Min(1600, (pixels + 255) / 256 * 256);
        if (shown.Path == path && shown.Side >= wanted) return;
        bool first = shown.Path != path;
        shown.Path = path;
        if (first)
        {
            shown.Side = 0;
            set(Thumbnails.Recent(path));
            var thumb = await Thumbnails.LoadAsync(path);
            if (shown.Path != path) return;
            if (shown.Side == 0) set(thumb);
        }
        if (wanted <= shown.Side) return;
        shown.Side = wanted;
        var key = $"{path.ToLowerInvariant()}|{wanted}";
        var cached = Sharp.FirstOrDefault(s => s.Key == key);
        if (cached.Source is not null)
        {
            set(cached.Source);
            return;
        }
        try
        {
            var decoded = await Task.Run(() => ImageIO.DecodeAsync(path, wanted, bgra: true));
            var source = await Thumbnails.ToSourceAsync(decoded);
            Sharp.AddFirst((key, source));
            while (Sharp.Count > SharpCount) Sharp.RemoveLast();
            if (shown.Path == path && shown.Side == wanted) set(source);
        }
        catch (Exception)
        {
            // The thumbnail stays; a codec that cannot decode the photo made it.
        }
    }

    internal static double ScaleOf(FrameworkElement e) => e.XamlRoot?.RasterizationScale ?? 1;
}

/// <summary>
/// 斜切拼贴: photos cut into slanted pieces with thin white seams. A cut
/// from top to bottom, and for three or four pieces a gentler cut across.
/// </summary>
internal sealed class SlantCover : CardCover
{
    private readonly List<(Microsoft.UI.Xaml.Shapes.Path Shape, ImageBrush Brush, string Photo)> _pieces = new();
    private readonly List<Point[]> _shapes;

    internal SlantCover(IReadOnlyList<PhotoEntry> photos, CardSize size, Random random)
    {
        int most = size == CardSize.Small ? 3 : 4;
        int pieces = photos.Count switch
        {
            0 => 0,
            1 => 1,
            2 => 2,
            _ => 2 + random.Next(Math.Min(photos.Count, most) - 1),
        };
        _shapes = pieces == 0 ? new List<Point[]>() : Shapes(pieces, random);
        var canvas = new Canvas();
        for (int i = 0; i < _shapes.Count; i++)
        {
            var brush = new ImageBrush { Stretch = Stretch.UniformToFill };
            var shape = new Microsoft.UI.Xaml.Shapes.Path
            {
                Fill = brush,
                Stroke = Kit.White,
                StrokeThickness = pieces > 1 ? Seam : 0,
            };
            canvas.Children.Add(shape);
            _pieces.Add((shape, brush, photos[i].Path));
        }
        Children.Add(canvas);
    }

    internal override CardStyle Component => CardStyle.Slant;

    protected override void Layout(double width, double height)
    {
        double scale = CoverPhotos.ScaleOf(this);
        for (int i = 0; i < _pieces.Count; i++)
        {
            var points = _shapes[i];
            var figure = new PathFigure { IsClosed = true, StartPoint = new Point(points[0].X * width, points[0].Y * height) };
            foreach (var p in points.Skip(1)) figure.Segments.Add(new LineSegment { Point = new Point(p.X * width, p.Y * height) });
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            _pieces[i].Shape.Data = geometry;
            double w = (points.Max(p => p.X) - points.Min(p => p.X)) * width, h = (points.Max(p => p.Y) - points.Min(p => p.Y)) * height;
            CoverPhotos.Show(_pieces[i].Brush, _pieces[i].Photo, Math.Max(w, h), scale);
        }
    }

    /// <summary>
    /// Polygons in unit coordinates. A slanted cut from top to bottom
    /// (a at the top, b at the bottom); for three or four pieces a second,
    /// gentler cut across from left (c) to right (d).
    /// </summary>
    private static List<Point[]> Shapes(int pieces, Random random)
    {
        double Between(double lo, double hi) => lo + random.NextDouble() * (hi - lo);
        if (pieces <= 1) return new List<Point[]> { new[] { new Point(0, 0), new Point(1, 0), new Point(1, 1), new Point(0, 1) } };

        bool leanLeft = random.Next(2) == 0;
        double a = leanLeft ? Between(0.55, 0.72) : Between(0.30, 0.45);
        double b = leanLeft ? Between(0.30, 0.45) : Between(0.55, 0.72);
        if (pieces == 2)
        {
            return new List<Point[]>
            {
                new[] { new Point(0, 0), new Point(a, 0), new Point(b, 1), new Point(0, 1) },
                new[] { new Point(a, 0), new Point(1, 0), new Point(1, 1), new Point(b, 1) },
            };
        }

        double c = Between(0.40, 0.62), d = Between(0.40, 0.62);
        // Where the cut across meets the slanted one.
        double x = (a + (b - a) * c) / (1 - (b - a) * (d - c));
        var p = new Point(x, c + (d - c) * x);
        if (pieces == 3)
        {
            // One tall piece on one side, the other half cut across.
            bool tallLeft = random.Next(2) == 0;
            return tallLeft
                ? new List<Point[]>
                {
                    new[] { new Point(0, 0), new Point(a, 0), new Point(b, 1), new Point(0, 1) },
                    new[] { new Point(a, 0), new Point(1, 0), new Point(1, d), p },
                    new[] { p, new Point(1, d), new Point(1, 1), new Point(b, 1) },
                }
                : new List<Point[]>
                {
                    new[] { new Point(a, 0), new Point(1, 0), new Point(1, 1), new Point(b, 1) },
                    new[] { new Point(0, 0), new Point(a, 0), p, new Point(0, c) },
                    new[] { new Point(0, c), p, new Point(b, 1), new Point(0, 1) },
                };
        }
        return new List<Point[]>
        {
            new[] { new Point(0, 0), new Point(a, 0), p, new Point(0, c) },
            new[] { new Point(a, 0), new Point(1, 0), new Point(1, d), p },
            new[] { new Point(0, c), p, new Point(b, 1), new Point(0, 1) },
            new[] { p, new Point(1, d), new Point(1, 1), new Point(b, 1) },
        };
    }
}

/// <summary>
/// 大图: one photo takes most of the card; on cards bigger than a cell a
/// few more line up in a strip along one side.
/// </summary>
internal sealed class FeatureCover : CardCover
{
    private readonly Canvas _canvas = new();
    private readonly List<(Grid Frame, Image Image, string Photo)> _photos = new();
    /// Which side the strip is on: 0 right, 1 left, 2 bottom.
    private readonly int _side;
    private readonly double _share;

    internal FeatureCover(IReadOnlyList<PhotoEntry> photos, CardSize size, Random random)
    {
        int count = Math.Min(photos.Count, CardStyles.Wants(CardStyle.Feature, size));
        // A small card or a coin toss: the one photo alone.
        if (count > 1 && random.Next(4) == 0) count = 1;
        _side = size == CardSize.Large ? 2 : random.Next(2);
        _share = 0.62 + random.NextDouble() * 0.1;
        foreach (var p in photos.Take(count))
        {
            var image = Picture();
            var frame = new Grid { Background = Kit.Tile };
            frame.Children.Add(image);
            _canvas.Children.Add(frame);
            _photos.Add((frame, image, p.Path));
        }
        Children.Add(_canvas);
    }

    internal override CardStyle Component => CardStyle.Feature;

    protected override void Layout(double width, double height)
    {
        if (_photos.Count == 0) return;
        double scale = CoverPhotos.ScaleOf(this);
        void Put(int i, double x, double y, double w, double h)
        {
            Place(_photos[i].Frame, x, y, w, h);
            CoverPhotos.Show(_photos[i].Image, _photos[i].Photo, Math.Max(w, h), scale);
        }
        if (_photos.Count == 1)
        {
            Put(0, 0, 0, width, height);
            return;
        }
        int rest = _photos.Count - 1;
        if (_side == 2)
        {
            double main = Math.Round(height * _share);
            Put(0, 0, 0, width, main);
            double w = (width - Seam * (rest - 1)) / rest;
            for (int i = 0; i < rest; i++) Put(i + 1, i * (w + Seam), main + Seam, w, height - main - Seam);
        }
        else
        {
            double main = Math.Round(width * _share);
            double stripX = _side == 0 ? main + Seam : 0;
            Put(0, _side == 0 ? 0 : width - main, 0, main, height);
            double stripW = width - main - Seam;
            // Up to three in the strip in one column; four in two.
            int columns = rest >= 4 ? 2 : 1;
            int rows = (rest + columns - 1) / columns;
            double w = (stripW - Seam * (columns - 1)) / columns, h = (height - Seam * (rows - 1)) / rows;
            for (int i = 0; i < rest; i++)
            {
                int c = i % columns, r = i / columns;
                Put(i + 1, stripX + c * (w + Seam), r * (h + Seam), w, h);
            }
        }
    }
}

/// <summary>
/// 方格拼贴: the card cut again and again into two, along the longer side
/// at a random point, until there is a piece for each photo; straight
/// white seams between them.
/// </summary>
internal sealed class MosaicCover : CardCover
{
    private readonly Canvas _canvas = new();
    private readonly List<(Grid Frame, Image Image, string Photo)> _photos = new();
    private readonly Random _random;
    private List<Rect>? _cells;

    internal MosaicCover(IReadOnlyList<PhotoEntry> photos, CardSize size, Random random)
    {
        _random = random;
        int most = Math.Min(photos.Count, CardStyles.Wants(CardStyle.Mosaic, size));
        int fewest = Math.Min(most, size == CardSize.Small ? 2 : 3);
        int count = fewest + random.Next(most - fewest + 1);
        foreach (var p in photos.Take(count))
        {
            var image = Picture();
            var frame = new Grid { Background = Kit.Tile };
            frame.Children.Add(image);
            _canvas.Children.Add(frame);
            _photos.Add((frame, image, p.Path));
        }
        Children.Add(_canvas);
    }

    internal override CardStyle Component => CardStyle.Mosaic;

    protected override void Layout(double width, double height)
    {
        // Cut once, at the first size, in proportions; resizing keeps the cut.
        _cells ??= Cut(_photos.Count, width / Math.Max(1, height));
        double scale = CoverPhotos.ScaleOf(this);
        for (int i = 0; i < _photos.Count; i++)
        {
            var c = _cells[i];
            // Seams: half on each side of every inner edge.
            double x0 = c.X * width + (c.X > 0 ? Seam / 2 : 0), y0 = c.Y * height + (c.Y > 0 ? Seam / 2 : 0);
            double x1 = (c.X + c.Width) * width - (c.X + c.Width < 0.999 ? Seam / 2 : 0);
            double y1 = (c.Y + c.Height) * height - (c.Y + c.Height < 0.999 ? Seam / 2 : 0);
            Place(_photos[i].Frame, x0, y0, x1 - x0, y1 - y0);
            CoverPhotos.Show(_photos[i].Image, _photos[i].Photo, Math.Max(x1 - x0, y1 - y0), scale);
        }
    }

    /// Unit rectangles: the biggest piece is cut in two until there are count.
    private List<Rect> Cut(int count, double aspect)
    {
        var cells = new List<Rect> { new(0, 0, 1, 1) };
        while (cells.Count < count)
        {
            int at = 0;
            for (int i = 1; i < cells.Count; i++)
                if (cells[i].Width * cells[i].Height > cells[at].Width * cells[at].Height) at = i;
            var r = cells[at];
            double t = 0.38 + _random.NextDouble() * 0.24;
            cells.RemoveAt(at);
            if (r.Width * aspect >= r.Height)
            {
                cells.Insert(at, new Rect(r.X, r.Y, r.Width * t, r.Height));
                cells.Insert(at + 1, new Rect(r.X + r.Width * t, r.Y, r.Width * (1 - t), r.Height));
            }
            else
            {
                cells.Insert(at, new Rect(r.X, r.Y, r.Width, r.Height * t));
                cells.Insert(at + 1, new Rect(r.X, r.Y + r.Height * t, r.Width, r.Height * (1 - t)));
            }
        }
        return cells;
    }
}

/// <summary>
/// 散落相片: white-bordered prints dropped on a table, each turned its own
/// way, spread so every one still shows.
/// </summary>
internal sealed class ScatterCover : CardCover
{
    private static readonly SolidColorBrush Table = new(Color.FromArgb(255, 0xF1, 0xEE, 0xE9));
    private static readonly SolidColorBrush Shade = new(Color.FromArgb(0x30, 0, 0, 0));

    private readonly Canvas _canvas = new();
    private readonly List<(Border Shadow, Border Print, Image Image, string Photo, Point At, double Turn)> _prints = new();
    private readonly double _size;

    internal ScatterCover(IReadOnlyList<PhotoEntry> photos, CardSize size, Random random)
    {
        Background = Table;
        int count = Math.Min(photos.Count, CardStyles.Wants(CardStyle.Scatter, size));
        // Two rows on a large card, one row otherwise.
        int rows = size == CardSize.Large && count > 2 ? 2 : 1;
        int columns = (count + rows - 1) / rows;
        _size = rows == 2 ? 0.46 : 0.7;
        for (int i = 0; i < count; i++)
        {
            int r = i / columns, c = i % columns;
            int inRow = r == rows - 1 ? count - columns * (rows - 1) : columns;
            var at = new Point(
                (c + 0.5) / inRow + (random.NextDouble() - 0.5) * 0.12 / inRow,
                (r + 0.5) / rows + (random.NextDouble() - 0.5) * 0.14 / rows);
            double turn = (3 + random.NextDouble() * 9) * (random.Next(2) == 0 ? -1 : 1);
            var image = Picture();
            var print = new Border
            {
                Background = Kit.White,
                Child = image,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform { Angle = turn },
            };
            var shadow = new Border
            {
                Background = Shade,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform { Angle = turn },
            };
            _prints.Add((shadow, print, image, photos[i].Path, at, turn));
        }
        // Dropped in a random order: any print may lie on top.
        foreach (var p in _prints.OrderBy(_ => random.Next()))
        {
            _canvas.Children.Add(p.Shadow);
            _canvas.Children.Add(p.Print);
        }
        Children.Add(_canvas);
    }

    internal override CardStyle Component => CardStyle.Scatter;

    protected override void Layout(double width, double height)
    {
        double scale = CoverPhotos.ScaleOf(this);
        double h = height * _size;
        // A print a little narrower than tall, but never wider than its share of the row.
        double w = Math.Min(h * 0.86, width / Math.Max(1, _prints.Count) * 1.25);
        h = Math.Min(h, w / 0.86);
        double border = Math.Max(3, w * 0.05);
        foreach (var p in _prints)
        {
            double x = p.At.X * width - w / 2, y = p.At.Y * height - h / 2;
            Place(p.Print, x, y, w, h);
            p.Print.Padding = new Thickness(border, border, border, border * 3);
            Place(p.Shadow, x + 2, y + 3, w, h);
            CoverPhotos.Show(p.Image, p.Photo, w, scale);
        }
    }
}

/// <summary>
/// 轮播: one photo at a time, the next fading in every few seconds, in a
/// random order. Still when Windows' animations are off.
/// </summary>
internal sealed class SlideshowCover : CardCover
{
    private readonly Image _back = Picture(), _front = Picture();
    private readonly List<string> _photos;
    private readonly Random _random;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _timer;
    private int _at;
    private double _side;

    internal SlideshowCover(IReadOnlyList<PhotoEntry> photos, Random random)
    {
        _random = random;
        _photos = photos.Select(p => p.Path).ToList();
        Children.Add(_back);
        Children.Add(_front);
        Loaded += (_, _) => Start();
        Unloaded += (_, _) => Stop();
    }

    internal override CardStyle Component => CardStyle.Slideshow;

    protected override void Layout(double width, double height)
    {
        _side = Math.Max(width, height);
        if (_photos.Count > 0) CoverPhotos.Show(_front, _photos[_at], _side, CoverPhotos.ScaleOf(this));
    }

    private void Start()
    {
        if (_photos.Count < 2 || !Motion.Enabled || _timer is not null) return;
        _timer = DispatcherQueue.CreateTimer();
        // Cards on a page do not all change at once.
        _timer.Interval = TimeSpan.FromMilliseconds(4000 + _random.Next(2500));
        _timer.Tick += (_, _) => Next();
        _timer.Start();
    }

    private void Stop()
    {
        _timer?.Stop();
        _timer = null;
    }

    private void Next()
    {
        _at = (_at + 1) % _photos.Count;
        // The shown photo moves behind; the next one fades in over it.
        _back.Source = _front.Source;
        _front.Opacity = 0;
        CoverPhotos.Show(_front, _photos[_at], _side, CoverPhotos.ScaleOf(this));
        Motion.Fade(_front, true, TimeSpan.FromMilliseconds(700), start: 0);
        // The one after is ready by the next tick.
        _ = Thumbnails.LoadAsync(_photos[(_at + 1) % _photos.Count]);
    }

    /// Moves on at once (for the UI test).
    internal void NextForTest() => Next();

    internal string CurrentForTest => _photos.Count > 0 ? _photos[_at] : "";
}

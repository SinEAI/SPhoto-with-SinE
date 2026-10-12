using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SPhoto.Services;
using SPhoto.Views;
using Windows.Foundation;

namespace SPhoto.Ui.Main;

/// <summary>
/// A tag's cover: its newest few photos cut into slanted pieces with thin
/// white seams between them. The cut is random but seeded by the tag and
/// its photos, so a cover stays the same until the tag changes.
/// </summary>
internal sealed class TagCollage : Grid
{
    private const double Seam = 3;

    internal TagCollage(string tag, IReadOnlyList<PhotoEntry> photos, double width, double height)
    {
        Width = width;
        Height = height;
        Background = Kit.Tile;
        Clip = new RectangleGeometry { Rect = new Rect(0, 0, width, height) };
        if (photos.Count == 0) return;

        var random = new Random(Seed(tag, photos));
        int pieces = photos.Count switch
        {
            1 => 1,
            2 => 2,
            3 => random.Next(2) == 0 ? 2 : 3,
            _ => 2 + random.Next(3),
        };
        var canvas = new Canvas { Width = width, Height = height };
        var shapes = Shapes(pieces, random);
        for (int i = 0; i < shapes.Count; i++)
        {
            var brush = new ImageBrush { Stretch = Stretch.UniformToFill };
            var figure = new PathFigure { IsClosed = true, StartPoint = ToPixels(shapes[i][0], width, height) };
            foreach (var p in shapes[i].Skip(1)) figure.Segments.Add(new LineSegment { Point = ToPixels(p, width, height) });
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            canvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Path
            {
                Data = geometry,
                Fill = brush,
                Stroke = Kit.White,
                StrokeThickness = pieces > 1 ? Seam : 0,
            });
            _ = LoadAsync(brush, photos[i].Path);
        }
        Children.Add(canvas);
    }

    private static Point ToPixels(Point p, double w, double h) => new(p.X * w, p.Y * h);

    private static async System.Threading.Tasks.Task LoadAsync(ImageBrush brush, string path) =>
        brush.ImageSource = await Thumbnails.LoadAsync(path);

    private static int Seed(string tag, IReadOnlyList<PhotoEntry> photos)
    {
        int h = 17;
        foreach (char c in tag) h = h * 31 + c;
        foreach (var p in photos) h = h * 31 + StringComparer.OrdinalIgnoreCase.GetHashCode(p.Path);
        return h;
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
            // One tall piece on the left, the right half cut across.
            return new List<Point[]>
            {
                new[] { new Point(0, 0), new Point(a, 0), new Point(b, 1), new Point(0, 1) },
                new[] { new Point(a, 0), new Point(1, 0), new Point(1, d), p },
                new[] { p, new Point(1, d), new Point(1, 1), new Point(b, 1) },
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

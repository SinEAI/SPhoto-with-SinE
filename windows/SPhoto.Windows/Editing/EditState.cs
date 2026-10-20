using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace SPhoto.Editing;

/// <summary>One adjustment of the core, in sp_params order.</summary>
internal sealed record Adjustment(int Index, string Key, string Title, float Min, float Max)
{
    private static readonly Dictionary<string, string> Titles = new()
    {
        ["exposure"] = "曝光", ["contrast"] = "对比度", ["saturation"] = "饱和度",
        ["temperature"] = "色温", ["tint"] = "色调", ["highlights"] = "高光",
        ["shadows"] = "阴影", ["fade"] = "褪色", ["sharpen"] = "锐化",
        ["clarity"] = "清晰度", ["vignette"] = "暗角", ["grain"] = "颗粒",
    };

    /// Every adjustment the core offers, read from the core itself.
    internal static readonly IReadOnlyList<Adjustment> All = Enumerable.Range(0, Native.sp_param_count())
        .Select(i =>
        {
            Native.sp_param_info(i, out var name, out var min, out var max);
            var key = Marshal.PtrToStringAnsi(name) ?? "";
            return new Adjustment(i, key, Titles.GetValueOrDefault(key, key), min, max);
        })
        .ToList();

    internal static Adjustment ByKey(string key) => All.First(a => a.Key == key);
}

/// <summary>A built-in filter of the core.</summary>
internal sealed record Filter(string Id, string Title)
{
    private static readonly Dictionary<string, string> Titles = new()
    {
        ["warm"] = "暖阳", ["film"] = "胶片", ["cool"] = "冷调",
        ["teal_orange"] = "青橙", ["mono"] = "黑白", ["mono_soft"] = "柔和黑白",
    };

    /// "No filter" first, then the core's filters.
    internal static readonly IReadOnlyList<Filter> All = new[] { new Filter("", "原图") }
        .Concat(Enumerable.Range(0, Native.sp_filter_count()).Select(i =>
        {
            var id = Marshal.PtrToStringAnsi(Native.sp_filter_id(i)) ?? "";
            return new Filter(id, Titles.GetValueOrDefault(id, id));
        }))
        .ToList();
}

/// <summary>
/// Everything the user has changed about one photo. Immutable, so the undo
/// history can hold the states themselves.
/// </summary>
internal sealed class EditState : IEquatable<EditState>
{
    private static readonly (float X, float Y)[] NoPoints = Array.Empty<(float, float)>();
    private static readonly float[] NoBands = new float[8];

    internal float[] Values { get; private init; } = new float[Adjustment.All.Count];
    /// Filter id, "" for none.
    internal string Filter { get; private init; } = "";
    internal float Strength { get; private init; } = 1f;
    internal Geometry Geometry { get; private init; }
    /// Tone curves: all channels, red, green, blue. Each is up to 8 points
    /// (x ascending, 0..1); none means a straight line.
    internal (float X, float Y)[][] Curves { get; private init; } = { NoPoints, NoPoints, NoPoints, NoPoints };
    /// Per colour band (red, orange, yellow, green, aqua, blue, purple,
    /// magenta), each -1..+1.
    internal float[] Hue { get; private init; } = NoBands;
    internal float[] Saturation { get; private init; } = NoBands;
    internal float[] Luminance { get; private init; } = NoBands;

    internal static readonly EditState Original = new();

    internal bool IsOriginal => Equals(Original);

    /// <summary>
    /// What a version saved with this state is called at first: its
    /// filter, or 裁剪 / 旋转 when only the framing changed; null leaves
    /// it "版本 N". The user can rename it in the viewer.
    /// </summary>
    internal string? SuggestedName()
    {
        if (Filter.Length > 0) return Editing.Filter.All.FirstOrDefault(f => f.Id == Filter)?.Title;
        bool toned = Values.Any(v => v != 0) || Curves.Any(c => c.Length > 0) ||
                        Hue.Any(v => v != 0) || Saturation.Any(v => v != 0) || Luminance.Any(v => v != 0);
        if (toned || Geometry.Equals(Original.Geometry)) return null;
        return CropMath.Rect(Geometry) != (0, 0, 1, 1) ? "裁剪" : "旋转";
    }

    private EditState Copy(float[]? values = null, string? filter = null, float? strength = null, Geometry? geometry = null,
                           (float X, float Y)[][]? curves = null, float[]? hue = null, float[]? saturation = null,
                           float[]? luminance = null) => new()
    {
        Values = values ?? Values,
        Filter = filter ?? Filter,
        Strength = strength ?? Strength,
        Geometry = geometry ?? Geometry,
        Curves = curves ?? Curves,
        Hue = hue ?? Hue,
        Saturation = saturation ?? Saturation,
        Luminance = luminance ?? Luminance,
    };

    internal EditState WithValue(int index, float value)
    {
        var values = (float[])Values.Clone();
        values[index] = value;
        return Copy(values: values);
    }

    internal EditState WithFilter(string filter, float strength) => Copy(filter: filter, strength: strength);

    internal EditState WithGeometry(Geometry geometry) => Copy(geometry: geometry);

    /// Light, colour and effects back to 0; the filter, crop, curves and
    /// HSL stay (each has its own reset).
    internal EditState WithoutAdjustments() => Copy(values: new float[Adjustment.All.Count]);

    /// A channel's curve (0 all, 1 red, 2 green, 3 blue); points outside
    /// 0..1 are clamped and the list is sorted by x.
    internal EditState WithCurve(int channel, IEnumerable<(float X, float Y)> points)
    {
        var curves = ((float X, float Y)[][])Curves.Clone();
        curves[channel] = points.Select(p => (Math.Clamp(p.X, 0f, 1f), Math.Clamp(p.Y, 0f, 1f)))
                                .OrderBy(p => p.Item1).Take(8).ToArray();
        // Two points on the diagonal at the ends are no curve at all.
        if (curves[channel].All(p => Math.Abs(p.X - p.Y) < 1e-4f)) curves[channel] = NoPoints;
        return Copy(curves: curves);
    }

    internal EditState WithoutCurves() => Copy(curves: new[] { NoPoints, NoPoints, NoPoints, NoPoints });

    /// kind: 0 hue, 1 saturation, 2 luminance.
    internal EditState WithBand(int kind, int band, float value)
    {
        var bands = (float[])(kind == 0 ? Hue : kind == 1 ? Saturation : Luminance).Clone();
        bands[band] = Math.Clamp(value, -1f, 1f);
        return kind == 0 ? Copy(hue: bands) : kind == 1 ? Copy(saturation: bands) : Copy(luminance: bands);
    }

    internal EditState WithoutHsl() => Copy(hue: NoBands, saturation: NoBands, luminance: NoBands);

    internal float Band(int kind, int band) => (kind == 0 ? Hue : kind == 1 ? Saturation : Luminance)[band];

    internal Edit ToNative()
    {
        var adjust = new float[Edit.ParamCount];
        Array.Copy(Values, adjust, Math.Min(Values.Length, adjust.Length));
        var curves = new Curve[4];
        for (int c = 0; c < 4; c++)
        {
            curves[c] = Curve.Identity;
            var points = Curves[c];
            curves[c].Count = points.Length;
            for (int i = 0; i < points.Length; i++)
            {
                curves[c].X[i] = points[i].X;
                curves[c].Y[i] = points[i].Y;
            }
        }
        return new Edit
        {
            Adjust = adjust, Filter = Filter, FilterStrength = Strength, Geometry = Geometry,
            Curves = curves,
            Hsl = new Hsl { Hue = (float[])Hue.Clone(), Saturation = (float[])Saturation.Clone(), Luminance = (float[])Luminance.Clone() },
        };
    }

    public bool Equals(EditState? other) =>
        other is not null && Values.SequenceEqual(other.Values) && Filter == other.Filter &&
        Strength == other.Strength && Geometry.Equals(other.Geometry) &&
        Enumerable.Range(0, 4).All(c => Curves[c].SequenceEqual(other.Curves[c])) &&
        Hue.SequenceEqual(other.Hue) && Saturation.SequenceEqual(other.Saturation) && Luminance.SequenceEqual(other.Luminance);

    public override bool Equals(object? obj) => Equals(obj as EditState);

    public override int GetHashCode() => HashCode.Combine(Filter, Strength, Geometry);
}

/// <summary>Crop rectangles and the aspect ratios the crop tool offers.</summary>
internal static class CropMath
{
    internal sealed record Ratio(string Title, double Value);

    /// Value 0 is free, -1 the photo's own ratio.
    internal static readonly IReadOnlyList<Ratio> Ratios = new Ratio[]
    {
        new("自由", 0), new("原始比例", -1), new("1:1", 1), new("4:5", 4.0 / 5),
        new("3:4", 3.0 / 4), new("4:3", 4.0 / 3), new("16:9", 16.0 / 9), new("9:16", 9.0 / 16),
    };

    internal static (double X, double Y, double W, double H) Rect(Geometry g) =>
        g.CropWidth > 0 && g.CropHeight > 0 ? (g.CropX, g.CropY, g.CropWidth, g.CropHeight) : (0, 0, 1, 1);

    internal static Geometry WithRect(Geometry g, double x, double y, double w, double h)
    {
        bool full = w >= 0.9999 && h >= 0.9999;
        g.CropX = full ? 0 : (float)x;
        g.CropY = full ? 0 : (float)y;
        g.CropWidth = full ? 0 : (float)w;
        g.CropHeight = full ? 0 : (float)h;
        return g;
    }

    /// The largest rect of the given pixel aspect inside the frame of
    /// frameWidth x frameHeight pixels, centred on the current crop.
    internal static Geometry FitRatio(Geometry g, double aspect, int frameWidth, int frameHeight)
    {
        var (x, y, w, h) = Rect(g);
        double frameAspect = (double)frameWidth / frameHeight;
        double nw = 1, nh = 1;
        if (frameAspect > aspect) nw = aspect / frameAspect;
        else nh = frameAspect / aspect;
        double cx = x + w / 2, cy = y + h / 2;
        double nx = Math.Clamp(cx - nw / 2, 0, 1 - nw);
        double ny = Math.Clamp(cy - nh / 2, 0, 1 - nh);
        return WithRect(g, nx, ny, nw, nh);
    }

    /// Turns what the user sees a quarter clockwise (or anticlockwise),
    /// carrying the crop and perspective along. The core applies flip, then
    /// the quarter turns, then perspective and straighten, so a turn of the
    /// result is one more quarter turn with the perspective turned too.
    internal static Geometry Turn(Geometry g, bool clockwise)
    {
        var (x, y, w, h) = Rect(g);
        g.QuarterTurns = (g.QuarterTurns + (clockwise ? 1 : 3)) % 4;
        (g.Horizontal, g.Vertical) = clockwise ? (-g.Vertical, g.Horizontal) : (g.Vertical, -g.Horizontal);
        return clockwise ? WithRect(g, 1 - y - h, x, h, w) : WithRect(g, y, 1 - x - w, h, w);
    }

    /// Mirrors what the user sees left to right. In the core's order that is
    /// a flip of the source, the quarter turns reversed, and the straighten
    /// angle and horizontal perspective negated.
    internal static Geometry Flip(Geometry g)
    {
        var (x, y, w, h) = Rect(g);
        g.FlipHorizontal ^= 1;
        g.QuarterTurns = (4 - g.QuarterTurns) % 4;
        g.Straighten = -g.Straighten;
        g.Horizontal = -g.Horizontal;
        return WithRect(g, 1 - x - w, y, w, h);
    }

    /// The geometry without its crop: the frame the crop tool draws on.
    internal static Geometry Uncropped(Geometry g) => WithRect(g, 0, 0, 1, 1);
}

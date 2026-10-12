using Xunit;

namespace SPhoto.Library.Tests;

public class FingerprintTests
{
    private static byte[] Random(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    [Fact]
    public void SameBytesSameFingerprint()
    {
        using var dir = new TempDir();
        var data = Random(200_000, 1);
        var a = dir.File("a.jpg", data);
        var b = dir.File("sub/b.jpg", data);
        var fp = Fingerprint.Compute(a);
        Assert.Equal(64, fp.Length);
        Assert.Equal(fp.ToLowerInvariant(), fp);
        Assert.Equal(fp, Fingerprint.Compute(a));
        Assert.Equal(fp, Fingerprint.Compute(b));
    }

    [Theory]
    [InlineData(0)]           // head
    [InlineData(32 * 1024 - 1)]
    [InlineData(-1)]          // last byte
    [InlineData(-32 * 1024)]  // first byte of the tail
    public void OneByteAtTheEndsChangesIt(int at)
    {
        var data = Random(200_000, 2);
        var before = Fingerprint.Compute(new MemoryStream(data));
        data[at >= 0 ? at : data.Length + at] ^= 1;
        Assert.NotEqual(before, Fingerprint.Compute(new MemoryStream(data)));
    }

    [Fact]
    public void LengthChangesIt()
    {
        var data = Random(200_000, 3);
        var longer = data.Concat(new byte[] { data[^1] }).ToArray();
        // Same head; the tail shifts by one byte and the length differs.
        Assert.NotEqual(Fingerprint.Compute(new MemoryStream(data)), Fingerprint.Compute(new MemoryStream(longer)));
        // Trailing zero on a zero-padded file: only the length tells them apart.
        var zeros = new byte[100_000];
        Assert.NotEqual(Fingerprint.Compute(new MemoryStream(zeros)), Fingerprint.Compute(new MemoryStream(new byte[100_001])));
    }

    [Fact]
    public void MiddleOfALargeFileIsNotRead()
    {
        var data = Random(200_000, 4);
        var before = Fingerprint.Compute(new MemoryStream(data));
        data[100_000] ^= 1;
        Assert.Equal(before, Fingerprint.Compute(new MemoryStream(data)));
    }

    [Fact]
    public void SmallFileIsHashedWhole()
    {
        var data = Random(50_000, 5);
        var before = Fingerprint.Compute(new MemoryStream(data));
        data[40_000] ^= 1;
        Assert.NotEqual(before, Fingerprint.Compute(new MemoryStream(data)));
    }
}

public class ExifTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReadsOriginalDateAndCamera(bool little)
    {
        var jpeg = TestJpeg.Build(little, make: "SONY", model: "ILCE-7M3", dateTime: "2024:01:01 00:00:00",
                                  original: "2023:05:06 07:08:09");
        var info = Exif.Parse(jpeg);
        Assert.Equal(new DateTime(2023, 5, 6, 7, 8, 9), info.Taken);
        Assert.Equal("SONY ILCE-7M3", info.Camera);
    }

    [Fact]
    public void FallsBackToDateTime()
    {
        var info = Exif.Parse(TestJpeg.Build(false, dateTime: "2020:02:03 04:05:06"));
        Assert.Equal(new DateTime(2020, 2, 3, 4, 5, 6), info.Taken);
        Assert.Null(info.Camera);
    }

    [Theory]
    [InlineData("Canon", "Canon EOS R5", "Canon EOS R5")]
    [InlineData("NIKON CORPORATION", "NIKON D750", "NIKON D750")]
    [InlineData("Apple", "iPhone 15 Pro", "Apple iPhone 15 Pro")]
    [InlineData("  FUJIFILM ", "X-T4  ", "FUJIFILM X-T4")]
    [InlineData(null, "X100V", "X100V")]
    [InlineData("HUAWEI", null, "HUAWEI")]
    public void CameraName(string? make, string? model, string expected)
    {
        var info = Exif.Parse(TestJpeg.Build(true, make: make, model: model, original: "2023:05:06 07:08:09"));
        Assert.Equal(expected, info.Camera);
    }

    [Fact]
    public void ShortValuesInline()
    {
        // Values of 4 bytes or fewer ("abc\0") sit inside the IFD entry.
        var info = Exif.Parse(TestJpeg.Build(true, make: "Foo", model: "abc"));
        Assert.Equal("Foo abc", info.Camera);
    }

    [Theory]
    // As Windows' WIC writes it: an Exif block without the date, the date in XMP.
    [InlineData("<rdf:Description xmlns:exif=\"http://ns.adobe.com/exif/1.0/\"><exif:DateTimeOriginal>2008-07-09T11:08:00.000</exif:DateTimeOriginal></rdf:Description>")]
    [InlineData("<rdf:Description exif:DateTimeOriginal=\"2008-07-09T11:08:00+02:00\"/>")]
    [InlineData("<rdf:Description xmp:CreateDate='2008-07-09T11:08'/>")]
    public void FallsBackToTheXmpDate(string xmp)
    {
        var info = Exif.Parse(TestJpeg.Build(true, model: "Test", xmpAfter: xmp));
        Assert.Equal(new DateTime(2008, 7, 9, 11, 8, 0), info.Taken);
        Assert.Equal("Test", info.Camera);
    }

    [Fact]
    public void ExifDateWinsOverXmp()
    {
        var info = Exif.Parse(TestJpeg.Build(true, original: "2020:01:02 03:04:05",
                                             xmpAfter: "<x exif:DateTimeOriginal=\"2008-07-09T11:08:00\"/>"));
        Assert.Equal(new DateTime(2020, 1, 2, 3, 4, 5), info.Taken);
    }

    [Fact]
    public void FindsExifAfterLargeSegmentsAndXmp()
    {
        // 3 x 65 KB of ICC-like segments: past any fixed 128 KB read.
        var jpeg = TestJpeg.Build(true, model: "Pixel 8", original: "2022:12:31 23:59:58", paddingSegments: 3, xmpFirst: true);
        Assert.True(jpeg.Length > 190_000);
        var info = Exif.Parse(jpeg);
        Assert.Equal(new DateTime(2022, 12, 31, 23, 59, 58), info.Taken);
        Assert.Equal("Pixel 8", info.Camera);
    }

    [Fact]
    public void ReadsFilesAndIgnoresOtherFormats()
    {
        using var dir = new TempDir();
        var bytes = TestJpeg.Build(true, original: "2021:05:06 07:08:09");
        Assert.Equal(new DateTime(2021, 5, 6, 7, 8, 9), Exif.Read(dir.File("a.JPG", bytes)).Taken);
        Assert.Null(Exif.Read(dir.File("a.png", bytes)).Taken);
        Assert.Equal(ExifInfo.None, Exif.Read(dir.File("broken.jpg", new byte[] { 0xFF, 0xD8, 0xFF })));
        Assert.Equal(ExifInfo.None, Exif.Read(Path.Combine(dir.Path, "missing.jpg")));
    }

    [Fact]
    public void BadDatesAreNull()
    {
        Assert.Null(Exif.Parse(TestJpeg.Build(true, original: "    :  :     :  :  ")).Taken);
        Assert.Null(Exif.Parse(TestJpeg.Build(true, original: "0000:00:00 00:00:00")).Taken);
    }
}

public class DHashTests
{
    internal static byte[] Gradient(int w, int h, Func<int, int, double> f)
    {
        var g = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                g[y * w + x] = (byte)Math.Clamp(f(x, y), 0, 255);
        return g;
    }

    [Fact]
    public void SimilarImagesAreCloseDifferentOnesFar()
    {
        int w = 160, h = 120;
        // A scene with structure: bands and a blob.
        double Scene(int x, int y, double shift) =>
            128 + 70 * Math.Sin((x + shift) / 23.0) * Math.Cos(y / 17.0) + 40 * Math.Exp(-((x - 100) * (x - 100) + (y - 60) * (y - 60)) / 400.0);
        var a = DHash.Compute(Gradient(w, h, (x, y) => Scene(x, y, 0)), w, h);
        var noise = new Random(7);
        var b = DHash.Compute(Gradient(w, h, (x, y) => Scene(x, y, 1) + noise.Next(-4, 5)), w, h);
        var c = DHash.Compute(Gradient(w, h, (x, y) => 255 - Scene(x, y, 0)), w, h);
        var d = DHash.Compute(Gradient(w, h, (x, y) => 128 + 100 * Math.Cos(y / 5.0 + x / 31.0)), w, h);
        Assert.True(DHash.Distance(a, b) <= 6, $"similar: {DHash.Distance(a, b)}");
        Assert.True(DHash.Distance(a, c) >= 40, $"inverted: {DHash.Distance(a, c)}");
        Assert.True(DHash.Distance(a, d) >= 16, $"different: {DHash.Distance(a, d)}");
    }

    [Fact]
    public void GradientDirection()
    {
        var right = DHash.Compute(Gradient(90, 80, (x, _) => x * 2.5), 90, 80);
        var left = DHash.Compute(Gradient(90, 80, (x, _) => 255 - x * 2.5), 90, 80);
        Assert.Equal(ulong.MaxValue, right);  // every pixel darker than its right neighbour
        Assert.Equal(0UL, left);
        Assert.Equal(64, DHash.Distance(left, right));
        // Scaling does not change the hash.
        Assert.Equal(right, DHash.Compute(Gradient(900, 800, (x, _) => x * 0.25), 900, 800));
    }

    [Fact]
    public void TinyImagesWork()
    {
        DHash.Compute(new byte[] { 1, 2, 3, 4 }, 2, 2);
        DHash.Compute(new byte[] { 9 }, 1, 1);
    }
}

public class ImageStatsTests
{
    [Fact]
    public void SharpBeatsBlurred()
    {
        int w = 128, h = 128;
        var sharp = DHashTests.Gradient(w, h, (x, y) => ((x / 8 + y / 8) % 2) * 200 + 20);
        var blurred = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int sum = 0, n = 0;
                for (int dy = -3; dy <= 3; dy++)
                    for (int dx = -3; dx <= 3; dx++)
                    {
                        int sx = Math.Clamp(x + dx, 0, w - 1), sy = Math.Clamp(y + dy, 0, h - 1);
                        sum += sharp[sy * w + sx];
                        n++;
                    }
                blurred[y * w + x] = (byte)(sum / n);
            }
        float s = ImageStats.Sharpness(sharp, w, h), b = ImageStats.Sharpness(blurred, w, h);
        Assert.True(s > b * 4, $"sharp {s} blurred {b}");
        Assert.Equal(0, ImageStats.Sharpness(new byte[w * h], w, h));
        float e = ImageStats.Exposure(sharp, w, h);
        Assert.True(ImageStats.Quality(s, e) > ImageStats.Quality(b, e));
    }

    [Fact]
    public void Exposure()
    {
        int w = 64, h = 64;
        float mid = ImageStats.Exposure(DHashTests.Gradient(w, h, (x, y) => 60 + x + y), w, h);
        float dark = ImageStats.Exposure(DHashTests.Gradient(w, h, (x, y) => (x + y) % 8), w, h);
        float blown = ImageStats.Exposure(DHashTests.Gradient(w, h, (x, y) => 250 + (x + y) % 6), w, h);
        float halfClipped = ImageStats.Exposure(DHashTests.Gradient(w, h, (x, _) => x < 32 ? 0 : 255), w, h);
        Assert.InRange(mid, 0.85f, 1f);
        Assert.InRange(dark, 0f, 0.1f);
        Assert.InRange(blown, 0f, 0.1f);
        Assert.InRange(halfClipped, 0f, 0.05f);  // mean is mid-grey but it is all clipped
    }
}

public class VersionsTests
{
    [Theory]
    [InlineData("IMG_1_已编辑.jpg", "IMG_1")]
    [InlineData("IMG_1_已编辑 (2).jpg", "IMG_1")]
    [InlineData("IMG_1_已编辑_已编辑.png", "IMG_1")]
    [InlineData("IMG_1_已编辑 (2)_已编辑 (3).jpg", "IMG_1")]
    [InlineData("旅行_已编辑.jpeg", "旅行")]
    [InlineData("IMG_1.jpg", null)]
    [InlineData("_已编辑.jpg", null)]
    [InlineData("IMG_1 (2).jpg", null)]
    [InlineData("IMG_已编辑_1.jpg", null)]
    public void OriginalStem(string name, string? stem) => Assert.Equal(stem, Versions.OriginalStem(name));

    [Fact]
    public void MatchByNameInTheSameFolder()
    {
        string P(params string[] parts) => Path.Combine(parts);
        var paths = new[]
        {
            P("a", "IMG_1.heic"), P("a", "IMG_1_已编辑.jpg"), P("a", "IMG_1_已编辑 (2).jpg"),
            P("a", "IMG_2.jpg"), P("a", "IMG_2.png"), P("a", "IMG_2_已编辑.png"),
            P("b", "IMG_1_已编辑.jpg"),  // original is in another folder: no match
            P("a", "IMG_3_已编辑_已编辑.jpg"),  // no original at all
        };
        var map = Versions.MatchByName(paths);
        Assert.Equal(P("a", "IMG_1.heic"), map[P("a", "IMG_1_已编辑.jpg")]);
        Assert.Equal(P("a", "IMG_1.heic"), map[P("a", "IMG_1_已编辑 (2).jpg")]);
        Assert.Equal(P("a", "IMG_2.png"), map[P("a", "IMG_2_已编辑.png")]);  // same extension first
        Assert.Equal(3, map.Count);
    }
}

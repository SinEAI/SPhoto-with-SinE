using Xunit;

namespace SPhoto.Library.Tests;

public class StackTests
{
    private static readonly DateTime T0 = new(2024, 7, 1, 10, 0, 0);

    /// A hash differing from 0 in the lowest n bits.
    private static ulong Bits(int n) => n == 0 ? 0 : n >= 64 ? ulong.MaxValue : (1UL << n) - 1;

    private static StackInput P(string key, double seconds, ulong? hash = 0, string? camera = "Cam", float? quality = 1, bool exif = true) =>
        new(key, exif ? T0.AddSeconds(seconds) : null, camera, hash, quality);

    private static List<List<string>> Stacks(IReadOnlyList<StackGroup> groups) =>
        groups.Where(g => g.Members.Count > 1).Select(g => g.Members.ToList()).OrderBy(m => m[0]).ToList();

    [Fact]
    public void BurstIsStacked()
    {
        var groups = StackBuilder.Build(new[]
        {
            P("a", 0, Bits(0)), P("b", 0.5, Bits(5)), P("c", 1.5, Bits(10), quality: 5), P("d", 2.5, Bits(10)),
        });
        var stack = Assert.Single(Stacks(groups));
        Assert.Equal(new[] { "a", "b", "c", "d" }, stack);
        Assert.Equal("c", groups.Single(g => g.Members.Count > 1).Cover);  // sharpest
        Assert.Single(groups);
    }

    [Fact]
    public void BurstRulesAndRetakeRules()
    {
        // 2 s apart, 12 bits: a burst. 3 s apart, 12 bits: too different for a retake.
        Assert.Single(Stacks(StackBuilder.Build(new[] { P("a", 0, Bits(0)), P("b", 2, Bits(12)) })));
        Assert.Empty(Stacks(StackBuilder.Build(new[] { P("a", 0, Bits(0)), P("b", 2, Bits(13)) })));
        Assert.Empty(Stacks(StackBuilder.Build(new[] { P("a", 0, Bits(0)), P("b", 3, Bits(12)) })));
        // Retakes: up to 30 s when nearly identical.
        Assert.Single(Stacks(StackBuilder.Build(new[] { P("a", 0, Bits(0)), P("b", 30, Bits(6)) })));
        Assert.Empty(Stacks(StackBuilder.Build(new[] { P("a", 0, Bits(0)), P("b", 30, Bits(7)) })));
    }

    [Fact]
    public void ThirtyOneSecondsApartIsNotStacked()
    {
        Assert.Empty(Stacks(StackBuilder.Build(new[] { P("a", 0), P("b", 31) })));
    }

    [Fact]
    public void DifferentCameraIsNotStacked()
    {
        Assert.Empty(Stacks(StackBuilder.Build(new[] { P("a", 0), P("b", 1, camera: "Other") })));
        Assert.Empty(Stacks(StackBuilder.Build(new[] { P("a", 0, camera: null), P("b", 1, camera: null) })));
    }

    [Fact]
    public void NoExifDateOrNoAnalysisIsNotStacked()
    {
        Assert.Empty(Stacks(StackBuilder.Build(new[] { P("a", 0, exif: false), P("b", 0, exif: false) })));
        Assert.Empty(Stacks(StackBuilder.Build(new[] { P("a", 0), P("b", 1, hash: null) })));
    }

    [Fact]
    public void DriftGuardBreaksLongChains()
    {
        // Each frame moves 5 bits from the last, within the burst rule, but
        // the fourth is 20 bits from the first: it starts a new run.
        ulong h0 = 0, h1 = Bits(5), h2 = Bits(10), h3 = Bits(15), h4 = Bits(20), h5 = Bits(25);
        var groups = StackBuilder.Build(new[] { P("a", 0, h0), P("b", 1, h1), P("c", 2, h2), P("d", 3, h3), P("e", 4, h4), P("f", 5, h5) });
        Assert.Equal(new[] { new List<string> { "a", "b", "c", "d" }, new List<string> { "e", "f" } }, Stacks(groups));
    }

    [Fact]
    public void CoverTiesGoToTheEarliest()
    {
        var groups = StackBuilder.Build(new[] { P("b", 1, quality: 3), P("a", 0, quality: 3), P("c", 2, quality: null) });
        Assert.Equal("a", groups.Single().Cover);
        Assert.Equal(new[] { "a", "b", "c" }, groups.Single().Members);
    }

    [Fact]
    public void OverridesMergeSplitCover()
    {
        var photos = new[] { P("a", 0), P("b", 1), P("c", 2), P("x", 1000, Bits(40)), P("y", 5000, exif: false) };
        Assert.Equal(new[] { new List<string> { "a", "b", "c" } }, Stacks(StackBuilder.Build(photos)));

        var overrides = new StackOverrides
        {
            KeepApart = new HashSet<string> { "b" },
            ManualGroups = new Dictionary<string, string> { ["x"] = "g1", ["y"] = "g1", ["c"] = "g1" },
            Covers = new Dictionary<string, long> { ["x"] = 1, ["y"] = 2 },
        };
        var groups = StackBuilder.Build(photos, overrides);
        // b is on its own; c left the auto run for the manual stack; a is alone.
        Assert.Equal(new[] { new List<string> { "c", "x", "y" } }, Stacks(groups));
        Assert.Equal("y", groups.Single(g => g.Members.Count == 3).Cover);  // latest manual cover
        Assert.Equal(3, groups.Count);

        // A manual cover inside an automatic stack.
        var withCover = StackBuilder.Build(photos, new StackOverrides { Covers = new Dictionary<string, long> { ["b"] = 1 } });
        Assert.Equal("b", withCover.Single(g => g.Members.Count == 3).Cover);
    }
}

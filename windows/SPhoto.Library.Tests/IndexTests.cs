using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace SPhoto.Library.Tests;

public class IndexTests
{
    private readonly ITestOutputHelper _out;

    public IndexTests(ITestOutputHelper output) => _out = output;

    private static readonly DateTime T0 = new(2024, 7, 1, 10, 0, 0);

    private static FileRecord F(string path, string? fp, double seconds = 0, string? camera = "Cam", bool exif = true, long size = 100, long mtime = 1) =>
        new(path, size, mtime, T0.AddSeconds(seconds), exif, camera, fp);

    private static string P(params string[] parts) => Path.Combine(new[] { Path.GetTempPath(), "lib" }.Concat(parts).ToArray());

    [Fact]
    public void FilesRoundTripAndPersist()
    {
        using var dir = new TempDir();
        var db = Path.Combine(dir.Path, "library.db");
        using (var index = LibraryIndex.Open(db))
        {
            index.Upsert(F(P("a.jpg"), null, camera: null, exif: false));
            index.Upsert(F(P("b.jpg"), "fb", 5));
            index.AddTag("fb", "猫");
        }
        using (var index = LibraryIndex.Open(db))
        {
            var a = index.Known(P("a.jpg"));
            Assert.NotNull(a);
            Assert.Equal(T0, a!.Taken);
            Assert.False(a.ExifTaken);
            Assert.Null(a.Camera);
            Assert.Null(a.Fingerprint);
            Assert.Equal("fb", index.Known(P("b.jpg"))!.Fingerprint);
            Assert.Null(index.Known(P("c.jpg")));
            Assert.Equal("猫", Assert.Single(index.TagsOf("fb")).Name);
        }
        Assert.True(File.Exists(db));
    }

    [Fact]
    public void FingerprintsOnlyLandOnTheFileThatWasHashed()
    {
        using var index = LibraryIndex.Open(":memory:");
        index.Upsert(F(P("a.jpg"), null, size: 10, mtime: 1));
        index.Upsert(F(P("b.jpg"), null, size: 10, mtime: 1));
        Assert.Equal(2, index.PendingFingerprints(10).Count);
        Assert.Equal(P("b.jpg"), Assert.Single(index.PendingFingerprints(10, after: P("a.jpg"))).Path);
        Assert.False(index.SetFingerprint(P("a.jpg"), "x", size: 11, mtimeTicks: 1));  // changed since
        Assert.True(index.SetFingerprint(P("a.jpg"), "x", size: 10, mtimeTicks: 1));
        Assert.Equal(P("b.jpg"), Assert.Single(index.PendingFingerprints(10)).Path);
    }

    [Fact]
    public void AnalysisQueueAndFailures()
    {
        using var index = LibraryIndex.Open(":memory:");
        index.UpsertMany(new[] { F(P("old.jpg"), "f1", 0), F(P("new.jpg"), "f2", 100), F(P("copy.jpg"), "f2", 100), F(P("x.jpg"), null) });
        var jobs = index.PendingAnalysis(10);
        Assert.Equal(new[] { "f2", "f1" }, jobs.Select(j => j.Fingerprint));  // newest first, one per photo
        index.SetAnalysis("f2", 0xFFFF_0000_0000_0001UL, 12.5f, 0.75f);
        index.SetAnalysisFailed("f1");
        Assert.Empty(index.PendingAnalysis(10));
        var a = index.AnalysisOf("f2")!;
        Assert.Equal(0xFFFF_0000_0000_0001UL, a.DHash);
        Assert.Equal(12.5f, a.Sharpness);
        Assert.Null(index.AnalysisOf("f1")!.DHash);
    }

    [Fact]
    public void TagsKeepTheirSource()
    {
        using var index = LibraryIndex.Open(":memory:");
        index.AddTag("f1", "海边", TagSource.Auto);
        index.AddTag("f1", " 家人 ", TagSource.Manual);
        index.AddTag("f2", "海边", TagSource.Manual);
        index.AddTag("f2", "海边", TagSource.Auto);  // recognition does not take a user's tag over
        index.AddTag("f1", "海边", TagSource.Manual);  // the user confirming an auto tag makes it theirs
        Assert.Equal(new[] { new TagInfo("家人", TagSource.Manual), new TagInfo("海边", TagSource.Manual) }, index.TagsOf("f1"));
        Assert.Equal(TagSource.Manual, index.TagsOf("f2").Single().Source);
        index.AddTag("f3", "山", TagSource.Auto);
        Assert.Equal(TagSource.Auto, index.TagsOf("f3").Single().Source);
        Assert.Equal(new[] { new TagCount("海边", 2), new TagCount("家人", 1), new TagCount("山", 1) }, index.AllTags());
        index.RemoveTag("f1", "海边");
        Assert.Equal(1, index.AllTags().Single(t => t.Name == "海边").Count);
        Assert.Throws<ArgumentException>(() => index.AddTag("f1", "  "));
    }

    [Fact]
    public void TagsCountOnlyPhotosOnDiskAndMissingOnesCanBeForgotten()
    {
        using var index = LibraryIndex.Open(":memory:");
        index.Upsert(F(P("a.jpg"), "fa", 1));
        index.Upsert(F(P("b.jpg"), "fb", 2));
        index.AddTag("fa", "头像");
        index.AddTag("fb", "头像");
        index.AddTag("gone", "头像");     // a photo moved out of the library
        index.AddTag("gone", "旧相机");   // its only photo is gone too
        var catalog = index.Catalog();
        Assert.Equal(new[] { new TagCount("头像", 2) }, catalog.TagCounts());
        Assert.Equal(new[] { P("b.jpg"), P("a.jpg") }, catalog.PhotosWithTag("头像").Select(p => p.Path));
        Assert.Equal(new[] { "gone" }, catalog.MissingTagged());

        // The tags wait for the photo to come back until forgotten.
        Assert.Equal(1, index.ForgetTags(catalog.MissingTagged().ToList()));
        Assert.Empty(index.TagsOf("gone"));
        Assert.Equal(2, index.TagsOf("fa").Count + index.TagsOf("fb").Count);
        Assert.Empty(index.Catalog().MissingTagged());
    }

    [Fact]
    public void PagesShowTheirTagsWithoutTouchingFiles()
    {
        using var dir = new TempDir();
        var photo = dir.File("a.jpg", TestJpeg.Build(true, original: "2021:01:01 00:00:00"));
        var before = File.ReadAllBytes(photo);
        using var index = LibraryIndex.Open(":memory:");
        Scanner.Scan(index, new[] { dir.Path });
        Fingerprinter.Run(index);
        var fp = index.Known(photo)!.Fingerprint!;
        index.Upsert(F(P("b.jpg"), "fb", 10));
        index.Upsert(F(P("c.jpg"), "fc", 11));

        index.AddTag(fp, "#希乐");  // a typed # is not part of the name
        index.AddTag(fp, "佛罗里达");
        index.AddTag("fb", "强斯");
        index.AddTag("fc", "洛杉矶");
        long people = index.CreatePage("人物", new[] { "希乐", "强斯", "希乐", " " });
        long trips = index.CreatePage("旅游", new[] { "佛罗里达", "洛杉矶" });
        Assert.Equal(new[] { ("人物", "希乐,强斯", false), ("旅游", "佛罗里达,洛杉矶", false) },
                     index.Pages().Select(p => (p.Name, string.Join(",", p.Tags), p.Archived)));

        // A photo with tags from two pages is in both.
        var catalog = index.Catalog();
        var page = index.Pages()[0];
        Assert.Equal(new[] { "fb", fp }, catalog.Items(CatalogScope.AnyTag(page.Tags)).Select(i => i.Cover.Fingerprint));
        Assert.Contains(catalog.Items(CatalogScope.AnyTag(index.Pages()[1].Tags)), i => i.Cover.Fingerprint == fp);
        Assert.Empty(catalog.Items(CatalogScope.AnyTag(new[] { "没有" })));

        // Card sizes are per page and survive editing the page's tags.
        index.SetPageTagSize(people, "强斯", CardSize.Large);
        index.SetPageTagSize(trips, "洛杉矶", CardSize.Medium);
        Assert.Equal(CardSize.Large, index.Pages()[0].SizeOf("强斯"));
        Assert.Equal(CardSize.Small, index.Pages()[0].SizeOf("希乐"));
        Assert.Equal(CardSize.Small, index.Pages()[1].SizeOf("强斯"));
        index.RenamePage(people, "家人");
        index.SetPageTags(people, new[] { "强斯" });
        Assert.Equal(CardSize.Large, index.Pages()[0].SizeOf("强斯"));
        index.ArchivePage(trips, true);
        Assert.Equal(new[] { ("家人", "强斯", false), ("旅游", "佛罗里达,洛杉矶", true) },
                     index.Pages().Select(p => (p.Name, string.Join(",", p.Tags), p.Archived)));

        // Renaming a tag follows into pages; deleting one leaves the page.
        index.RenameTag("洛杉矶", "LA");
        Assert.Equal(new[] { "佛罗里达", "LA" }, index.Pages()[1].Tags);
        Assert.Equal(CardSize.Medium, index.Pages()[1].SizeOf("LA"));
        Assert.Equal("fc", Assert.Single(index.Catalog().Items(CatalogScope.Tag("LA"))).Cover.Fingerprint);
        index.DeleteTag("佛罗里达");
        Assert.Equal(new[] { "LA" }, index.Pages()[1].Tags);
        Assert.DoesNotContain(index.AllTags(), t => t.Name == "佛罗里达");
        index.DeletePage(people);
        Assert.Single(index.Pages());
        Assert.Equal(before, File.ReadAllBytes(photo));  // the photo itself is untouched
    }

    [Fact]
    public void DuplicatesAreOnePhotoWithCopies()
    {
        using var index = LibraryIndex.Open(":memory:");
        index.UpsertMany(new[]
        {
            F(P("backup", "2024", "IMG_1.jpg"), "f1", exif: false, size: 5),
            F(P("IMG_1.jpg"), "f1", 3, size: 5),
            F(P("z", "IMG_1.jpg"), "f1", size: 5),
        });
        var catalog = index.Catalog();
        var photo = Assert.Single(catalog.ByFingerprint.Values);
        Assert.Equal(P("IMG_1.jpg"), photo.Path);
        Assert.Equal(new[] { P("IMG_1.jpg"), P("z", "IMG_1.jpg"), P("backup", "2024", "IMG_1.jpg") }, photo.Copies);
        Assert.Equal(T0.AddSeconds(3), photo.Taken);
        Assert.Equal(1, catalog.Count);
        Assert.Same(photo, catalog.ByPath[P("backup", "2024", "IMG_1.jpg")]);
    }

    [Fact]
    public void VersionsFoldUnderTheOriginal()
    {
        using var index = LibraryIndex.Open(":memory:");
        index.UpsertMany(new[]
        {
            F(P("IMG_1.jpg"), "orig", 0),
            F(P("IMG_1_已编辑.jpg"), "v1", 0, mtime: 10),
            F(P("IMG_1_已编辑 (2).jpg"), "v2", 0, mtime: 20),
            F(P("exported.jpg"), "v3", 0, mtime: 30),  // recorded by hand below
            F(P("IMG_2.jpg"), "other", 100),
        });
        index.AddVersion("orig", "v3");
        var catalog = index.Catalog();
        Assert.Equal(new[] { "other", "orig" }, catalog.Items(CatalogScope.All).Select(i => i.Cover.Fingerprint));
        var item = catalog.ItemOf("v1")!;
        Assert.Equal("orig", item.Cover.Fingerprint);
        Assert.Equal(new[] { "v3", "v2", "v1" }, item.Versions.Select(v => v.Fingerprint));  // newest first
        Assert.Equal("orig", catalog.OriginalOf("v2")!.Fingerprint);

        // A record beats the name: v1 belongs to IMG_2 after all.
        index.AddVersion("other", "v1");
        catalog = index.Catalog();
        Assert.Equal("other", catalog.ItemOf("v1")!.Cover.Fingerprint);

        // In a tag holding only a version, the version shows on its own.
        index.AddTag("v2", "修过的");
        Assert.Equal("v2", index.Catalog().Items(CatalogScope.Tag("修过的")).Single().Cover.Fingerprint);
    }

    [Fact]
    public void FilesWithoutFingerprintAreListed()
    {
        using var index = LibraryIndex.Open(":memory:");
        index.UpsertMany(new[] { F(P("a.jpg"), null, 0), F(P("b.jpg"), null, 5), F(P("c.jpg"), "fc", 10) });
        var items = index.Catalog().Items(CatalogScope.All);
        Assert.Equal(new[] { "fc", "path:" + P("b.jpg"), "path:" + P("a.jpg") }, items.Select(i => i.Cover.Fingerprint));
        Assert.False(items[1].Cover.IsFingerprinted);
    }

    [Fact]
    public void StacksInTheCatalogAndTheirOverrides()
    {
        using var index = LibraryIndex.Open(":memory:");
        index.UpsertMany(new[] { F(P("1.jpg"), "a", 0), F(P("2.jpg"), "b", 1), F(P("3.jpg"), "c", 2), F(P("4.jpg"), "d", 500) });
        // Not analysed yet: nothing stacks.
        Assert.Equal(4, index.Catalog().Count);
        index.SetAnalysis("a", 0, 10, 0.5f);
        index.SetAnalysis("b", 1, 500, 0.9f);
        index.SetAnalysis("c", 3, 20, 0.5f);
        index.SetAnalysis("d", ulong.MaxValue, 20, 0.5f);
        var catalog = index.Catalog();
        Assert.Equal(2, catalog.Count);
        var burst = catalog.ItemOf("a")!;
        Assert.Equal("b", burst.Cover.Fingerprint);  // sharpest
        Assert.Equal(new[] { "a", "b", "c" }, burst.Stack.Select(p => p.Fingerprint));

        index.SetCover("c");
        Assert.Equal("c", index.Catalog().ItemOf("a")!.Cover.Fingerprint);
        index.SetCover("a");
        Assert.Equal("a", index.Catalog().ItemOf("c")!.Cover.Fingerprint);

        index.SplitStack(new[] { "b" });
        catalog = index.Catalog();
        Assert.Equal(new[] { "a", "c" }, catalog.ItemOf("a")!.Stack.Select(p => p.Fingerprint));
        Assert.Single(catalog.ItemOf("b")!.Stack);

        index.MergeIntoStack(new[] { "b", "d" });
        catalog = index.Catalog();
        Assert.Equal(new[] { "b", "d" }, catalog.ItemOf("d")!.Stack.Select(p => p.Fingerprint));
        index.MergeIntoStack(new[] { "a", "c", "d" });  // d brings b along
        catalog = index.Catalog();
        Assert.Equal(1, catalog.Count);
        Assert.Equal(new[] { "a", "b", "c", "d" }, catalog.ItemOf("a")!.Stack.Select(p => p.Fingerprint));

        index.ResetStack(new[] { "a", "b", "c", "d" });
        Assert.Equal(2, index.Catalog().Count);

        // In a tag holding two of the burst, they stack by themselves.
        foreach (var f in new[] { "a", "c", "d" }) index.AddTag(f, "精选");
        var inAlbum = index.Catalog().Items(CatalogScope.Tag("精选"));
        Assert.Equal(2, inAlbum.Count);
        var stack = inAlbum.Single(i => i.IsStack);
        Assert.Equal(new[] { "a", "c" }, stack.Stack.Select(p => p.Fingerprint));
        Assert.Equal("c", stack.Cover.Fingerprint);  // b, the global cover, is not tagged
    }

    [Fact]
    public void ScanFindsChangesAndKeepsFingerprints()
    {
        using var dir = new TempDir();
        var a = dir.File("2021/a.jpg", TestJpeg.Build(true, make: "Canon", model: "Canon EOS R5", original: "2021:05:06 07:08:09"));
        var b = dir.File("b.png", new byte[] { 1, 2, 3 });
        dir.File("notes.txt", new byte[] { 1 });
        var hidden = dir.File(".hidden/c.jpg", new byte[] { 1 });
        if (OperatingSystem.IsWindows())
        {
            var folder = Path.GetDirectoryName(hidden)!;
            File.SetAttributes(folder, File.GetAttributes(folder) | FileAttributes.Hidden);
        }
        using var index = LibraryIndex.Open(":memory:");

        var result = Scanner.Scan(index, new[] { dir.Path });
        Assert.Equal(new[] { a, b }.OrderBy(p => p), result.Files.Select(f => f.Path).OrderBy(p => p));
        var ra = result.Files.Single(f => f.Path == a);
        Assert.Equal(new DateTime(2021, 5, 6, 7, 8, 9), ra.Taken);
        Assert.True(ra.ExifTaken);
        Assert.Equal("Canon EOS R5", ra.Camera);
        Assert.False(result.Files.Single(f => f.Path == b).ExifTaken);
        Assert.Null(ra.Fingerprint);

        var progress = new List<int>();
        Assert.Equal(2, Fingerprinter.Run(index, default, new SyncProgress(progress.Add)));
        Assert.Equal(2, progress.Last());
        Assert.Equal(Fingerprint.Compute(a), index.Known(a)!.Fingerprint);

        // Unchanged files keep their fingerprint; a changed one loses it.
        File.WriteAllBytes(b, new byte[] { 4, 5, 6, 7 });
        result = Scanner.Scan(index, new[] { dir.Path });
        Assert.NotNull(result.Files.Single(f => f.Path == a).Fingerprint);
        Assert.Null(index.Known(b)!.Fingerprint);

        // Deleted files go; files outside the roots go.
        File.Delete(a);
        Scanner.Scan(index, new[] { dir.Path });
        Assert.Null(index.Known(a));
        Scanner.Scan(index, new[] { Path.Combine(dir.Path, "2021") });
        Assert.Null(index.Known(b));
        Assert.True(File.Exists(hidden));
    }

    [Fact]
    public void ScanCanBeCancelled()
    {
        using var dir = new TempDir();
        dir.File("a.jpg", new byte[] { 1 });
        using var index = LibraryIndex.Open(":memory:");
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Scanner.Scan(index, new[] { dir.Path }, cancel.Token));
    }

    [Fact]
    public void PagesFromBeforeCardSizesOpenWithSmallCards()
    {
        using var dir = new TempDir();
        var db = Path.Combine(dir.Path, "library.db");
        using (var index = LibraryIndex.Open(db)) index.CreatePage("人物", new[] { "希乐" });
        using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db};Pooling=False"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = "ALTER TABLE page_tags DROP COLUMN size; DROP INDEX files_legacy_fingerprint; ALTER TABLE files DROP COLUMN legacy_fingerprint; DROP TABLE legacy_fingerprints; PRAGMA user_version = 2;";
            cmd.ExecuteNonQuery();
        }
        using (var index = LibraryIndex.Open(db))
        {
            var page = Assert.Single(index.Pages());
            Assert.Equal(CardSize.Small, page.SizeOf("希乐"));
            index.SetPageTagSize(page.Id, "希乐", CardSize.Medium);
            Assert.Equal(CardSize.Medium, index.Pages()[0].SizeOf("希乐"));
        }
    }

    [Fact]
    public void SchemaIsVersioned()
    {
        using var dir = new TempDir();
        var db = Path.Combine(dir.Path, "library.db");
        LibraryIndex.Open(db).Dispose();
        using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db};Pooling=False"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = "PRAGMA user_version";
            Assert.Equal((long)LibraryIndex.SchemaVersion, (long)cmd.ExecuteScalar()!);
            cmd.CommandText = "PRAGMA journal_mode";
            Assert.Equal("wal", (string)cmd.ExecuteScalar()!);
            cmd.CommandText = $"PRAGMA user_version = {LibraryIndex.SchemaVersion + 1}";
            cmd.ExecuteNonQuery();
        }
        Assert.Throws<InvalidOperationException>(() => LibraryIndex.Open(db));
    }

    [Fact]
    public void SafeFromManyThreads()
    {
        using var index = LibraryIndex.Open(":memory:");
        Parallel.For(0, 200, i =>
        {
            index.Upsert(F(P($"{i}.jpg"), $"f{i}", i));
            index.AddTag($"f{i}", "t" + (i % 5));
            if (i % 10 == 0) index.Catalog();
        });
        Assert.Equal(200, index.Catalog().Count);
        Assert.Equal(5, index.AllTags().Count);
    }

    [Fact]
    public void FiftyThousandPhotoCatalog()
    {
        using var dir = new TempDir();
        using var index = LibraryIndex.Open(Path.Combine(dir.Path, "library.db"));
        const int n = 50_000;
        var random = new Random(42);
        var files = new List<FileRecord>(n);
        var clock = T0;
        for (int i = 0; i < n; i++)
        {
            // Bursts of 1-5 shots, a gap of minutes between them.
            clock = clock.AddSeconds(i % 5 == 0 ? 600 : 1);
            string folder = $"{2000 + i / 5000}";
            string name = i % 50 == 1 ? $"IMG_{i - 1}_已编辑.jpg" : $"IMG_{i}.jpg";
            string? fp = i % 100 == 99 ? null : $"fp{i}";
            if (i % 200 == 7) fp = "fp" + (i - 1);  // a duplicate
            files.Add(new FileRecord(P(folder, name), 1000 + i, i, clock, true, i % 3 == 0 ? "A" : "B", fp));
        }
        var sw = Stopwatch.StartNew();
        index.UpsertMany(files);
        var insert = sw.Elapsed;
        for (int i = 0; i < n; i += 1)
        {
            if (files[i].Fingerprint is { } fp && i % 4 != 3) index.SetAnalysis(fp, (ulong)random.NextInt64() & 0xFF, random.NextSingle() * 100, 0.5f);
        }
        for (int i = 0; i < n; i += 3)
        {
            if (files[i].Fingerprint is { } fp) index.AddTag(fp, "大页面");
        }
        for (int i = 0; i < n; i += 7)
        {
            if (files[i].Fingerprint is { } fp) index.AddTag(fp, "tag" + i % 10);
        }

        sw.Restart();
        var catalog = index.Catalog();
        var build = sw.Elapsed;
        sw.Restart();
        index.Catalog();
        var warm = sw.Elapsed;
        sw.Restart();
        var all = catalog.Items(CatalogScope.All);
        var inAlbum = catalog.Items(CatalogScope.AnyTag(new[] { "大页面", "tag1" }));
        var tagged = catalog.Items(CatalogScope.Tag("tag3"));
        var query = sw.Elapsed;
        _out.WriteLine($"insert {n}: {insert.TotalMilliseconds:F0} ms; catalog: {build.TotalMilliseconds:F0} ms, again {warm.TotalMilliseconds:F0} ms; " +
                       $"queries: {query.TotalMilliseconds:F0} ms; {all.Count} tiles, {inAlbum.Count} in a page, {tagged.Count} tagged, " +
                       $"{all.Count(i => i.IsStack)} stacks, {all.Sum(i => i.Versions.Count)} versions");
        Console.WriteLine($"[50k] catalog {build.TotalMilliseconds:F0} ms (again {warm.TotalMilliseconds:F0} ms), queries {query.TotalMilliseconds:F0} ms, insert {insert.TotalMilliseconds:F0} ms");

        Assert.True(build < TimeSpan.FromSeconds(5), $"catalog took {build}");
        Assert.True(query < TimeSpan.FromSeconds(2), $"queries took {query}");
        Assert.True(all.Count < n);
        Assert.Contains(all, i => i.IsStack);
        Assert.Contains(all, i => i.Versions.Count > 0);
        Assert.True(all.Zip(all.Skip(1)).All(p => p.First.Cover.Taken >= p.Second.Cover.Taken));
        Assert.Equal(n, catalog.ByPath.Count);
    }

    private sealed class SyncProgress : IProgress<int>
    {
        private readonly Action<int> _report;
        public SyncProgress(Action<int> report) => _report = report;
        public void Report(int value) => _report(value);
    }
}

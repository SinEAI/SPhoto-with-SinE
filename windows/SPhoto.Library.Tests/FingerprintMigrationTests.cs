using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Xunit;

namespace SPhoto.Library.Tests;

public class FingerprintMigrationTests
{
    private static byte[] Data(int size, int seed)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static string Legacy(byte[] data)
    {
        byte[] input = new byte[8 + Math.Min(data.Length, 65536)];
        BinaryPrimitives.WriteInt64LittleEndian(input, data.Length);
        if (data.Length <= 65536) data.CopyTo(input, 8);
        else
        {
            data.AsSpan(0, 32768).CopyTo(input.AsSpan(8));
            data.AsSpan(data.Length - 32768).CopyTo(input.AsSpan(32776));
        }
        return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    }

    private static void DowngradeToSampledSchema(string db)
    {
        using var connection = new SqliteConnection($"Data Source={db};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DROP INDEX files_legacy_fingerprint; ALTER TABLE files DROP COLUMN legacy_fingerprint; DROP TABLE legacy_fingerprints; PRAGMA user_version = 3;";
        command.ExecuteNonQuery();
    }

    private static FileRecord Record(string path, byte[] data) =>
        Scanner.ReadFile(new FileInfo(path)) with { Fingerprint = Legacy(data) };

    [Theory]
    [InlineData(0)]
    [InlineData(65535)]
    [InlineData(65536)]
    public void ShortFileIdentitiesAreCompatible(int length)
    {
        byte[] data = Data(length, 1);
        Assert.Equal(Legacy(data), Fingerprint.Compute(new MemoryStream(data)));
    }

    [Fact]
    public void UpgradeResumesAndPreservesTagsStacksVersionsAndSmallFiles()
    {
        using var dir = new TempDir();
        byte[] original = Data(200000, 1), version = Data(200000, 2), small = Data(40000, 3);
        string a = dir.File("a.jpg", original), b = dir.File("b.jpg", version), c = dir.File("c.jpg", small);
        string db = Path.Combine(dir.Path, "library.db");
        string old = Legacy(original), current = Fingerprint.Compute(a), edited = Fingerprint.Compute(b);
        using (var index = LibraryIndex.Open(db))
        {
            index.UpsertMany(new[] { Record(a, original), Record(b, version), Record(c, small) });
            index.AddTag(old, "sample-tag");
            index.KeepApart(old);
            index.SetCover(old);
            index.AddVersion(old, Legacy(version));
            index.SetAnalysis(old, 42, 1, 0.5f);
        }
        DowngradeToSampledSchema(db);
        using (var index = LibraryIndex.Open(db))
        {
            Assert.Equal(2, index.PendingFingerprints(10).Count);
            Assert.Equal(Legacy(small), index.Known(c)!.Fingerprint);
            var info = new FileInfo(a);
            Assert.True(index.SetFingerprint(a, current, info.Length, info.LastWriteTimeUtc.Ticks));
            Assert.Equal("sample-tag", Assert.Single(index.TagsOf(current)).Name);
        }
        // One identity was verified before closing; the rest resumes after reopening.
        using (var index = LibraryIndex.Open(db))
        {
            Assert.Equal(1, Fingerprinter.Run(index));
            Assert.Empty(index.PendingFingerprints(10));
            Assert.Equal(current, index.Known(a)!.Fingerprint);
            Assert.Equal(edited, index.Known(b)!.Fingerprint);
            Assert.Equal("sample-tag", Assert.Single(index.TagsOf(current)).Name);
            Assert.Empty(index.TagsOf(old));
            Assert.True(index.Overrides().KeepApart.Contains(current));
            Assert.Equal(current, index.Catalog().OriginalOf(edited)!.Fingerprint);
            Assert.Null(index.AnalysisOf(current));
            Assert.Contains(index.PendingAnalysis(10), job => job.Fingerprint == current);
        }
    }

    [Fact]
    public void SampledCollisionSplitsPhotosPreservesTagsAndDoesNotGuessVersionParent()
    {
        using var dir = new TempDir();
        byte[] first = Data(200000, 4), second = (byte[])first.Clone(), version = Data(40000, 5);
        second[100000] ^= 1;
        string a = dir.File("a.jpg", first), b = dir.File("b.jpg", second), c = dir.File("c.jpg", version);
        string old = Legacy(first), db = Path.Combine(dir.Path, "library.db");
        Assert.Equal(old, Legacy(second));
        using (var index = LibraryIndex.Open(db))
        {
            index.UpsertMany(new[] { Record(a, first), Record(b, second), Record(c, version) });
            index.AddTag(old, "sample-tag");
            index.AddVersion(old, Legacy(version));
        }
        DowngradeToSampledSchema(db);
        using (var index = LibraryIndex.Open(db))
        {
            Assert.Equal(2, Fingerprinter.Run(index));
            Assert.NotEqual(index.Known(a)!.Fingerprint, index.Known(b)!.Fingerprint);
            Assert.Equal(2, index.Catalog().PhotosWithTag("sample-tag").Count);
            Assert.Equal(3, index.Catalog().Count);
        }
        using var connection = new SqliteConnection($"Data Source={db};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM versions WHERE original_fingerprint = $old";
        command.Parameters.AddWithValue("$old", old);
        Assert.Equal(1L, (long)command.ExecuteScalar()!);
    }

    [Fact]
    public void RenameBeforeRehashStillFindsLegacyTags()
    {
        using var dir = new TempDir();
        byte[] data = Data(200000, 6);
        string before = dir.File("before.jpg", data), after = Path.Combine(dir.Path, "after.jpg");
        string db = Path.Combine(dir.Path, "library.db");
        using (var index = LibraryIndex.Open(db))
        {
            index.Upsert(Record(before, data));
            index.AddTag(Legacy(data), "sample-tag");
        }
        DowngradeToSampledSchema(db);
        using (var index = LibraryIndex.Open(db))
        {
            File.Move(before, after);
            Scanner.Scan(index, new[] { dir.Path });
            Assert.Null(index.Known(before));
            Assert.Equal(1, Fingerprinter.Run(index));
            Assert.Equal("sample-tag", Assert.Single(index.TagsOf(index.Known(after)!.Fingerprint!)).Name);
        }
    }
}

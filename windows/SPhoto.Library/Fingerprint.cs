using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;

namespace SPhoto.Library;

/// <summary>
/// The identity of a photo in the library (图库): the same bytes in two
/// folders are one photo with two copies, and tags follow the
/// photo when it is moved or renamed.
/// </summary>
public static class Fingerprint
{
    /// Legacy sampling size, retained only to migrate existing identities.
    public const int EdgeBytes = 32 * 1024;

    /// <summary>
    /// Lowercase hex SHA-256 over the file length (8 bytes little-endian)
    /// and every file byte. Short-file identities remain compatible.
    /// </summary>
    public static string Compute(string path)
    {
        using var stream = Open(path);
        return Compute(stream);
    }

    /// The same over a seekable stream.
    public static string Compute(Stream stream)
    {
        long length = stream.Length;
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> size = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(size, length);
        sha.AppendData(size);

        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            stream.Position = 0;
            int n;
            while ((n = stream.Read(buffer, 0, buffer.Length)) > 0) sha.AppendData(buffer, 0, n);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    internal static (string Full, string Legacy) ComputeForMigration(string path)
    {
        using var stream = Open(path);
        string full = Compute(stream);
        if (stream.Length <= 2 * EdgeBytes) return (full, full);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> size = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(size, stream.Length);
        sha.AppendData(size);
        var buffer = ArrayPool<byte>.Shared.Rent(EdgeBytes);
        try
        {
            stream.Position = 0;
            stream.ReadExactly(buffer, 0, EdgeBytes);
            sha.AppendData(buffer, 0, EdgeBytes);
            stream.Position = stream.Length - EdgeBytes;
            stream.ReadExactly(buffer, 0, EdgeBytes);
            sha.AppendData(buffer, 0, EdgeBytes);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        return (full, Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant());
    }

    private static FileStream Open(string path) => new(path, FileMode.Open, FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, FileOptions.SequentialScan);
}

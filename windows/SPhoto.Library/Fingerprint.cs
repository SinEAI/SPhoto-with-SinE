using System;
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
    /// How much of each end is hashed. Reading only the ends keeps a 50 MB
    /// RAW as cheap as a phone JPEG; the length and both ends together
    /// differ for any two real photos.
    public const int EdgeBytes = 32 * 1024;

    /// <summary>
    /// Lowercase hex SHA-256 over the file length (8 bytes little-endian),
    /// the first 32 KiB and the last 32 KiB, or the whole file when it is
    /// shorter than 64 KiB.
    /// </summary>
    public static string Compute(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                                          bufferSize: 1, FileOptions.RandomAccess);
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

        var buffer = new byte[EdgeBytes];
        if (length < 2 * EdgeBytes)
        {
            stream.Position = 0;
            int n;
            while ((n = stream.Read(buffer, 0, buffer.Length)) > 0) sha.AppendData(buffer, 0, n);
        }
        else
        {
            stream.Position = 0;
            stream.ReadExactly(buffer, 0, EdgeBytes);
            sha.AppendData(buffer);
            stream.Position = length - EdgeBytes;
            stream.ReadExactly(buffer, 0, EdgeBytes);
            sha.AppendData(buffer);
        }
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>
    /// The same SHA-256 over the length and every byte of the file: equal to
    /// Compute below 64 KiB. The fingerprinter reads whole files only when
    /// two of them share a sampled fingerprint, to tell real copies apart
    /// from different photos whose ends happen to match.
    /// </summary>
    public static string ComputeFull(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                                          bufferSize: 1, FileOptions.SequentialScan);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> size = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(size, stream.Length);
        sha.AppendData(size);
        var buffer = new byte[64 * 1024];
        int n;
        while ((n = stream.Read(buffer, 0, buffer.Length)) > 0) sha.AppendData(buffer, 0, n);
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }
}

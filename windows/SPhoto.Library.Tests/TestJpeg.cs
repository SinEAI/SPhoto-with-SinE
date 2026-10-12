using System.Text;

namespace SPhoto.Library.Tests;

/// <summary>Builds minimal JPEG byte streams with an Exif block, by hand.</summary>
internal static class TestJpeg
{
    internal static byte[] Build(bool little, string? make = null, string? model = null, string? dateTime = null,
                                 string? original = null, int paddingSegments = 0, bool xmpFirst = false,
                                 string? xmpAfter = null)
    {
        var ms = new MemoryStream();
        ms.Write(new byte[] { 0xFF, 0xD8 });
        Segment(ms, 0xE0, "JFIF\0\x01\x02\0\0\x01\0\x01\0\0"u8.ToArray());
        for (int i = 0; i < paddingSegments; i++) Segment(ms, 0xE2, new byte[65000]);
        if (xmpFirst) Segment(ms, 0xE1, Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0<x:xmpmeta/>"));
        var exif = new MemoryStream();
        exif.Write("Exif\0\0"u8);
        exif.Write(Tiff(little, make, model, dateTime, original));
        Segment(ms, 0xE1, exif.ToArray());
        if (xmpAfter is not null) Segment(ms, 0xE1, Encoding.UTF8.GetBytes("http://ns.adobe.com/xap/1.0/\0" + xmpAfter));
        // A start of scan and some "image data".
        Segment(ms, 0xDA, new byte[10]);
        ms.Write(new byte[100]);
        ms.Write(new byte[] { 0xFF, 0xD9 });
        return ms.ToArray();
    }

    private static void Segment(Stream s, byte marker, byte[] body)
    {
        int size = body.Length + 2;
        s.Write(new byte[] { 0xFF, marker, (byte)(size >> 8), (byte)size });
        s.Write(body);
    }

    private static byte[] Tiff(bool little, string? make, string? model, string? dateTime, string? original)
    {
        var ifd0 = new List<(int Tag, byte[]? Ascii)>();
        if (make is not null) ifd0.Add((0x010F, Ascii(make)));
        if (model is not null) ifd0.Add((0x0110, Ascii(model)));
        if (dateTime is not null) ifd0.Add((0x0132, Ascii(dateTime)));
        bool hasExif = original is not null;
        int ifd0Count = ifd0.Count + (hasExif ? 1 : 0);
        int ifd0At = 8, ifd0Size = 2 + 12 * ifd0Count + 4;
        int exifAt = ifd0At + ifd0Size, exifSize = hasExif ? 2 + 12 + 4 : 0;
        int dataAt = exifAt + exifSize;

        var data = new List<byte>();
        var buf = new List<byte>();
        void U16(List<byte> b, int v) { if (little) { b.Add((byte)v); b.Add((byte)(v >> 8)); } else { b.Add((byte)(v >> 8)); b.Add((byte)v); } }
        void U32(List<byte> b, long v)
        {
            if (little) { b.Add((byte)v); b.Add((byte)(v >> 8)); b.Add((byte)(v >> 16)); b.Add((byte)(v >> 24)); }
            else { b.Add((byte)(v >> 24)); b.Add((byte)(v >> 16)); b.Add((byte)(v >> 8)); b.Add((byte)v); }
        }
        void AsciiEntry(List<byte> b, int tag, byte[] value)
        {
            U16(b, tag);
            U16(b, 2);
            U32(b, value.Length);
            if (value.Length <= 4)
            {
                var inline = new byte[4];
                value.CopyTo(inline, 0);
                b.AddRange(inline);
            }
            else
            {
                U32(b, dataAt + data.Count);
                data.AddRange(value);
            }
        }

        buf.AddRange(little ? "II"u8.ToArray() : "MM"u8.ToArray());
        U16(buf, 42);
        U32(buf, ifd0At);
        U16(buf, ifd0Count);
        foreach (var (tag, value) in ifd0.OrderBy(e => e.Tag)) AsciiEntry(buf, tag, value!);
        if (hasExif)
        {
            U16(buf, 0x8769);
            U16(buf, 4);
            U32(buf, 1);
            U32(buf, exifAt);
        }
        U32(buf, 0);
        if (hasExif)
        {
            U16(buf, 1);
            AsciiEntry(buf, 0x9003, Ascii(original!));
            U32(buf, 0);
        }
        buf.AddRange(data);
        return buf.ToArray();
    }

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s + "\0");
}

/// <summary>A temporary folder deleted at the end of a test.</summary>
internal sealed class TempDir : IDisposable
{
    internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sphoto-test-" + Guid.NewGuid().ToString("N")[..8]);

    internal TempDir() => Directory.CreateDirectory(Path);

    internal string File(string relative, byte[] content)
    {
        var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(Path, relative));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, content);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

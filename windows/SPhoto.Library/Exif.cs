using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace SPhoto.Library;

/// <summary>What the library needs from a photo's EXIF block.</summary>
public sealed record ExifInfo(DateTime? Taken, string? Camera)
{
    public static readonly ExifInfo None = new(null, null);
}

/// <summary>
/// Reads the shooting date and camera from a JPEG without decoding it. It
/// walks the segment headers and reads only the APP1 Exif segment, so a
/// whole library can be scanned at disk speed.
/// </summary>
public static class Exif
{
    internal static bool IsJpeg(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".jfif", StringComparison.OrdinalIgnoreCase);
    }

    /// ExifInfo.None for other formats and for files that cannot be read.
    public static ExifInfo Read(string path)
    {
        if (!IsJpeg(path)) return ExifInfo.None;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                                              bufferSize: 4096);
            return Read(stream);
        }
        catch (Exception)
        {
            return ExifInfo.None;
        }
    }

    /// Reads from the start of a JPEG stream; seeks past segments when it can.
    /// The date comes from Exif, else from XMP: some writers (Windows' own
    /// WIC among them) put the date only in the XMP packet.
    public static ExifInfo Read(Stream stream)
    {
        Span<byte> header = stackalloc byte[4];
        if (!Fill(stream, header[..2]) || header[0] != 0xFF || header[1] != 0xD8) return ExifInfo.None;
        ExifInfo exif = ExifInfo.None;
        DateTime? xmp = null;
        while (true)
        {
            if (!Fill(stream, header[..2]) || header[0] != 0xFF) break;
            byte marker = header[1];
            // Fill bytes: any number of 0xFF may precede a marker.
            while (marker == 0xFF)
            {
                int next = stream.ReadByte();
                if (next < 0) return Merge(exif, xmp);
                marker = (byte)next;
            }
            if (marker == 0x01 || marker is >= 0xD0 and <= 0xD7) continue;  // no length
            if (marker is 0xDA or 0xD9) break;  // image data: no more metadata
            if (!Fill(stream, header[..2])) break;
            int size = (header[0] << 8) | header[1];
            if (size < 2) break;
            int body = size - 2;
            if (marker == 0xE1 && body > 14)
            {
                var segment = new byte[body];
                if (!Fill(stream, segment)) break;
                // APP1 holds Exif (TIFF) or XMP.
                if (segment.AsSpan(0, 6).SequenceEqual("Exif\0\0"u8))
                {
                    exif = ParseTiff(segment.AsSpan(6));
                    if (exif.Taken is not null) return exif;
                }
                else if (segment.AsSpan().StartsWith(XmpHeader))
                {
                    xmp ??= XmpDate(segment.AsSpan(XmpHeader.Length));
                }
                continue;
            }
            if (!Skip(stream, body)) break;
        }
        return Merge(exif, xmp);
    }

    private static ExifInfo Merge(ExifInfo exif, DateTime? xmp) =>
        xmp is null || exif.Taken is not null ? exif : exif with { Taken = xmp };

    private static ReadOnlySpan<byte> XmpHeader => "http://ns.adobe.com/xap/1.0/\0"u8;

    /// <summary>
    /// The shooting date in an XMP packet (Adobe XMP Specification Part 2):
    /// exif:DateTimeOriginal, else photoshop:DateCreated, else
    /// xmp:CreateDate, as an attribute or an element. The clock time as
    /// written, like Exif's; a time zone suffix is ignored.
    /// </summary>
    internal static DateTime? XmpDate(ReadOnlySpan<byte> packet)
    {
        var text = Encoding.UTF8.GetString(packet);
        foreach (var name in new[] { "exif:DateTimeOriginal", "photoshop:DateCreated", "xmp:CreateDate" })
        {
            int at = text.IndexOf(name, StringComparison.Ordinal);
            while (at >= 0)
            {
                int i = at + name.Length;
                while (i < text.Length && text[i] == ' ') i++;
                if (i < text.Length && (text[i] == '=' || text[i] == '>'))
                {
                    i++;
                    while (i < text.Length && (text[i] is ' ' or '"' or '\'')) i++;
                    int end = i;
                    while (end < text.Length && text[end] is not ('"' or '\'' or '<')) end++;
                    var value = text[i..end].Trim();
                    if (value.Length >= 19 &&
                        DateTime.TryParseExact(value[..19], "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture,
                                               DateTimeStyles.None, out var d)) return d;
                    if (value.Length >= 16 &&
                        DateTime.TryParseExact(value[..16], "yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture,
                                               DateTimeStyles.None, out d)) return d;
                }
                at = text.IndexOf(name, at + name.Length, StringComparison.Ordinal);
            }
        }
        return null;
    }

    /// The same over JPEG bytes in memory.
    public static ExifInfo Parse(ReadOnlySpan<byte> jpeg)
    {
        using var stream = new MemoryStream(jpeg.ToArray(), writable: false);
        return Read(stream);
    }

    private static bool Fill(Stream stream, Span<byte> buffer)
    {
        while (buffer.Length > 0)
        {
            int n = stream.Read(buffer);
            if (n <= 0) return false;
            buffer = buffer[n..];
        }
        return true;
    }

    private static bool Skip(Stream stream, int count)
    {
        if (stream.CanSeek)
        {
            if (stream.Position + count > stream.Length) return false;
            stream.Seek(count, SeekOrigin.Current);
            return true;
        }
        Span<byte> scratch = stackalloc byte[512];
        while (count > 0)
        {
            int n = stream.Read(scratch[..Math.Min(count, scratch.Length)]);
            if (n <= 0) return false;
            count -= n;
        }
        return true;
    }

    private const int TagMake = 0x010F, TagModel = 0x0110, TagDateTime = 0x0132;
    private const int TagExifPointer = 0x8769, TagDateTimeOriginal = 0x9003;

    /// Parses the TIFF structure inside the Exif segment (after "Exif\0\0").
    internal static ExifInfo ParseTiff(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8) return ExifInfo.None;
        bool little;
        if (tiff[0] == 'I' && tiff[1] == 'I') little = true;
        else if (tiff[0] == 'M' && tiff[1] == 'M') little = false;
        else return ExifInfo.None;
        var t = new Tiff(tiff, little);
        if (t.U16(2) != 42) return ExifInfo.None;

        long ifd0 = t.U32(4);
        DateTime? taken = null;
        long exifPointer = t.Find(ifd0, TagExifPointer);
        if (exifPointer >= 0) taken = Date(t, t.Find(t.U32((int)exifPointer + 8), TagDateTimeOriginal));
        taken ??= Date(t, t.Find(ifd0, TagDateTime));  // DateTime: last change

        var make = Ascii(t, t.Find(ifd0, TagMake));
        var model = Ascii(t, t.Find(ifd0, TagModel));
        return new ExifInfo(taken, CameraName(make, model));
    }

    /// <summary>
    /// "Make Model", without the make when the model already says it
    /// ("Canon" + "Canon EOS R5" is "Canon EOS R5", and so is "NIKON
    /// CORPORATION" + "NIKON D750" → "NIKON D750").
    /// </summary>
    internal static string? CameraName(string? make, string? model)
    {
        make = string.IsNullOrWhiteSpace(make) ? null : make.Trim();
        model = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
        if (make is null || model is null) return model ?? make;
        if (model.StartsWith(make, StringComparison.OrdinalIgnoreCase)) return model;
        int space = make.IndexOf(' ');
        if (space > 0 && model.StartsWith(make[..space] + " ", StringComparison.OrdinalIgnoreCase)) return model;
        return make + " " + model;
    }

    private static DateTime? Date(Tiff t, long entry)
    {
        var text = Ascii(t, entry);
        if (text is null || text.Length < 19) return null;
        return DateTime.TryParseExact(text[..19], "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture,
                                      DateTimeStyles.None, out var d) ? d : null;
    }

    /// An ASCII value (type 2) of the IFD entry at entry: inline when it
    /// fits in 4 bytes, otherwise at the offset stored there.
    private static string? Ascii(Tiff t, long entry)
    {
        if (entry < 0 || t.U16((int)entry + 2) != 2) return null;
        long count = t.U32((int)entry + 4);
        if (count <= 0 || count > 1024) return null;
        long at = count <= 4 ? entry + 8 : t.U32((int)entry + 8);
        if (at < 0 || at + count > t.Data.Length) return null;
        var text = Encoding.ASCII.GetString(t.Data.Slice((int)at, (int)count));
        int nul = text.IndexOf('\0');
        if (nul >= 0) text = text[..nul];
        text = text.Trim();
        return text.Length == 0 ? null : text;
    }

    private readonly ref struct Tiff
    {
        internal readonly ReadOnlySpan<byte> Data;
        private readonly bool _little;

        internal Tiff(ReadOnlySpan<byte> data, bool little)
        {
            Data = data;
            _little = little;
        }

        internal int U16(int at) =>
            at < 0 || at + 2 > Data.Length ? -1
            : _little ? Data[at] | (Data[at + 1] << 8) : (Data[at] << 8) | Data[at + 1];

        internal long U32(int at) =>
            at < 0 || at + 4 > Data.Length ? -1
            : _little
                ? Data[at] | ((long)Data[at + 1] << 8) | ((long)Data[at + 2] << 16) | ((long)Data[at + 3] << 24)
                : ((long)Data[at] << 24) | ((long)Data[at + 1] << 16) | ((long)Data[at + 2] << 8) | Data[at + 3];

        /// The offset of the 12-byte entry for tag in the IFD at ifd, or -1.
        internal long Find(long ifd, int tag)
        {
            if (ifd < 0 || ifd + 2 > Data.Length) return -1;
            int count = U16((int)ifd);
            for (int i = 0; i < count; i++)
            {
                long entry = ifd + 2 + i * 12L;
                if (entry + 12 > Data.Length) return -1;
                if (U16((int)entry) == tag) return entry;
            }
            return -1;
        }
    }
}

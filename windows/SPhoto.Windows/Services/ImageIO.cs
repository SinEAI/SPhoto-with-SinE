using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace SPhoto.Services;

/// <summary>Decoded pixels, 4 bytes per pixel, upright.</summary>
internal sealed record Pixels(byte[] Data, int Width, int Height);

/// <summary>What the info pane shows about a photo.</summary>
internal sealed record PhotoDetails(
    int Width, int Height, long Bytes, DateTimeOffset? Taken, string? Camera);

/// <summary>
/// Reads and writes photos with the Windows imaging codecs
/// (Windows.Graphics.Imaging), the same ones the Photos app uses, so every
/// format with an installed codec (JPEG, PNG, HEIC, WebP...) opens.
/// </summary>
internal static class ImageIO
{
    /// The largest side the editor works at: the GPU texture limit.
    internal const int MaxEditSide = 16384;

    internal static async Task<BitmapDecoder> OpenAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        IRandomAccessStream stream = await file.OpenReadAsync();
        return await BitmapDecoder.CreateAsync(stream);
    }

    /// <summary>
    /// Decodes the photo upright (EXIF orientation applied), scaled down so
    /// that it fits in maxSide x maxSide. bgra gives premultiplied BGRA for
    /// display, otherwise straight RGBA for the core.
    /// </summary>
    internal static async Task<Pixels> DecodeAsync(string path, int maxSide, bool bgra)
        => (await DecodeWithSizeAsync(path, maxSide, bgra)).Pixels;

    /// The decoded working image and the original upright dimensions, from one file opening.
    internal static async Task<(Pixels Pixels, int FullWidth, int FullHeight)> DecodeWithSizeAsync(string path, int maxSide, bool bgra)
    {
        // Read the actual file, with an explicit lifetime for the decoder's
        // stream. No Explorer thumbnail provider participates in this path.
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var stream = file.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        int orientation = await OrientationAsync(decoder);
        // Scale in the stored orientation, where the transform is
        // unambiguous, and turn the pixels upright afterwards.
        int w = (int)decoder.PixelWidth, h = (int)decoder.PixelHeight;
        double scale = Math.Min(1.0, (double)maxSide / Math.Max(w, h));
        int sw = Math.Max(1, (int)Math.Round(w * scale)), sh = Math.Max(1, (int)Math.Round(h * scale));
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)sw,
            ScaledHeight = (uint)sh,
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        var data = await decoder.GetPixelDataAsync(
            bgra ? BitmapPixelFormat.Bgra8 : BitmapPixelFormat.Rgba8,
            bgra ? BitmapAlphaMode.Premultiplied : BitmapAlphaMode.Straight,
            transform, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.ColorManageToSRgb);
        var pixels = Orient(new Pixels(data.DetachPixelData(), sw, sh), orientation);
        return orientation >= 5 ? (pixels, h, w) : (pixels, w, h);
    }

    /// <summary>
    /// Decodes a photo for the screen: upright, at least maxSide on its
    /// longer side when it is that big, with the full upright size, from
    /// one opening of the file. Scales by a power of two, which JPEG
    /// decodes directly at and which bilinear averages cleanly, and leaves
    /// the rest of the way down to the GPU.
    /// </summary>
    internal static async Task<(Pixels Pixels, int FullWidth, int FullHeight)> DecodeForViewAsync(string path, int maxSide)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var stream = file.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        int orientation = await OrientationAsync(decoder);
        int w = (int)decoder.PixelWidth, h = (int)decoder.PixelHeight;
        double scale = 1;
        while (Math.Max(w, h) * scale / 2 >= maxSide) scale /= 2;
        int sw = Math.Max(1, (int)Math.Round(w * scale)), sh = Math.Max(1, (int)Math.Round(h * scale));
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)sw,
            ScaledHeight = (uint)sh,
            // Halving bilinearly averages each 2x2 block; further down Fant
            // keeps fine detail from aliasing.
            InterpolationMode = scale >= 0.5 ? BitmapInterpolationMode.Linear : BitmapInterpolationMode.Fant,
        };
        var data = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            transform, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.ColorManageToSRgb);
        var pixels = Orient(new Pixels(data.DetachPixelData(), sw, sh), orientation);
        return orientation >= 5 ? (pixels, h, w) : (pixels, w, h);
    }

    /// The upright size of the photo without decoding it.
    internal static async Task<(int Width, int Height)> SizeAsync(string path)
    {
        var decoder = await OpenAsync(path);
        int orientation = await OrientationAsync(decoder);
        return orientation >= 5
            ? ((int)decoder.PixelHeight, (int)decoder.PixelWidth)
            : ((int)decoder.PixelWidth, (int)decoder.PixelHeight);
    }

    private static async Task<int> OrientationAsync(BitmapDecoder decoder)
    {
        try
        {
            var props = await decoder.BitmapProperties.GetPropertiesAsync(new[] { "System.Photo.Orientation" });
            if (props.TryGetValue("System.Photo.Orientation", out var value) && value.Value is ushort o && o is >= 1 and <= 8)
            {
                return o;
            }
        }
        catch (Exception)
        {
            // Formats without metadata (BMP, some PNGs) throw: upright.
        }
        return 1;
    }

    /// <summary>Applies an EXIF orientation (1-8) to 4-byte pixels.</summary>
    internal static Pixels Orient(Pixels p, int orientation)
    {
        if (orientation <= 1 || orientation > 8) return p;
        bool swap = orientation >= 5;
        int w = p.Width, h = p.Height;
        int ow = swap ? h : w, oh = swap ? w : h;
        var src = ToPixels(p.Data);
        var outPixels = new int[src.Length];
        System.Threading.Tasks.Parallel.For(0, oh, y =>
        {
            for (int x = 0; x < ow; x++)
            {
                // Where output (x, y) comes from in the stored image.
                int sx, sy;
                switch (orientation)
                {
                    case 2: sx = w - 1 - x; sy = y; break;              // mirrored
                    case 3: sx = w - 1 - x; sy = h - 1 - y; break;      // 180
                    case 4: sx = x; sy = h - 1 - y; break;              // mirrored vertically
                    case 5: sx = y; sy = x; break;                      // transposed
                    case 6: sx = y; sy = h - 1 - x; break;              // 90 clockwise
                    case 7: sx = w - 1 - y; sy = h - 1 - x; break;      // transversed
                    default: sx = w - 1 - y; sy = x; break;             // 8: 90 anticlockwise
                }
                outPixels[y * ow + x] = src[sy * w + sx];
            }
        });
        var dst = new byte[p.Data.Length];
        System.Buffer.BlockCopy(outPixels, 0, dst, 0, dst.Length);
        return new Pixels(dst, ow, oh);
    }

    /// The bytes as one int per pixel, so a pixel moves in one step.
    private static int[] ToPixels(byte[] bytes)
    {
        var ints = new int[bytes.Length / 4];
        System.Buffer.BlockCopy(bytes, 0, ints, 0, ints.Length * 4);
        return ints;
    }

    internal static async Task<PhotoDetails> DetailsAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        var basic = await file.GetBasicPropertiesAsync();
        DateTimeOffset? taken = null;
        string? camera = null;
        try
        {
            var image = await file.Properties.GetImagePropertiesAsync();
            if (image.DateTaken.Year > 1601) taken = image.DateTaken;
            camera = $"{image.CameraManufacturer} {image.CameraModel}".Trim();
            if (camera.Length == 0) camera = null;
        }
        catch (Exception)
        {
            // Not every format carries photo properties.
        }
        var (w, h) = await SizeAsync(path);
        return new PhotoDetails(w, h, (long)basic.Size, taken, camera);
    }

    internal static bool IsPng(string path) =>
        string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Writes RGBA pixels as a JPEG (or PNG when the name ends in .png),
    /// copying the shooting date and camera from the original when given.
    /// </summary>
    internal static async Task EncodeAsync(Pixels rgba, string path, string? metadataFrom)
    {
        // Metadata first: reading it after the new file exists could read
        // the new file when the two paths are the same.
        var metadata = new List<KeyValuePair<string, BitmapTypedValue>>();
        if (metadataFrom is not null && !IsPng(path))
        {
            try
            {
                var source = await OpenAsync(metadataFrom);
                var keys = new[] { "System.Photo.DateTaken", "System.Photo.CameraManufacturer", "System.Photo.CameraModel" };
                var props = await source.BitmapProperties.GetPropertiesAsync(keys);
                foreach (var key in keys)
                {
                    if (props.TryGetValue(key, out var value)) metadata.Add(new(key, value));
                }
            }
            catch (Exception)
            {
                // A copy without the date is still a good copy.
            }
        }

        var temp = path + ".sphoto-tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.ReadWrite))
            {
                var ras = stream.AsRandomAccessStream();
                BitmapEncoder encoder;
                if (IsPng(path))
                {
                    encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, ras);
                }
                else
                {
                    var options = new BitmapPropertySet
                    {
                        { "ImageQuality", new BitmapTypedValue(0.92, PropertyType.Single) },
                    };
                    encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, ras, options);
                }
                encoder.SetPixelData(BitmapPixelFormat.Rgba8,
                                     IsPng(path) ? BitmapAlphaMode.Straight : BitmapAlphaMode.Ignore,
                                     (uint)rgba.Width, (uint)rgba.Height, 96, 96, rgba.Data);
                foreach (var item in metadata)
                {
                    try
                    {
                        var set = new BitmapPropertySet { { item.Key, item.Value } };
                        await encoder.BitmapProperties.SetPropertiesAsync(set);
                    }
                    catch (Exception)
                    {
                        // Skip what this format cannot hold.
                    }
                }
                await encoder.FlushAsync();
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}

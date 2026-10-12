using System;

namespace SPhoto.Library;

/// <summary>
/// Cheap measures on a small grayscale copy of a photo, used to pick the
/// best shot of a burst as the cover of its stack.
/// </summary>
public static class ImageStats
{
    /// Pixel values at or beyond these count as clipped shadows or highlights.
    public const byte ClipLow = 4, ClipHigh = 251;

    /// <summary>
    /// The variance of a 3x3 Laplacian: high for crisp edges, low for blur
    /// or camera shake. Only comparable between images of the same size.
    /// </summary>
    public static float Sharpness(byte[] gray, int w, int h)
    {
        Check(gray, w, h);
        if (w < 3 || h < 3) return 0;
        double sum = 0, sumSq = 0;
        long n = 0;
        for (int y = 1; y < h - 1; y++)
        {
            int row = y * w;
            for (int x = 1; x < w - 1; x++)
            {
                int i = row + x;
                int lap = gray[i - w] + gray[i + w] + gray[i - 1] + gray[i + 1] - 4 * gray[i];
                sum += lap;
                sumSq += (double)lap * lap;
                n++;
            }
        }
        double mean = sum / n;
        return (float)Math.Max(0, sumSq / n - mean * mean);
    }

    /// <summary>
    /// 0..1, 1 when well exposed: the mean is near mid-grey and few pixels
    /// are crushed to black or blown to white.
    /// </summary>
    public static float Exposure(byte[] gray, int w, int h)
    {
        Check(gray, w, h);
        int n = w * h;
        if (n == 0) return 0;
        long sum = 0;
        int clipped = 0;
        for (int i = 0; i < n; i++)
        {
            byte v = gray[i];
            sum += v;
            if (v <= ClipLow || v >= ClipHigh) clipped++;
        }
        // 1 at mid-grey, 0 for an all-black or all-white frame.
        double offset = (sum / (double)n / 255.0 - 0.5) / 0.5;
        double meanScore = 1 - offset * offset;
        // A quarter of the frame clipped already costs half the score; dark
        // backgrounds and bright skies are normal, so it is not stricter.
        double clipScore = 1 - Math.Min(1, 2.0 * clipped / n);
        return (float)Math.Clamp(meanScore * clipScore, 0, 1);
    }

    /// <summary>
    /// One number for "better shot": sharpness counts most (log-scaled so a
    /// busy scene does not swamp everything), exposure scales it by up to 40%.
    /// </summary>
    public static float Quality(float sharpness, float exposure) =>
        (float)(Math.Log(1 + Math.Max(0, sharpness)) * (0.6 + 0.4 * Math.Clamp(exposure, 0, 1)));

    private static void Check(byte[] gray, int w, int h)
    {
        if (w < 0 || h < 0 || gray.Length < w * h) throw new ArgumentException("gray must hold w x h pixels");
    }
}

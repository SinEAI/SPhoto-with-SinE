using System;
using System.Numerics;

namespace SPhoto.Library;

/// <summary>
/// Difference hash: 64 bits that say, for a tiny 9x8 version of the image,
/// whether each pixel is darker than its right neighbour. Shots of the same
/// scene a moment apart differ in a few bits; different scenes in about half.
/// </summary>
public static class DHash
{
    public const int Columns = 9, Rows = 8;

    /// gray is one byte per pixel, row by row, width x height.
    public static ulong Compute(byte[] gray, int width, int height)
    {
        if (width <= 0 || height <= 0 || gray.Length < width * height)
        {
            throw new ArgumentException("gray must hold width x height pixels");
        }
        Span<double> cells = stackalloc double[Columns * Rows];
        for (int cy = 0; cy < Rows; cy++)
        {
            var (y0, y1) = Span(cy, Rows, height);
            for (int cx = 0; cx < Columns; cx++)
            {
                var (x0, x1) = Span(cx, Columns, width);
                long sum = 0;
                for (int y = y0; y < y1; y++)
                {
                    int row = y * width;
                    for (int x = x0; x < x1; x++) sum += gray[row + x];
                }
                cells[cy * Columns + cx] = (double)sum / ((y1 - y0) * (x1 - x0));
            }
        }
        ulong hash = 0;
        for (int y = 0; y < Rows; y++)
        {
            for (int x = 0; x < Columns - 1; x++)
            {
                if (cells[y * Columns + x] < cells[y * Columns + x + 1]) hash |= 1UL << (y * 8 + x);
            }
        }
        return hash;
    }

    /// The source pixels box i of n covers; never empty, also for images
    /// narrower than the grid.
    private static (int Start, int End) Span(int i, int n, int size)
    {
        int start = (int)((long)i * size / n);
        int end = (int)((long)(i + 1) * size / n);
        if (start >= size) start = size - 1;
        if (end <= start) end = start + 1;
        return (start, end);
    }

    /// The number of differing bits: 0 identical, about 32 unrelated.
    public static int Distance(ulong a, ulong b) => BitOperations.PopCount(a ^ b);
}

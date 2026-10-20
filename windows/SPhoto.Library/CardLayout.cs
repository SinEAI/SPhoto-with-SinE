using System;
using System.Collections.Generic;

namespace SPhoto.Library;

/// <summary>
/// Where a page's cards go, as with home screen widgets: a grid of equal
/// cells, each card 1×1, 2×1, 2×2 or 4×2 of them, every card at the first
/// place from the top left where it fits, so mixed sizes leave as few
/// holes as possible. Pure arithmetic, so it is tested without a window.
/// </summary>
public static class CardLayout
{
    /// <summary>
    /// How many columns of at least min fit in width, and how wide each is
    /// when they share all of it, snapped down to whole device pixels so
    /// the columns never add up to more than the row.
    /// </summary>
    public static (int Columns, double Cell) Fit(double width, double min, double gap, double scale)
    {
        int columns = Math.Max(1, (int)Math.Floor((width + gap + 0.01) / (min + gap)));
        double cell = Math.Floor((width - gap * (columns - 1)) / columns * scale) / scale;
        return (columns, Math.Max(cell, 1));
    }

    /// Columns and rows a card of this size takes, stepped down to fit the columns there are.
    public static (int Columns, int Rows) Span(CardSize size, int columns)
    {
        var (c, r) = size switch
        {
            CardSize.Medium => (2, 1),
            CardSize.Large => (2, 2),
            CardSize.ExtraLarge => (4, 2),
            _ => (1, 1),
        };
        // 4×2 becomes 2×1 (still wide), 2×1 and 2×2 become 1×1.
        if (c > columns) (c, r) = c == 4 && columns >= 2 ? (2, 1) : (1, 1);
        return (c, r);
    }

    /// Each card's place in cells, in order: the first free place from the top left where it fits.
    public static List<(int Column, int Row, int Columns, int Rows)> Pack(IReadOnlyList<CardSize> sizes, int columns)
    {
        columns = Math.Max(1, columns);
        var used = new List<bool[]>();
        bool Free(int row, int col, int c, int r)
        {
            for (int y = row; y < Math.Min(row + r, used.Count); y++)
            {
                for (int x = col; x < col + c; x++)
                    if (used[y][x]) return false;
            }
            return true;
        }
        var result = new List<(int, int, int, int)>(sizes.Count);
        foreach (var size in sizes)
        {
            var (c, r) = Span(size, columns);
            for (int row = 0; ; row++)
            {
                int found = -1;
                for (int col = 0; col + c <= columns && found < 0; col++)
                    if (Free(row, col, c, r)) found = col;
                if (found < 0) continue;
                while (used.Count < row + r) used.Add(new bool[columns]);
                for (int y = row; y < row + r; y++)
                    for (int x = found; x < found + c; x++) used[y][x] = true;
                result.Add((found, row, c, r));
                break;
            }
        }
        return result;
    }
}

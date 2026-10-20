using Xunit;

namespace SPhoto.Library.Tests;

public class CardLayoutTests
{
    [Theory]
    // Columns of at least 196 with 12 between them, sharing the whole
    // width, each snapped down to whole device pixels.
    [InlineData(1000, 1.0, 4, 241)]
    [InlineData(1100, 1.0, 5, 210)]
    [InlineData(150, 1.0, 1, 150)]
    [InlineData(1000, 1.25, 4, 240.8)]
    [InlineData(1001, 1.5, 4, 240.67)]
    public void ColumnsFillTheWidth(double width, double scale, int columns, double cell)
    {
        var fit = CardLayout.Fit(width, 196, 12, scale);
        Assert.Equal(columns, fit.Columns);
        Assert.Equal(cell, fit.Cell, 2);
        double used = fit.Cell * fit.Columns + 12 * (fit.Columns - 1);
        Assert.True(used <= width + 1e-9, "never wider than the row");
        Assert.True(width - used < fit.Columns / scale + 1e-9, "no more than a device pixel per column left over");
    }

    [Fact]
    public void WideningTheWindowNeverLeavesACellsWorthOfGap()
    {
        for (double width = 200; width < 2000; width += 7)
        {
            var (columns, cell) = CardLayout.Fit(width, 196, 12, 1);
            Assert.True(cell >= 196 || columns == 1);
            Assert.True(width - (cell * columns + 12 * (columns - 1)) < columns);
        }
    }

    [Fact]
    public void BigCardsStepDownWhenTheColumnsAreFew()
    {
        Assert.Equal((4, 2), CardLayout.Span(CardSize.ExtraLarge, 5));
        Assert.Equal((2, 1), CardLayout.Span(CardSize.ExtraLarge, 3));
        Assert.Equal((1, 1), CardLayout.Span(CardSize.ExtraLarge, 1));
        Assert.Equal((2, 2), CardLayout.Span(CardSize.Large, 2));
        Assert.Equal((1, 1), CardLayout.Span(CardSize.Large, 1));
        Assert.Equal((1, 1), CardLayout.Span(CardSize.Medium, 1));
    }

    [Fact]
    public void CardsGoToTheFirstPlaceTheyFit()
    {
        var places = CardLayout.Pack(new[]
        {
            CardSize.Small, CardSize.Large, CardSize.Medium, CardSize.Small, CardSize.Small, CardSize.ExtraLarge, CardSize.Small,
        }, 4);
        Assert.Equal(new[]
        {
            (0, 0, 1, 1),  // small, top left
            (1, 0, 2, 2),  // large beside it
            (0, 2, 2, 1),  // medium: no 2×1 gap in rows 0-1, so below
            (3, 0, 1, 1),  // small fills the top right
            (0, 1, 1, 1),  // and the hole under the first small
            (0, 3, 4, 2),  // extra large takes a full row pair
            (3, 1, 1, 1),  // the last small goes back up into the hole
        }, places);
    }

    [Fact]
    public void EveryCardHasItsOwnCells()
    {
        var sizes = Enumerable.Range(0, 40).Select(i => (CardSize)(i * 7 % 4)).ToList();
        foreach (int columns in new[] { 1, 2, 3, 4, 6 })
        {
            var taken = new HashSet<(int, int)>();
            foreach (var (x, y, c, r) in CardLayout.Pack(sizes, columns))
            {
                Assert.True(x >= 0 && x + c <= columns);
                for (int i = x; i < x + c; i++)
                    for (int j = y; j < y + r; j++) Assert.True(taken.Add((i, j)), $"cell {i},{j} used twice at {columns} columns");
            }
        }
    }
}

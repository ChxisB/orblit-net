namespace Orblit.Net.Tests;

public sealed class ActiveCellsTests
{
    private TimeSpan _now = TimeSpan.Zero;
    private ActiveCells _active;

    public ActiveCellsTests() => _active = Build();

    private ActiveCells Build(int radius = 1, int hold = 5) =>
        new(new Grid(10), radius, TimeSpan.FromSeconds(hold), () => _now);

    private void At(int seconds, params Cell[] focus)
    {
        _now = TimeSpan.FromSeconds(seconds);
        _active.Update(focus);
    }

    [Fact]
    public void ACellTurnsActiveWhenAClientArrivesAndSoDoesItsRing()
    {
        Assert.False(_active.IsActive(new Cell(0, 0)));

        At(0, new Cell(0, 0));

        Assert.True(_active.IsActive(new Cell(0, 0)));
        Assert.True(_active.IsActive(new Cell(1, -1)));
        Assert.False(_active.IsActive(new Cell(2, 0)));
        Assert.Equal(9, _active.Cells.Count());
    }

    [Fact]
    public void ItStaysActiveThroughHoldAfterTheClientLeaves()
    {
        At(0, new Cell(0, 0));

        At(4);
        Assert.True(_active.IsActive(new Cell(0, 0)));
        At(5);
        Assert.True(_active.IsActive(new Cell(0, 0)));
        At(6);
        Assert.False(_active.IsActive(new Cell(0, 0)));
        Assert.Empty(_active.Cells);
    }

    [Fact]
    public void TwoClientsKeepACellActiveBetweenThemUntilBothAreGone()
    {
        At(0, new Cell(0, 0), new Cell(2, 0));
        Assert.True(_active.IsActive(new Cell(1, 0)));

        At(10, new Cell(2, 0));
        Assert.True(_active.IsActive(new Cell(1, 0)));
        Assert.False(_active.IsActive(new Cell(-1, 0)));

        At(15);
        Assert.True(_active.IsActive(new Cell(1, 0)));
        At(16);
        Assert.False(_active.IsActive(new Cell(1, 0)));
    }

    [Fact]
    public void AFocusSteppingBackAndForthDoesNotStopTheCell()
    {
        At(0, new Cell(0, 0));
        At(3, new Cell(5, 5));
        At(4, new Cell(0, 0));
        At(8, new Cell(5, 5));

        Assert.True(_active.IsActive(new Cell(0, 0)));
        At(9, new Cell(5, 5));
        Assert.True(_active.IsActive(new Cell(0, 0)));
        At(10, new Cell(5, 5));
        Assert.False(_active.IsActive(new Cell(0, 0)));
    }

    [Fact]
    public void ARadiusOfZeroIsTheFocusCellAlone()
    {
        _active = Build(radius: 0);
        At(0, new Cell(3, 4));

        Assert.Equal([new Cell(3, 4)], _active.Cells);
    }

    [Fact]
    public void AskingByPlaceFindsTheCellAndABadPlaceIsNotActive()
    {
        At(0, new Cell(0, 0));

        Assert.True(_active.IsActiveAt(5, 5));
        Assert.True(_active.IsActiveAt(-5, 15));
        Assert.False(_active.IsActiveAt(25, 5));
        Assert.False(_active.IsActiveAt(double.NaN, 5));
    }

    [Fact]
    public void ARadiusBelowZeroOrAHoldBelowZeroIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ActiveCells(new Grid(10), -1, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ActiveCells(new Grid(10), 1, TimeSpan.FromSeconds(-1)));
    }
}

namespace Orblit.Net.Tests;

public sealed class BudgetTests
{
    private static readonly InboundBudget Budget = new(
        messagesPerTick: 3,
        bytesPerMessage: 10,
        bytesPerTick: 20,
        strikes: 2);

    [Fact]
    public void AMessageOverBytesPerMessageIsRefusedOnItsOwn()
    {
        var meter = new BudgetMeter();

        Assert.True(meter.Allows(Budget, 10));
        Assert.False(meter.Allows(Budget, 11));
    }

    [Fact]
    public void MessagesPerTickRefusesTheMessageAfterTheLastAllowed()
    {
        var meter = new BudgetMeter();
        for (var i = 0; i < 3; i++)
        {
            Assert.True(meter.Allows(Budget, 1));
            meter.Count(1);
        }

        Assert.False(meter.Allows(Budget, 1));
    }

    [Fact]
    public void BytesPerTickRefusesWhatWouldPassTheTotal()
    {
        var meter = new BudgetMeter();
        meter.Count(10);
        meter.Count(8);

        Assert.True(meter.Allows(Budget, 2));
        Assert.False(meter.Allows(Budget, 3));
    }

    [Fact]
    public void EndTickStartsANewWindow()
    {
        var meter = new BudgetMeter();
        for (var i = 0; i < 3; i++)
        {
            meter.Count(1);
        }

        meter.EndTick();

        Assert.True(meter.Allows(Budget, 1));
    }

    [Fact]
    public void ARefusedTickAddsAStrikeAndACleanTickForgivesOne()
    {
        var meter = new BudgetMeter();
        meter.Refuse();
        meter.EndTick();
        Assert.False(meter.IsOver(Budget));

        meter.EndTick();
        meter.Refuse();
        meter.EndTick();
        Assert.False(meter.IsOver(Budget));

        meter.Refuse();
        meter.EndTick();
        Assert.True(meter.IsOver(Budget));
    }

    [Fact]
    public void ForgivingACleanTickStopsAtZero()
    {
        var meter = new BudgetMeter();
        for (var i = 0; i < 5; i++)
        {
            meter.EndTick();
        }

        meter.Refuse();
        meter.EndTick();

        Assert.False(meter.IsOver(Budget));
    }

    [Fact]
    public void ALimitUnderOneIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new InboundBudget(messagesPerTick: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new InboundBudget(bytesPerMessage: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new InboundBudget(bytesPerTick: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new InboundBudget(strikes: 0));
    }
}

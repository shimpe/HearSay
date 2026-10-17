using HearSay.OperatorMatrix.Priors;

namespace HearSay.OperatorMatrix.Tests;

public sealed class RemainingTimeTests
{
    private sealed class Clock
    {
        public double Seconds { get; set; }
    }

    private static (Clock Clock, RemainingTime Remaining) Start()
    {
        var clock = new Clock();
        return (clock, new RemainingTime(() => TimeSpan.FromSeconds(clock.Seconds)));
    }

    [Fact]
    public void Estimate_ExtrapolatesThePaceSoFar()
    {
        (Clock clock, RemainingTime remaining) = Start();
        Assert.Null(remaining.Estimate(0.0));

        // A quarter done in 2 minutes: 6 minutes to go.
        clock.Seconds = 120;
        Assert.Equal(TimeSpan.FromMinutes(6), remaining.Estimate(0.25));
        Assert.Equal(TimeSpan.Zero, remaining.Estimate(1.0));
    }

    [Fact]
    public void Estimate_OfAJobJoinedHalfWay_TakesThePaceFromWhereItWasJoined()
    {
        (Clock clock, RemainingTime remaining) = Start();
        clock.Seconds = 10;
        Assert.Null(remaining.Estimate(0.5));

        // 10 % more in a minute: the last 40 % take four.
        clock.Seconds = 70;
        Assert.Equal(TimeSpan.FromMinutes(4), remaining.Estimate(0.6));
    }

    [Fact]
    public void Estimate_WaitsUntilThePaceIsKnown()
    {
        (Clock clock, RemainingTime remaining) = Start();
        remaining.Estimate(0.0);
        clock.Seconds = 120;
        Assert.Null(remaining.Estimate(0.01));
        clock.Seconds = 2;
        Assert.Null(remaining.Estimate(0.5));
        clock.Seconds = 5;
        Assert.NotNull(remaining.Estimate(0.02));
    }

    [Theory]
    [InlineData(0, 0.0, "0% done, estimating the time left")]
    [InlineData(120, 0.25, "25% done, about 6 min left")]
    [InlineData(240, 0.375, "37% done, about 7 min left")]
    [InlineData(270, 0.9, "90% done, less than a minute left")]
    [InlineData(600, 0.1, "10% done, about 1 h 30 min left")]
    public void Describe_GivesTheShareDoneAndTheTimeLeft(double seconds, double fraction, string expected)
    {
        (Clock clock, RemainingTime remaining) = Start();
        remaining.Describe(0.0);
        clock.Seconds = seconds;
        Assert.Equal(expected, remaining.Describe(fraction));
    }
}

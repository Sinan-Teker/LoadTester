using LoadTester.Core.Scenarios;

namespace LoadTester.Core.Tests;

public class LoadProfileTests
{
    private static LoadProfile RampThenHold() => new()
    {
        StartTarget = 0,
        Stages = [new LoadStage(10, 10), new LoadStage(30, 10), new LoadStage(10, 0)],
    };

    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, 5)]
    [InlineData(10, 10)]
    [InlineData(25, 10)]
    [InlineData(45, 5)]
    [InlineData(50, 0)]
    [InlineData(80, 0)]
    public void TargetAt_InterpolatesWithinStages(double seconds, double expected)
    {
        Assert.Equal(expected, RampThenHold().TargetAt(seconds), precision: 6);
    }

    [Fact]
    public void ZeroLengthStage_JumpsImmediately()
    {
        var profile = new LoadProfile { Stages = [new LoadStage(0, 20), new LoadStage(10, 20)] };

        Assert.Equal(20, profile.TargetAt(0));
        Assert.Equal(20, profile.VirtualUsersAt(5));
        Assert.Equal(10, profile.TotalSeconds);
    }

    [Fact]
    public void ArrivalsUntil_IsTheAreaUnderTheRateCurve()
    {
        var constant = new LoadProfile { StartTarget = 10, Stages = [new LoadStage(10, 10)] };
        var ramp = new LoadProfile { Stages = [new LoadStage(10, 10)] };

        Assert.Equal(50, constant.ArrivalsUntil(5), precision: 6);
        Assert.Equal(100, constant.ArrivalsUntil(10), precision: 6);
        Assert.Equal(100, constant.ArrivalsUntil(60), precision: 6);
        Assert.Equal(12.5, ramp.ArrivalsUntil(5), precision: 6);
        Assert.Equal(50, ramp.ArrivalsUntil(10), precision: 6);
    }

    [Fact]
    public void PeakTarget_IncludesStartValue()
    {
        Assert.Equal(10, RampThenHold().PeakTarget);
        Assert.Equal(40, new LoadProfile { StartTarget = 40, Stages = [new LoadStage(5, 0)] }.PeakTarget);
    }
}

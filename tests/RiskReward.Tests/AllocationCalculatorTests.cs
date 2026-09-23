using RiskReward.Core;

namespace RiskReward.Tests;

public sealed class AllocationCalculatorTests
{
    [Fact] public void UpperLineIsZeroPercent() => Assert.Equal(0m, AllocationCalculator.Calculate(100m, 25m, 100m));
    [Fact] public void LowerLineIsTenPercent() => Assert.Equal(10m, AllocationCalculator.Calculate(25m, 25m, 100m));
    [Fact] public void GeometricMidpointIsFivePercent() => Assert.Equal(5m, AllocationCalculator.Calculate(50m, 25m, 100m));
    [Fact] public void AboveAndBelowRangeAreClamped()
    {
        Assert.Equal(0m, AllocationCalculator.Calculate(200m, 25m, 100m));
        Assert.Equal(10m, AllocationCalculator.Calculate(10m, 25m, 100m));
    }
    [Theory][InlineData(0, 10, 20)][InlineData(10, 20, 20)][InlineData(10, 30, 20)]
    public void RejectsInvalidValues(decimal current, decimal lower, decimal upper) => Assert.ThrowsAny<ArgumentException>(() => AllocationCalculator.Calculate(current, lower, upper));
}

using ExpertCar.Core;

namespace ExpertCar.Tests;

public class CoreTests
{
    [Fact]
    public void AmountAndQuantityRejectExcessPrecision()
    {
        Assert.Equal(1234, Pricing.ToMinor(12.34m));
        Assert.Equal(1234, Pricing.ToScaledQuantity(1.234m));
        Assert.Throws<ArgumentOutOfRangeException>(() => Pricing.ToMinor(12.345m));
        Assert.Throws<ArgumentOutOfRangeException>(() => Pricing.ToScaledQuantity(1.2345m));
    }

    [Fact]
    public void LineAndOrderUseOneRoundingRule()
    {
        Assert.Equal(1, Pricing.LineTotal(500, 1));
        Assert.Equal(0, Pricing.LineTotal(499, 1));
        var totals = Pricing.Calculate([Pricing.LineTotal(1500, 101), 100], 1, 0.05m);
        Assert.Equal(new OrderTotals(252, 1, 13, 264), totals);
        Assert.Throws<ArgumentOutOfRangeException>(() => Pricing.Calculate([10], 11, 0.05m));
        Assert.Throws<ArgumentOutOfRangeException>(() => Pricing.LineTotal(1000, 100, 101));
    }

    [Fact]
    public void WorkOrderStateMachineRejectsBackwardAndTerminalMoves()
    {
        Assert.Equal(WorkOrderStatus.InProgress,
            WorkOrderTransitions.Move(WorkOrderStatus.Estimate, WorkOrderStatus.InProgress));
        Assert.Equal(WorkOrderStatus.Completed,
            WorkOrderTransitions.Move(WorkOrderStatus.InProgress, WorkOrderStatus.Completed));
        Assert.Equal(WorkOrderStatus.Settled,
            WorkOrderTransitions.Move(WorkOrderStatus.Completed, WorkOrderStatus.Settled));
        Assert.Throws<InvalidOperationException>(() =>
            WorkOrderTransitions.Move(WorkOrderStatus.Settled, WorkOrderStatus.InProgress));
        Assert.Throws<InvalidOperationException>(() =>
            WorkOrderTransitions.Move(WorkOrderStatus.Estimate, WorkOrderStatus.Settled));
    }

    [Fact]
    public void DataDirectoryIsOutsideProgramDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "local-app-data-fixture");
        var path = AppDataPath.DatabasePath(root);
        Assert.Equal(Path.Combine(root, "ExpertCar", "expertcar.db"), path);
        Assert.False(path.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase));
    }
}

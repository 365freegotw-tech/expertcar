using ExpertCar.Core;
using ExpertCar.Data;

namespace ExpertCar.Tests;

public sealed class V02ContractTests
{
    [Theory]
    [InlineData(false, false, false, 500000, 0)]
    [InlineData(true, false, false, 525000, 0)]
    [InlineData(false, true, true, 515000, 15000)]
    [InlineData(true, true, true, 540800, 15800)]
    [InlineData(true, true, false, 525000, 0)]
    public void CheckoutCases(bool invoice, bool card, bool enabled, long expected, long surcharge)
    {
        var quote = CheckoutPricing.Calculate([new ChargeLine(500000)], 0,
            new CheckoutOptions(invoice, card, enabled, 0.05m, 0.03m));
        Assert.Equal(expected, quote.TotalMinor);
        Assert.Equal(surcharge, quote.CardSurchargeMinor);
        Assert.Equal(invoice ? 25000 : 0, quote.TaxMinor);
        Assert.Equal(enabled && card, quote.CardSurchargeApplied);
        Assert.Equal(0.05m, quote.TaxRate);
        Assert.Equal(0.03m, quote.CardRate);
    }

    [Fact]
    public void UnapprovedAdditionalWorkIsExcludedAndDiscountPrecedesTax()
    {
        var lines = new[] { new ChargeLine(500000), new ChargeLine(100000, true, false) };
        var pending = CheckoutPricing.Calculate(lines, 50000, new CheckoutOptions(true, true, true, 0.05m, 0.03m));
        var accepted = CheckoutPricing.Calculate(lines.Select(line => line with { IsApproved = true }), 50000,
            new CheckoutOptions(true, true, true, 0.05m, 0.03m));
        Assert.Equal(450000, pending.BaseMinor);
        Assert.Equal(22500, pending.TaxMinor);
        Assert.Equal(486700, pending.TotalMinor);
        Assert.Equal(550000, accepted.BaseMinor);
        Assert.True(accepted.TotalMinor > pending.TotalMinor);
    }

    [Fact]
    public void PartialPaymentLeavesDueAndOverpaymentIsRejected()
    {
        Assert.Equal(300000, CheckoutPricing.Remaining(540800, [], 240800));
        Assert.Equal(0, CheckoutPricing.Remaining(540800, [240800], 300000));
        Assert.Throws<InvalidOperationException>(() => CheckoutPricing.Remaining(540800, [240800], 300001));
        Assert.Throws<ArgumentOutOfRangeException>(() => CheckoutPricing.Remaining(540800, [], 0));
    }

    [Fact]
    public async Task MemoryServiceSupportsUiFlowAndPrintSnapshot()
    {
        var vehicle = new VehicleQuickResult(1, 1, "ABC-1234", "王○明", "Toyota", "Altis", 2020, "109", "白", "12345678901234567");
        var service = new MemoryV02Service([new VehicleHistory(vehicle, [], [], [])]);
        var id = await service.SaveWorkOrderAsync(new WorkOrderDraft(null, "WO-001", 1, 1,
            new DateOnly(2026, 11, 20), "異音", "更換零件", null, "客戶簽名", "加修需另行同意",
            [new WorkLineDraft(WorkLineCategory.Labor, "檢修", null, null, "時", 1000, 0,
                LaborMode: LaborPriceMode.Hourly, LaborMinutes: 60, HourlyRateMinor: 500000)], []));
        Assert.Single(await service.QuickFindVehiclesAsync("abc1234"));
        Assert.Equal(500000, (await service.QuoteCheckoutAsync(id, new CheckoutOptions(true, true, true, 0.05m, 0.03m))).BaseMinor);
        await service.TransitionWorkOrderAsync(id, WorkOrderStatus.Estimate, WorkOrderStatus.Accepted);
        await service.TransitionWorkOrderAsync(id, WorkOrderStatus.Accepted, WorkOrderStatus.InProgress);
        await service.TransitionWorkOrderAsync(id, WorkOrderStatus.InProgress, WorkOrderStatus.AwaitingCheckout);
        var first = await service.RecordPaymentAsync(id, "Cash", 200000);
        Assert.Equal(340800, first.DueMinor);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecordPaymentAsync(id, "Card", 340801));
        Assert.Equal(0, (await service.RecordPaymentAsync(id, "Card", 340800)).DueMinor);
        await service.TransitionWorkOrderAsync(id, WorkOrderStatus.AwaitingCheckout, WorkOrderStatus.Settled);
        var snapshot = await service.CreatePrintSnapshotAsync(id, "Receipt");
        Assert.Equal(540800, snapshot.Checkout!.TotalMinor);
        Assert.Equal("12345678901234567", snapshot.Model.Vin);
        Assert.Equal("異音", snapshot.Model.Diagnosis);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteDraftWorkOrderAsync(id));
        var draftId = await service.SaveWorkOrderAsync((await service.GetWorkOrderAsync(id))!.Draft with
        { Id = null, Number = "WO-002" });
        await service.DeleteDraftWorkOrderAsync(draftId);
        Assert.Null(await service.GetWorkOrderAsync(draftId));
    }
}

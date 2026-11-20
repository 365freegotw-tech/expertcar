namespace ExpertCar.Core;

/// <summary>金額以分、數量以千分之一儲存；逐行及稅額採四捨五入（中點遠離零）。</summary>
public static class Pricing
{
    public const long QuantityScale = 1000;

    public static long ToMinor(decimal amount) => ExactScaled(amount, 100);
    public static long ToScaledQuantity(decimal quantity) => ExactScaled(quantity, QuantityScale);

    private static long ExactScaled(decimal value, long scale)
    {
        var scaled = checked(value * scale);
        if (scaled != decimal.Truncate(scaled))
            throw new ArgumentOutOfRangeException(nameof(value), "超出儲存精度");
        return checked((long)scaled);
    }

    public static long RoundMinor(decimal value) => checked((long)decimal.Round(value, 0, MidpointRounding.AwayFromZero));

    public static long LineTotal(long quantityScaled, long unitPriceMinor, long discountMinor = 0)
    {
        if (quantityScaled < 0 || unitPriceMinor < 0 || discountMinor < 0)
            throw new ArgumentOutOfRangeException(nameof(quantityScaled));
        var gross = RoundMinor((decimal)quantityScaled * unitPriceMinor / QuantityScale);
        if (discountMinor > gross) throw new ArgumentOutOfRangeException(nameof(discountMinor));
        return checked(gross - discountMinor);
    }

    public static OrderTotals Calculate(IEnumerable<long> lineTotals, long orderDiscountMinor, decimal taxRate)
    {
        if (taxRate < 0 || taxRate > 1) throw new ArgumentOutOfRangeException(nameof(taxRate));
        var subtotal = lineTotals.Aggregate(0L, (sum, value) =>
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(lineTotals));
            return checked(sum + value);
        });
        if (orderDiscountMinor < 0 || orderDiscountMinor > subtotal)
            throw new ArgumentOutOfRangeException(nameof(orderDiscountMinor));
        var taxable = checked(subtotal - orderDiscountMinor);
        var tax = RoundMinor(taxable * taxRate);
        return new OrderTotals(subtotal, orderDiscountMinor, tax, checked(taxable + tax));
    }
}

public sealed record OrderTotals(long SubtotalMinor, long DiscountMinor, long TaxMinor, long TotalMinor);

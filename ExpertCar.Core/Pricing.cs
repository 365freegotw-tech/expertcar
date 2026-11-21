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

/// <summary>v0.2 計價輸入；金額單位為分，刷卡加成由明確啟用旗標控制。</summary>
public sealed record CheckoutOptions(bool IssueInvoice, bool PayByCard, bool CardSurchargeEnabled = false,
    decimal TaxRate = 0.05m, decimal CardRate = 0m);

/// <summary>單據須保存整份結果，設定日後改變也不重算舊單。</summary>
public sealed record CheckoutQuote(long SubtotalMinor, long DiscountMinor, long BaseMinor, long TaxMinor,
    long CardSurchargeMinor, long TotalMinor, decimal TaxRate, decimal CardRate,
    bool IssueInvoice, bool PayByCard, bool CardSurchargeApplied);

/// <summary>行項金額已按數量千分之一及分四捨五入；加修未同意時排除。</summary>
public sealed record ChargeLine(long TotalMinor, bool IsAdditional = false, bool IsApproved = true);

public static class CheckoutPricing
{
    /// <summary>先合計已同意行項，再折扣、發票稅、刷卡加成；加成四捨五入至整元。</summary>
    public static CheckoutQuote Calculate(IEnumerable<ChargeLine> lines, long discountMinor, CheckoutOptions options)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(options);
        if (options.TaxRate is < 0 or > 1 || options.CardRate is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(options));
        var validated = lines.ToArray();
        if (validated.Any(line => line.TotalMinor < 0)) throw new ArgumentOutOfRangeException(nameof(lines));
        var subtotal = validated.Where(line => !line.IsAdditional || line.IsApproved)
            .Aggregate(0L, (sum, line) => checked(sum + line.TotalMinor));
        if (discountMinor < 0 || discountMinor > subtotal) throw new ArgumentOutOfRangeException(nameof(discountMinor));
        var baseMinor = checked(subtotal - discountMinor);
        var tax = options.IssueInvoice ? Pricing.RoundMinor(baseMinor * options.TaxRate) : 0;
        var cardBase = checked(baseMinor + tax);
        var applied = options.PayByCard && options.CardSurchargeEnabled && options.CardRate > 0;
        var surcharge = applied ? checked(Pricing.RoundMinor(cardBase * options.CardRate / 100m) * 100) : 0;
        return new(subtotal, discountMinor, baseMinor, tax, surcharge, checked(cardBase + surcharge),
            options.TaxRate, options.CardRate, options.IssueInvoice, options.PayByCard, applied);
    }

    /// <summary>計算本次付款後應收；拒絕非正數、負的舊收款及超收。</summary>
    public static long Remaining(long totalMinor, IEnumerable<long> existingPaymentsMinor, long newPaymentMinor)
    {
        if (totalMinor < 0 || newPaymentMinor <= 0) throw new ArgumentOutOfRangeException(nameof(newPaymentMinor));
        var paid = existingPaymentsMinor.Aggregate(0L,
            (sum, value) => value <= 0 ? throw new ArgumentOutOfRangeException(nameof(existingPaymentsMinor)) : checked(sum + value));
        if (paid > totalMinor || newPaymentMinor > totalMinor - paid) throw new InvalidOperationException("收款超過應收金額");
        return totalMinor - paid - newPaymentMinor;
    }
}

namespace ExpertCar.Core;

/// <summary>工單當時的內容快照，畫面與原創列印版型共用，不依賴 WPF 控制項。</summary>
public sealed record WorkOrderDocument(
    string Number,
    WorkOrderStatus Status,
    DateOnly OrderDate,
    string CustomerName,
    string? CustomerAddress,
    string Plate,
    IReadOnlyList<WorkOrderLineDocument> Lines,
    OrderTotals Totals,
    int Revision,
    string? Vin = null,
    string? Diagnosis = null,
    string? RepairDescription = null,
    DateOnly? EstimateExpiresOn = null,
    string? SignatureLabel = null,
    string? AdditionalWorkNotice = null);

public sealed record WorkOrderLineDocument(
    int LineNumber,
    string Kind,
    string Name,
    string? Unit,
    long QuantityScaled,
    long UnitPriceMinor,
    long DiscountMinor,
    long TotalMinor);

public sealed record PrintSnapshot(
    long WorkOrderId,
    string DocumentKind,
    int Revision,
    WorkOrderDocument Model,
    DateTimeOffset CreatedAt,
    CheckoutQuote? Checkout = null);

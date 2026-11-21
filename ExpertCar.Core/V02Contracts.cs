namespace ExpertCar.Core;

public sealed record VehicleQuickResult(long VehicleId, long CustomerId, string Plate, string CustomerName,
    string? Make, string? Model, int? ModelYear, string? RawYear, string? Color, string? Vin);
public sealed record OwnershipPeriod(long CustomerId, string CustomerName, DateOnly StartsOn, DateOnly? EndsOn);
public sealed record VehicleHistory(VehicleQuickResult Vehicle, IReadOnlyList<OwnershipPeriod> Ownerships,
    IReadOnlyList<WorkOrderSummary> Orders, IReadOnlyList<ReminderRow> Reminders);
public sealed record WorkOrderSummary(long Id, string Number, WorkOrderStatus Status, DateOnly OrderDate,
    long TotalMinor, long DueMinor);
public enum WorkLineCategory { Labor, Part, Consumable, Disposal, Outsource, Discount }
public enum LaborPriceMode { Fixed, Hourly }
public sealed record WorkLineDraft(WorkLineCategory Category, string Name, string? Code, string? PartNumber,
    string? Unit, long QuantityScaled, long UnitPriceMinor, long DiscountMinor = 0,
    LaborPriceMode? LaborMode = null, int? LaborMinutes = null, long? HourlyRateMinor = null,
    long? VendorId = null, long? AdditionId = null, string? VendorName = null);
public sealed record AdditionDraft(long? Id, string Title, string? Reason, bool Accepted,
    DateTimeOffset? AcceptedAt = null, string? ConsentMethod = null, string? CustomerName = null);
public sealed record WorkOrderDraft(long? Id, string Number, long CustomerId, long VehicleId,
    DateOnly OrderDate, string? Diagnosis, string? RepairDescription, DateOnly? EstimateExpiresOn,
    string? SignatureLabel, string AdditionalWorkNotice, IReadOnlyList<WorkLineDraft> Lines,
    IReadOnlyList<AdditionDraft> Additions, long OrderDiscountMinor = 0, DateOnly? PromisedDeliveryOn = null);
public sealed record WorkOrderDetail(long Id, WorkOrderDraft Draft, WorkOrderStatus Status,
    int Revision, IReadOnlyList<PaymentEntry> Payments, CheckoutQuote? LastQuote);
public sealed record CatalogSearchQuery(string Text, string? Kind, int Page = 1, int PageSize = 30);
public sealed record CatalogPage(IReadOnlyList<CatalogRow> Items, int TotalCount, int Page, int PageSize);
public sealed record PaymentEntry(long Id, long WorkOrderId, string Method, long AmountMinor,
    DateTimeOffset ReceivedAt, string? Note);
public sealed record ReceivableResult(long WorkOrderId, long TotalMinor, long PaidMinor, long DueMinor);
public sealed record WorkOrderListRow(WorkOrderSummary Order, string Plate, DateOnly? PromisedDeliveryOn);
public sealed record WorkOrderPage(IReadOnlyList<WorkOrderListRow> Items, int TotalCount, int Page, int PageSize);
public sealed record VehicleDetailsEditor(long VehicleId, string? Vin, string? Make, string? Model, int? ModelYear, string? Color);
public sealed record ImportSourceCount(string SourceFile, string SourceTable, int Scanned, int Imported,
    int Skipped, int Pending, int Failed);
public sealed record ImportDrillResult(ImportPreview Summary, IReadOnlyList<ImportSourceCount> Sources);
public sealed record ImportCommitResult(ImportResult Summary, IReadOnlyList<ImportSourceCount> Sources);

/// <summary>v0.2 畫面使用的工作流程契約；與既有 IStorageService 共用同一資料庫。</summary>
public interface IExpertCarV02Service
{
    Task UpdateVehicleDetailsAsync(VehicleDetailsEditor editor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task TransferVehicleAsync(long vehicleId, long newCustomerId, DateOnly effectiveOn, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task<WorkOrderPage> SearchWorkOrdersAsync(int page = 1, int pageSize = 30, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task<IReadOnlyList<WorkOrderListRow>> GetTodayDeliveryOrdersAsync(DateOnly today, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task<ReceivableResult?> GetReceivableAsync(long workOrderId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task<IReadOnlyList<long>> GetSuggestedPartIdsAsync(long serviceItemId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task SaveSuggestedPartIdsAsync(long serviceItemId, IReadOnlyList<long> partIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    /// <summary>依車牌正規化鍵、客戶姓名或電話快查，限制回傳筆數。</summary>
    Task<IReadOnlyList<VehicleQuickResult>> QuickFindVehiclesAsync(string query, int limit = 20, CancellationToken cancellationToken = default);
    /// <summary>取得一輛車的過戶期間、歷史工單與提醒。</summary>
    Task<VehicleHistory?> GetVehicleHistoryAsync(long vehicleId, CancellationToken cancellationToken = default);
    /// <summary>取得工單目前版本與付款紀錄。</summary>
    Task<WorkOrderDetail?> GetWorkOrderAsync(long id, CancellationToken cancellationToken = default);
    /// <summary>新增或更新估價／工單；更新時版本遞增。</summary>
    Task<long> SaveWorkOrderAsync(WorkOrderDraft draft, CancellationToken cancellationToken = default);
    /// <summary>只刪除未同意且未收款的草稿；其他工單用取消狀態保留稽核紀錄。</summary>
    Task DeleteDraftWorkOrderAsync(long id, CancellationToken cancellationToken = default);
    /// <summary>依狀態機轉換工單，非法或過期狀態須拒絕。</summary>
    Task TransitionWorkOrderAsync(long id, WorkOrderStatus expected, WorkOrderStatus next, CancellationToken cancellationToken = default);
    /// <summary>依代碼、名稱及類別搜尋店家目錄，頁碼從一開始。</summary>
    Task<CatalogPage> SearchCatalogAsync(CatalogSearchQuery query, CancellationToken cancellationToken = default);
    /// <summary>用已同意的行項試算結帳；結果含費率及四捨五入快照。</summary>
    Task<CheckoutQuote> QuoteCheckoutAsync(long workOrderId, CheckoutOptions options, CancellationToken cancellationToken = default);
    /// <summary>記錄單筆付款，拒絕超收並回傳最新應收。</summary>
    Task<ReceivableResult> RecordPaymentAsync(long workOrderId, string method, long amountMinor, CancellationToken cancellationToken = default);
    /// <summary>建立不可變的列印模型與計價快照；kind 為 Estimate、WorkOrder 或 Receipt。</summary>
    Task<PrintSnapshot> CreatePrintSnapshotAsync(long workOrderId, string kind, CancellationToken cancellationToken = default);
    /// <summary>僅演練來源資料，回傳各來源計數，不修改正式資料。</summary>
    Task<ImportDrillResult> DrillImportAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default);
    /// <summary>先備份再以交易提交最近一次演練，來源鍵重跑須跳過。</summary>
    Task<ImportCommitResult> CommitDrilledImportAsync(CancellationToken cancellationToken = default);
}

namespace ExpertCar.Core;

// 舊列印 JSON 以 enum 數值保存；v0.1 的值不可重新編號。
public enum WorkOrderStatus
{
    Estimate = 0, InProgress = 1, Completed = 2, Settled = 3, Void = 4, ImportReview = 5,
    Accepted = 6, AwaitingCheckout = 7, Cancelled = 8
}

public static class WorkOrderTransitions
{
    public static bool CanMove(WorkOrderStatus from, WorkOrderStatus to) => (from, to) switch
    {
        (WorkOrderStatus.Estimate, WorkOrderStatus.Accepted or WorkOrderStatus.Cancelled) => true,
        (WorkOrderStatus.Accepted, WorkOrderStatus.InProgress or WorkOrderStatus.Cancelled) => true,
        (WorkOrderStatus.InProgress, WorkOrderStatus.AwaitingCheckout or WorkOrderStatus.Cancelled) => true,
        (WorkOrderStatus.AwaitingCheckout, WorkOrderStatus.Settled or WorkOrderStatus.Cancelled) => true,
        (WorkOrderStatus.ImportReview, WorkOrderStatus.Estimate or WorkOrderStatus.Accepted or WorkOrderStatus.InProgress or WorkOrderStatus.AwaitingCheckout or WorkOrderStatus.Cancelled) => true,
        _ => false
    };

    public static WorkOrderStatus Move(WorkOrderStatus from, WorkOrderStatus to) =>
        CanMove(from, to) ? to : throw new InvalidOperationException($"不可從 {from} 轉為 {to}");
}

public sealed record CustomerDraft(string Code, string Name, string? Address = null);
public sealed record VehicleDraft(string Plate, string PlateKey, string? Model = null);
public sealed record DashboardSummary(long CustomerCount, long VehicleCount, long OpenOrderCount);

/// <summary>應用層只依賴此契約；日後可由區網服務替換本機實作。</summary>
public interface IStorageService
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<DashboardSummary> GetDashboardAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CustomerRow>> SearchCustomersAsync(string query, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<VehicleRow>> GetVehiclesAsync(long customerId, CancellationToken cancellationToken = default);
    Task<long> SaveCustomerAsync(CustomerEditor customer, CancellationToken cancellationToken = default);
    Task<long> SaveVehicleAsync(VehicleEditor vehicle, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CatalogRow>> GetCatalogAsync(CancellationToken cancellationToken = default);
    Task<long> SaveCatalogAsync(CatalogEditor item, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ReminderRow>> GetRemindersAsync(CancellationToken cancellationToken = default);
    Task<ShopSettings> GetShopSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveShopSettingsAsync(ShopSettings settings, CancellationToken cancellationToken = default);
    Task<string> CreateBackupAsync(CancellationToken cancellationToken = default);
    Task<ImportPreview> PreviewImportAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default);
    Task<ImportResult> CommitImportAsync(CancellationToken cancellationToken = default);
    Task<long> CreateCustomerWithVehicleAsync(CustomerDraft customer, VehicleDraft vehicle,
        CancellationToken cancellationToken = default);
}

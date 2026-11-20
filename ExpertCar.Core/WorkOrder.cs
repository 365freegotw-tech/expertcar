namespace ExpertCar.Core;

public enum WorkOrderStatus { Estimate, InProgress, Completed, Settled, Void, ImportReview }

public static class WorkOrderTransitions
{
    public static bool CanMove(WorkOrderStatus from, WorkOrderStatus to) => (from, to) switch
    {
        (WorkOrderStatus.Estimate, WorkOrderStatus.InProgress or WorkOrderStatus.Void) => true,
        (WorkOrderStatus.InProgress, WorkOrderStatus.Completed or WorkOrderStatus.Void) => true,
        (WorkOrderStatus.Completed, WorkOrderStatus.Settled) => true,
        (WorkOrderStatus.ImportReview, WorkOrderStatus.Estimate or WorkOrderStatus.InProgress or WorkOrderStatus.Completed or WorkOrderStatus.Void) => true,
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

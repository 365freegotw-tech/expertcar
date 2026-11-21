namespace ExpertCar.Import;

/// <summary>App 與無 WPF 驗證共用的正式服務組合：同一 SQLite 資料庫及匯入交易。</summary>
public static class ExpertCarServices
{
    public static async Task<ImportV02Service> OpenAsync(string databasePath, CancellationToken cancellationToken = default)
    {
        var service = new ImportV02Service(databasePath);
        try { await service.InitializeAsync(cancellationToken); return service; }
        catch { service.Dispose(); throw; }
    }
}

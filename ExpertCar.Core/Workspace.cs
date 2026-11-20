namespace ExpertCar.Core;

public sealed record CustomerRow(long Id, string Code, string Name, string? Phone, string? Address);
public sealed record VehicleRow(long Id, long CustomerId, string Plate, string? Model, long? OdometerKm, DateOnly? MaintenanceDate, long? MaintenanceKm, DateOnly? InspectionDate);
public sealed record CustomerEditor(long? Id, string Name, string? Phone, string? Address);
public sealed record VehicleEditor(long? Id, long CustomerId, string Plate, string? Model, long? OdometerKm, DateOnly? MaintenanceDate, long? MaintenanceKm, DateOnly? InspectionDate);
public sealed record CatalogRow(long Id, string Kind, string Code, string Name, string Unit, long PriceMinor)
{
    public string Price => (PriceMinor / 100m).ToString("N2", System.Globalization.CultureInfo.CurrentCulture);
}
public sealed record CatalogEditor(long? Id, string Kind, string Code, string Name, string Unit, long PriceMinor);
public sealed record ShopSettings(string Name, string Contact, int ReminderDays, decimal CardFeePercent, decimal TaxPercent)
{
    public static ShopSettings Default => new("", "", 30, 0m, 5m);
}
public sealed record ReminderRow(long VehicleId, long CustomerId, string CustomerName, string? Address, string Plate,
    string Kind, DateOnly? DueDate, long? DueKm, long? OdometerKm);
public sealed record ImportProblem(string File, string Table, long Row, string Code)
{
    public string Description => Code switch
    {
        "MissingColumn" => "來源缺少必要欄位",
        "MissingName" => "缺少客戶姓名",
        "UnlinkedPhone" => "電話無法連結車輛",
        "AmbiguousPhoneLink" => "電話對應多輛車，請核對",
        "DuplicatePlate" => "車牌重複，請核對",
        _ when Code.StartsWith("InvalidDate:", StringComparison.Ordinal) => "日期格式無效，請核對",
        _ when Code.StartsWith("InvalidNumber:", StringComparison.Ordinal) => "數字格式無效，請核對",
        _ => "資料需人工核對"
    };
}
public sealed record ReminderPrintCandidate(long CustomerId, long VehicleId, string Kind, string? Address,
    DateOnly? DueDate, long? DueKm);
public sealed record ImportPreview(int Success, int Skipped, int Pending, int Failed, IReadOnlyList<ImportProblem> Problems, bool AlreadyImported);
public sealed record ImportResult(int Success, int Skipped, int Pending, int Failed, string? BackupPath, long? RunId);

public static class ReminderGrouping
{
    public static string Group(ReminderRow reminder, DateOnly today, int days)
    {
        if (reminder.DueDate is { } date)
        {
            if (date < today) return "逾期";
            if (date == today) return "今日";
            if (days > 0 && date <= today.AddDays(days)) return "期間內";
        }
        return "期間外";
    }

    public static bool MileageDue(ReminderRow reminder) => reminder.DueKm is > 0 &&
        reminder.OdometerKm is { } km && km >= reminder.DueKm.Value;
}

public sealed class WorkspaceViewModel(IStorageService storage)
{
    public async Task<IReadOnlyList<CustomerRow>> SearchAsync(string? query) => await storage.SearchCustomersAsync(query?.Trim() ?? "");
    public async Task<ShopSettings> LoadSettingsAsync() => await storage.GetShopSettingsAsync();
    public async Task SaveSettingsAsync(ShopSettings settings)
    {
        if (settings.ReminderDays < 0 || settings.CardFeePercent < 0 || settings.CardFeePercent > 100 ||
            settings.TaxPercent < 0 || settings.TaxPercent > 100)
            throw new ArgumentOutOfRangeException(nameof(settings), "天數與費率超出範圍");
        await storage.SaveShopSettingsAsync(settings);
    }
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<ReminderRow>>> GetReminderGroupsAsync(DateOnly today, int days)
    {
        if (days < 0 || days > 3650) throw new ArgumentOutOfRangeException(nameof(days));
        var reminders = await storage.GetRemindersAsync();
        return reminders.Where(r => r.Kind is "Maintenance" or "Inspection" or "Missing")
            .GroupBy(r => r.DueDate is null ? "缺日期" : ReminderGrouping.Group(r, today, days) is "期間外" && ReminderGrouping.MileageDue(r) ? "里程到期" : ReminderGrouping.Group(r, today, days))
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ReminderRow>)g.ToList());
    }
}

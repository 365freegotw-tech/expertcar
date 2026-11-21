using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Text.Json;
using ExpertCar.Core;

namespace ExpertCar.App.Presentation;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? Changed;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Notify(name); Changed?.Invoke(); return true;
    }
    protected void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed class AsyncCommand(Func<Task> action, Func<bool>? canExecute = null) : ICommand
{
    private bool running;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !running && (canExecute?.Invoke() ?? true);
    public async void Execute(object? parameter) => await ExecuteAsync();
    public async Task ExecuteAsync()
    {
        if (!CanExecute(null)) return;
        running = true; Refresh();
        try { await action(); } finally { running = false; Refresh(); }
    }
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public static class UiText
{
    public static string Money(long minor) => $"NT$ {minor / 100m:N2}";
    public static DateTimeOffset Now => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow,
        TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Taipei Standard Time" : "Asia/Taipei"));
    public static string Status(WorkOrderStatus status) => status switch
    {
        WorkOrderStatus.Estimate => "估價中", WorkOrderStatus.Accepted => "客人已同意",
        WorkOrderStatus.InProgress => "施工中", WorkOrderStatus.AwaitingCheckout => "待結帳／交車",
        WorkOrderStatus.Settled => "已付清", WorkOrderStatus.Cancelled or WorkOrderStatus.Void => "已取消",
        WorkOrderStatus.ImportReview => "匯入待確認", _ => "歷史狀態"
    };
    public static string Category(WorkLineCategory kind) => kind switch
    {
        WorkLineCategory.Labor => "工資", WorkLineCategory.Part => "零件", WorkLineCategory.Consumable => "油液耗材",
        WorkLineCategory.Disposal => "環保處理費", WorkLineCategory.Outsource => "外包", _ => "折扣"
    };
    public static string Error(Exception ex) => ex switch
    {
        NotSupportedException => "目前服務不支援此操作，請確認使用完整的正式版本。",
        ArgumentOutOfRangeException => "數量、金額或費率超出範圍，請核對輸入及折扣。",
        FormatException => "格式不正確，請輸入有效數字或西元日期（yyyy-MM-dd）。",
        OverflowException => "數字過大，請核對輸入。",
        IOException => "無法讀寫檔案，請確認路徑與存取權限。",
        _ when ex.Message.Any(c => c >= '\u4e00' && c <= '\u9fff') => ex.Message,
        _ => "操作未完成，請核對資料後重試。"
    };
    public static long Amount(string text) => Pricing.ToMinor(decimal.Parse(text, System.Globalization.CultureInfo.CurrentCulture));
}

// 商業欄位僅供讀取舊 JSON 遷移；新偏好只保留導覽與列印方向。
public sealed record AppPreferences(long HourlyRateMinor = 120000, string DocumentHeader = "", bool TourSeen = false,
    Dictionary<long, long[]>? CatalogParts = null, PrintOrientation PrintOrientation = PrintOrientation.Portrait,
    string ShopAddress = "", string ShopTaxId = "");
public interface IAppPreferencesStore { AppPreferences Load(); void Save(AppPreferences value); }
public sealed class JsonAppPreferencesStore(string path) : IAppPreferencesStore
{
    public AppPreferences Load() => File.Exists(path)
        ? JsonSerializer.Deserialize<AppPreferences>(File.ReadAllText(path)) ?? new() : new();
    public void Save(AppPreferences value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value));
        File.Move(temp, path, true);
    }
}

public sealed class LineEditor : Observable
{
    public static IReadOnlyList<WorkLineCategory> Categories { get; } = [WorkLineCategory.Labor, WorkLineCategory.Part,
        WorkLineCategory.Consumable, WorkLineCategory.Disposal, WorkLineCategory.Outsource];
    private WorkLineCategory category = WorkLineCategory.Part;
    public WorkLineCategory Category { get => category; set => Set(ref category, value); }
    private string name = "", partNumber = "", code = "", unit = "個", vendor = "";
    public string Name { get => name; set => Set(ref name, value); }
    public string PartNumber { get => partNumber; set => Set(ref partNumber, value); }
    public string Code { get => code; set => Set(ref code, value); }
    public string Unit { get => unit; set => Set(ref unit, value); }
    public string Vendor { get => vendor; set => Set(ref vendor, value); }
    private decimal quantity = 1, price, hourlyRate = 1200;
    public decimal Quantity { get => quantity; set => Set(ref quantity, value); }
    public decimal Price { get => price; set => Set(ref price, value); }
    public decimal HourlyRate { get => hourlyRate; set => Set(ref hourlyRate, value); }
    private bool hourly;
    public bool Hourly { get => hourly; set => Set(ref hourly, value); }
    private int minutes = 60;
    public int Minutes { get => minutes; set => Set(ref minutes, value); }
    private decimal lineDiscount;
    public decimal LineDiscount { get => lineDiscount; set => Set(ref lineDiscount, value); }
    private long? vendorId;
    public long? VendorId { get => vendorId; set => Set(ref vendorId, value); }
    private long? additionId;
    public long? AdditionId { get => additionId; set => Set(ref additionId, value); }
    public WorkLineDraft ToDraft()
    {
        if (string.IsNullOrWhiteSpace(Name)) throw new ArgumentException("請輸入維修項目或零件名稱");
        if (Category == WorkLineCategory.Outsource && string.IsNullOrWhiteSpace(Vendor)) throw new ArgumentException("外包請輸入廠商名稱");
        if (Quantity <= 0 || Price < 0 || Minutes < 0 || HourlyRate < 0 || LineDiscount < 0) throw new ArgumentException("數量須大於零，費用與工時不可為負");
        return new(Category, Name, Code, PartNumber, Unit, Pricing.ToScaledQuantity(Quantity), Pricing.ToMinor(Price), Pricing.ToMinor(LineDiscount),
            LaborMode: Category == WorkLineCategory.Labor ? Hourly ? LaborPriceMode.Hourly : LaborPriceMode.Fixed : null,
            LaborMinutes: Minutes, HourlyRateMinor: Pricing.ToMinor(HourlyRate), VendorId: VendorId, AdditionId: AdditionId,
            VendorName: Category == WorkLineCategory.Outsource ? Vendor.Trim() : null);
    }
    public static LineEditor From(WorkLineDraft line) => new()
    {
        Category = line.Category, Name = line.Name, Code = line.Code ?? "", PartNumber = line.PartNumber ?? "",
        Unit = line.Unit ?? "", Quantity = line.QuantityScaled / 1000m, Price = line.UnitPriceMinor / 100m,
        Hourly = line.LaborMode == LaborPriceMode.Hourly, Minutes = line.LaborMinutes ?? 0,
        HourlyRate = (line.HourlyRateMinor ?? 0) / 100m, AdditionId = line.AdditionId,
        LineDiscount = line.DiscountMinor / 100m, VendorId = line.VendorId,
        Vendor = line.VendorName ?? ""
    };
}
public sealed class AdditionEditor : Observable
{
    public long Id { get; init; }
    private string title = "追加維修", reason = "", method = "", customerName = "";
    private bool accepted;
    private DateTimeOffset? acceptedAt;
    public string Title { get => title; set => Set(ref title, value); }
    public string Reason { get => reason; set => Set(ref reason, value); }
    public string Method { get => method; set => Set(ref method, value); }
    public string CustomerName { get => customerName; set => Set(ref customerName, value); }
    public bool Accepted
    {
        get => accepted;
        set { if (Set(ref accepted, value)) { acceptedAt = value ? UiText.Now : null; Notify(nameof(ConsentTime)); } }
    }
    public string ConsentTime => acceptedAt?.ToString("yyyy/MM/dd HH:mm zzz") ?? "尚未同意，不併入結帳";
    public AdditionDraft ToDraft()
    {
        if (string.IsNullOrWhiteSpace(Title)) throw new ArgumentException("請輸入加修項目名稱");
        if (Accepted && (string.IsNullOrWhiteSpace(Method) || string.IsNullOrWhiteSpace(CustomerName)))
            throw new ArgumentException("同意加修須記錄客人姓名與同意方式");
        return new(Id, Title, Reason, Accepted, acceptedAt, Method, CustomerName);
    }
    public static AdditionEditor From(AdditionDraft row) => new() { Id = row.Id ?? 1, title = row.Title,
        reason = row.Reason ?? "", method = row.ConsentMethod ?? "", customerName = row.CustomerName ?? "",
        accepted = row.Accepted, acceptedAt = row.AcceptedAt };
}

public sealed record HistoryOrder(WorkOrderSummary Source, string Description, bool HasQuote = true)
{
    public long Id => Source.Id;
    public string Number => Source.Number;
    public DateOnly Date => Source.OrderDate;
    public string Status => UiText.Status(Source.Status);
    public string Total => HasQuote ? UiText.Money(Source.TotalMinor) : "待試算";
    public string Due => HasQuote ? UiText.Money(Source.DueMinor) : "待試算";
}
public sealed record ReminderView(ReminderRow Source)
{
    public string Plate => Source.Plate;
    public string Customer => Source.CustomerName;
    public string Kind => Source.Kind switch { "Maintenance" => "保養", "Inspection" => "驗車", "Insurance" => "保險（待確認）", _ => "待確認" };
    public string Due => $"{(Source.DueDate?.ToString("yyyy/MM/dd") ?? "日期待確認")} ／ {Source.DueKm?.ToString("N0") ?? "—"} km";
}

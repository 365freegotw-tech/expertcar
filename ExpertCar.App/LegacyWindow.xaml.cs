using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ExpertCar.Core;
using ExpertCar.Import;
using Microsoft.Win32;

namespace ExpertCar.App;

public partial class LegacyWindow : Window
{
    private const string MvdisUrl = "https://mvdvan.mvdis.gov.tw/mvdvan/mvdvan";
    private readonly IStorageService _storage;
    private readonly WorkspaceViewModel _viewModel;
    private readonly ObservableCollection<ReminderDisplay> _reminders = [];
    private long? _editingVehicleId;
    private long? _editingCatalogId;
    private int _days = 30;

    public LegacyWindow(IStorageService storage)
    {
        _storage = storage;
        InitializeComponent();
        _viewModel = new WorkspaceViewModel(_storage);
        RemindersGrid.ItemsSource = _reminders;
        Loaded += async (_, _) => await Guard(async () =>
        {
            await _storage.InitializeAsync();
            await LoadSettings();
            await RefreshAll();
        });
    }

    private async Task Guard(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "操作未完成", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async Task RefreshAll()
    {
        var summary = await _storage.GetDashboardAsync();
        StatusText.Text = $"客戶 {summary.CustomerCount} 位 · 車輛 {summary.VehicleCount} 台 · 未結工單 {summary.OpenOrderCount} 張";
        await RefreshReminders(); await Search(); await RefreshCatalog();
    }

    private async Task RefreshReminders()
    {
        if (!int.TryParse(DaysBox.Text, out _days) || _days is < 0 or > 3650) throw new ArgumentException("提醒天數請輸入 0 至 3650");
        var taipei = TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Taipei Standard Time" : "Asia/Taipei");
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, taipei).DateTime);
        var groups = await _viewModel.GetReminderGroupsAsync(today, _days);
        _reminders.Clear();
        foreach (var group in new[] { "逾期", "今日", "期間內", "里程到期", "缺日期" })
            if (groups.TryGetValue(group, out var rows))
                foreach (var row in rows)
                {
                    var kmDue = ReminderGrouping.MileageDue(row);
                    _reminders.Add(new ReminderDisplay(row, group,
                        row.DueDate is null ? "未設定日期" : $"{row.DueDate:yyyy/MM/dd} · {group}",
                        row.DueKm is > 0 ? $"{row.DueKm:N0} 公里 · {(kmDue ? "已到期" : row.OdometerKm is null ? "目前里程未知" : "未到期")}" : "未設定里程"));
                }
    }

    private async Task Search() => CustomersGrid.ItemsSource = await _viewModel.SearchAsync(SearchBox.Text);
    private async Task RefreshCatalog() => CatalogGrid.ItemsSource = await _storage.GetCatalogAsync();
    private async void RefreshReminders_Click(object sender, RoutedEventArgs e) => await Guard(RefreshReminders);
    private async void Search_Click(object sender, RoutedEventArgs e) => await Guard(Search);
    private async void SearchBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) await Guard(Search); }
    private void PrintPlaceholder_Click(object sender, RoutedEventArgs e)
    {
        var selected = _reminders.Where(r => r.Selected).Select(r => new ReminderPrintCandidate(
            r.Row.CustomerId, r.Row.VehicleId, r.Row.Kind, r.Row.Address, r.Row.DueDate, r.Row.DueKm)).ToArray();
        MessageBox.Show(this, $"已選取 {selected.Length} 筆。標籤與通知函列印將由 B06 提供。", "列印尚未開放");
    }

    private async void CustomersGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => await Guard(async () =>
    {
        _editingVehicleId = null; ClearVehicle();
        VehiclesGrid.ItemsSource = CustomersGrid.SelectedItem is CustomerRow customer
            ? await _storage.GetVehiclesAsync(customer.Id) : null;
    });

    private void NewCustomer_Click(object sender, RoutedEventArgs e) => ShowCustomerEditor(null);
    private void EditCustomer_Click(object sender, RoutedEventArgs e)
    {
        if (CustomersGrid.SelectedItem is CustomerRow row) ShowCustomerEditor(row);
        else MessageBox.Show(this, "請先選取客戶");
    }

    private void ShowCustomerEditor(CustomerRow? existing)
    {
        var dialog = new Window { Title = existing is null ? "新增客戶" : "編輯客戶", Owner = this, Width = 530, Height = 380,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, FontSize = 19, Background = System.Windows.Media.Brushes.White };
        var panel = new StackPanel { Margin = new Thickness(20) };
        var name = Field(panel, "姓名", existing?.Name); var phone = Field(panel, "電話", existing?.Phone);
        var address = Field(panel, "郵寄地址", existing?.Address);
        var save = new Button { Content = "儲存客戶", MinHeight = 42, Margin = new Thickness(0, 14, 0, 0) };
        save.Click += async (_, _) => await Guard(async () =>
        {
            var id = await _storage.SaveCustomerAsync(new CustomerEditor(existing?.Id, name.Text, phone.Text, address.Text));
            dialog.DialogResult = true;
            await Search();
            CustomersGrid.SelectedItem = ((IEnumerable<CustomerRow>)CustomersGrid.ItemsSource).FirstOrDefault(c => c.Id == id);
        });
        panel.Children.Add(save); dialog.Content = panel; name.Focus(); dialog.ShowDialog();
    }

    private static TextBox Field(Panel panel, string label, string? value)
    {
        panel.Children.Add(new TextBlock { Text = label });
        var box = new TextBox { Text = value ?? "", MinHeight = 38, Margin = new Thickness(0, 4, 0, 10) };
        panel.Children.Add(box); return box;
    }

    private void VehiclesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VehiclesGrid.SelectedItem is not VehicleRow row) return;
        _editingVehicleId = row.Id; PlateBox.Text = row.Plate; ModelBox.Text = row.Model ?? "";
        OdometerBox.Text = row.OdometerKm?.ToString(CultureInfo.InvariantCulture) ?? "";
        MaintenanceKmBox.Text = row.MaintenanceKm?.ToString(CultureInfo.InvariantCulture) ?? "";
        MaintenanceDatePicker.SelectedDate = row.MaintenanceDate?.ToDateTime(TimeOnly.MinValue);
        InspectionDatePicker.SelectedDate = row.InspectionDate?.ToDateTime(TimeOnly.MinValue);
    }
    private void NewVehicle_Click(object sender, RoutedEventArgs e) { _editingVehicleId = null; VehiclesGrid.SelectedItem = null; ClearVehicle(); PlateBox.Focus(); }
    private void ClearVehicle()
    {
        PlateBox.Text = ModelBox.Text = OdometerBox.Text = MaintenanceKmBox.Text = "";
        MaintenanceDatePicker.SelectedDate = InspectionDatePicker.SelectedDate = null;
    }
    private async void SaveVehicle_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (CustomersGrid.SelectedItem is not CustomerRow customer) throw new ArgumentException("請先選取車主");
        await _storage.SaveVehicleAsync(new VehicleEditor(_editingVehicleId, customer.Id, PlateBox.Text, ModelBox.Text,
            ParseKm(OdometerBox.Text), ToDate(MaintenanceDatePicker.SelectedDate), ParseKm(MaintenanceKmBox.Text), ToDate(InspectionDatePicker.SelectedDate)));
        VehiclesGrid.ItemsSource = await _storage.GetVehiclesAsync(customer.Id);
        await RefreshReminders();
        StatusText.Text = "車輛已儲存";
    });
    private static long? ParseKm(string text) => string.IsNullOrWhiteSpace(text) ? null :
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var km) && km >= 0 ? km : throw new ArgumentException("公里數須為非負整數");
    private static DateOnly? ToDate(DateTime? date) => date is null ? null : DateOnly.FromDateTime(date.Value);

    private void CatalogGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CatalogGrid.SelectedItem is not CatalogRow row) return;
        _editingCatalogId = row.Id; CatalogKindBox.SelectedIndex = row.Kind == "項目" ? 0 : 1;
        CatalogCodeBox.Text = row.Code; CatalogNameBox.Text = row.Name; CatalogUnitBox.Text = row.Unit;
        CatalogPriceBox.Text = (row.PriceMinor / 100m).ToString("0.##", CultureInfo.CurrentCulture);
    }
    private void NewCatalog_Click(object sender, RoutedEventArgs e)
    {
        _editingCatalogId = null; CatalogGrid.SelectedItem = null;
        CatalogCodeBox.Text = CatalogNameBox.Text = CatalogUnitBox.Text = CatalogPriceBox.Text = "";
        CatalogCodeBox.Focus();
    }
    private async void SaveCatalog_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (!decimal.TryParse(CatalogPriceBox.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var price) || price < 0 || decimal.Round(price, 2) != price)
            throw new ArgumentException("價格請輸入非負數，最多兩位小數");
        var kind = ((ComboBoxItem)CatalogKindBox.SelectedItem).Content.ToString()!;
        if (_editingCatalogId is not null && CatalogGrid.SelectedItem is CatalogRow selected && selected.Kind != kind)
            throw new ArgumentException("編輯時不可更改類別；請新增目錄項目");
        await _storage.SaveCatalogAsync(new CatalogEditor(_editingCatalogId,
            kind, CatalogCodeBox.Text, CatalogNameBox.Text,
            CatalogUnitBox.Text, checked((long)(price * 100m))));
        await RefreshCatalog(); StatusText.Text = "目錄已儲存";
    });

    private async Task LoadSettings()
    {
        var s = await _viewModel.LoadSettingsAsync();
        ShopNameBox.Text = s.Name; ShopContactBox.Text = s.Contact;
        ShopDaysBox.Text = DaysBox.Text = s.ReminderDays.ToString(CultureInfo.InvariantCulture);
        CardFeeBox.Text = s.CardFeePercent.ToString(CultureInfo.CurrentCulture);
        TaxBox.Text = s.TaxPercent.ToString(CultureInfo.CurrentCulture);
    }
    private async void SaveSettings_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (!int.TryParse(ShopDaysBox.Text, out var days) ||
            !decimal.TryParse(CardFeeBox.Text, out var fee) || !decimal.TryParse(TaxBox.Text, out var tax))
            throw new ArgumentException("提醒天數或費率格式不正確");
        await _viewModel.SaveSettingsAsync((await _storage.GetShopSettingsAsync()) with
        { Name = ShopNameBox.Text.Trim(), Contact = ShopContactBox.Text.Trim(), ReminderDays = days,
            CardFeePercent = fee, TaxPercent = tax });
        DaysBox.Text = days.ToString(CultureInfo.InvariantCulture); await RefreshReminders(); StatusText.Text = "設定已儲存";
    });
    private void OpenMvdis_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(MvdisUrl) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "無法開啟瀏覽器", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "選擇舊資料來源資料夾" };
        if (dialog.ShowDialog(this) == true) { SourceFolderBox.Text = dialog.FolderName; CommitImportButton.IsEnabled = BackupImportButton.IsEnabled = false; ImportSummaryText.Text = "尚未演練"; }
    }
    private async void PreviewImport_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (!Directory.Exists(SourceFolderBox.Text)) throw new ArgumentException("請先選擇來源資料夾");
        var folder = SourceFolderBox.Text;
        var paths = ImportEngine.DiscoverSourceFiles(folder);
        if (paths.Length == 0) throw new ArgumentException("找不到支援的 Base.mdb 或 CUSTNAME／CUSTPHON.DBF");
        if (paths.Any(p => p.EndsWith(".mdb", StringComparison.OrdinalIgnoreCase)) && !MdbToolsAvailable())
            throw new InvalidOperationException("匯入 MDB 需安裝讀取工具 MDBTools。可先選只有 DBF 的資料夾匯入。");
        ImportSummaryText.Text = "正在掃描與演練…";
        var preview = await _storage.PreviewImportAsync(paths);
        ImportSummaryText.Text = $"成功 {preview.Success}／跳過 {preview.Skipped}／待處理 {preview.Pending}／失敗 {preview.Failed}";
        ImportProblemsGrid.ItemsSource = preview.Problems;
        BackupImportButton.IsEnabled = !preview.AlreadyImported && preview.Failed == 0 && preview.Success > 0;
        CommitImportButton.IsEnabled = false;
        ImportResultText.Text = preview.AlreadyImported ? "此來源已匯入，無需重複匯入。" : "請核對問題清單，再確認匯入。";
    });
    private static bool MdbToolsAvailable()
    {
        if (!OperatingSystem.IsWindows()) return true;
        var names = new[] { "mdb-tables.exe", "mdb-schema.exe", "mdb-export.exe" };
        var locations = new[] { Environment.GetEnvironmentVariable("EXPERTCAR_MDBTOOLS_DIR"),
            Path.Combine(AppContext.BaseDirectory, "tools", "mdbtools") }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
        return locations.Any(dir => !string.IsNullOrWhiteSpace(dir) && names.All(name => File.Exists(Path.Combine(dir, name))));
    }
    private async void BackupImport_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var path = await _storage.CreateBackupAsync();
        ImportResultText.Text = $"已備份目前資料：{path}。請確認演練問題，再按確認匯入。";
        BackupImportButton.IsEnabled = false; CommitImportButton.IsEnabled = true;
    });
    private async void CommitImport_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (MessageBox.Show(this, "已核對來源與演練結果？已備份現有資料庫。", "確認匯入", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        CommitImportButton.IsEnabled = false;
        var result = await _storage.CommitImportAsync();
        ImportResultText.Text = $"匯入完成：成功 {result.Success}／跳過 {result.Skipped}／待處理 {result.Pending}／失敗 {result.Failed}。備份：{result.BackupPath ?? "首次建庫，無既有資料"}。批次：{result.RunId?.ToString() ?? "已匯入來源"}";
        await RefreshAll();
    });
    private async void VerifyImport_Click(object sender, RoutedEventArgs e) => await Guard(async () => { await RefreshAll(); MainTabs.SelectedIndex = 0; });

    private sealed class ReminderDisplay(ReminderRow row, string group, string dateStatus, string mileageStatus)
    {
        public ReminderRow Row => row;
        public bool Selected { get; set; }
        public long VehicleId => row.VehicleId;
        public long CustomerId => row.CustomerId;
        public string Customer => row.CustomerName;
        public string Plate => row.Plate;
        public string Address => row.Address ?? "地址未設定";
        public string Kind => row.Kind switch { "Maintenance" => "保養", "Inspection" => "驗車", _ => "未設定" };
        public string Group => group;
        public string DateStatus => dateStatus;
        public string MileageStatus => mileageStatus;
    }
}

using System.ComponentModel;
using System.Globalization;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using ExpertCar.App.Presentation;
using ExpertCar.App.Printing;
using ExpertCar.Core;
using Microsoft.Win32;

namespace ExpertCar.App;

public partial class MainWindow : Window
{
    private readonly WorkspaceV02ViewModel viewModel;
    private readonly IStorageService storage;
    private readonly HashSet<ValidationError> inputErrors = [];
    private readonly DispatcherTimer catalogDebounce = new() { Interval = TimeSpan.FromMilliseconds(300) };
    public MainWindow(WorkspaceV02ViewModel viewModel, IStorageService storage)
    {
        InitializeComponent(); this.storage = storage; this.viewModel = viewModel; DataContext = viewModel;
        // DocumentViewer 工具列與 Ctrl+P 也使用單據方向及 A4 紙張設定。
        PrintViewer.CommandBindings.Add(new CommandBinding(ApplicationCommands.Print,
            (_, args) => { Print(false); args.Handled = true; }));
        AddHandler(Validation.ErrorEvent, new EventHandler<ValidationErrorEventArgs>((_, args) =>
        {
            if (args.Action == ValidationErrorEventAction.Added) inputErrors.Add(args.Error); else inputErrors.Remove(args.Error);
            viewModel.HasInputErrors = inputErrors.Count > 0;
            if (viewModel.HasInputErrors) viewModel.Message = "紅框欄位格式不正確，請輸入有效數字後再操作。";
        }));
        Loaded += async (_, _) => { await viewModel.GuardAsync(viewModel.InitializeAsync); QuickSearchBox.Focus(); };
        catalogDebounce.Tick += async (_, _) =>
        {
            catalogDebounce.Stop();
            // 搜尋正在忙碌時，保留最新文字，下一個 tick 再套用。
            if (viewModel.Busy) { catalogDebounce.Start(); return; }
            await viewModel.SearchCatalogCommand.ExecuteAsync();
        };
        viewModel.PropertyChanged += ViewModel_PropertyChanged;
        Closed += (_, _) => { catalogDebounce.Stop(); viewModel.PropertyChanged -= ViewModel_PropertyChanged; };
    }
    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(viewModel.PrintModel))
            PrintViewer.Document = viewModel.PrintModel is { } model ? A4DocumentRenderer.Render(model) : null;
    }
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2) { QuickSearchBox.Focus(); QuickSearchBox.SelectAll(); e.Handled = true; }
    }
    private async void Search_KeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Enter) { e.Handled = true; viewModel.SelectedTab = 0; await viewModel.SearchCommand.ExecuteAsync(); } }
    private async void Vehicle_DoubleClick(object sender, MouseButtonEventArgs e) => await viewModel.OpenVehicleCommand.ExecuteAsync();
    private async void Order_DoubleClick(object sender, MouseButtonEventArgs e) => await viewModel.OpenOrderCommand.ExecuteAsync();
    private void GoCatalog_Click(object sender, RoutedEventArgs e) => viewModel.SelectedTab = 5;
    private void Tour_Click(object sender, RoutedEventArgs e) => viewModel.ShowTour = true;
    private async void Legacy_Click(object sender, RoutedEventArgs e)
    {
        new LegacyWindow(storage) { Owner = this }.ShowDialog();
        viewModel.ImportFolder = "";
        await viewModel.GuardAsync(viewModel.InitializeAsync);
        viewModel.Message = "客戶、車輛、目錄與店家設定已重新讀取。";
    }
    private async Task Preview(string kind)
    { viewModel.PrintKind = kind; await viewModel.PrintCommand.ExecuteAsync(); }
    private async void Estimate_Click(object sender, RoutedEventArgs e) => await Preview("Estimate");
    private async void WorkOrderPrint_Click(object sender, RoutedEventArgs e) => await Preview("WorkOrder");
    private async void Receipt_Click(object sender, RoutedEventArgs e) => await Preview("Receipt");
    private void CatalogQuery_Changed(object sender, TextChangedEventArgs e)
    { if (DataContext is null) return; catalogDebounce.Stop(); catalogDebounce.Start(); }
    private void CatalogKind_Changed(object sender, SelectionChangedEventArgs e)
    { if (DataContext is null) return; catalogDebounce.Stop(); catalogDebounce.Start(); }
    private async void Catalog_KeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Enter) { e.Handled = true; catalogDebounce.Stop(); await viewModel.SearchCatalogCommand.ExecuteAsync(); } }
    private async void Catalog_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not null) await viewModel.GuardAsync(viewModel.SuggestPartsAsync);
    }
    private async void AddSuggestedPart_Click(object sender, RoutedEventArgs e)
    {
        if (SuggestedPartsGrid.SelectedItem is not CatalogRow row) { viewModel.Message = "請先選取搭配零件"; return; }
        await viewModel.GuardAsync(() => { viewModel.AddCatalogRow(row); viewModel.SelectedTab = 2; return Task.CompletedTask; });
    }
    private async void SaveSuggestedParts_Click(object sender, RoutedEventArgs e)
    {
        var ids = SuggestedPartsGrid.SelectedItems.Cast<CatalogRow>().Select(p => p.Id).ToArray();
        await viewModel.GuardAsync(() => viewModel.SaveSuggestedPartsAsync(ids));
    }
    private void ChooseImport_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "選擇店家授權的 MDB／DBF 來源資料夾" };
        if (dialog.ShowDialog(this) == true) viewModel.ImportFolder = dialog.FolderName;
    }
    private void VerifyImport_Click(object sender, RoutedEventArgs e) { viewModel.SelectedTab = 0; QuickSearchBox.Focus(); }
    private void Print_Click(object sender, RoutedEventArgs e) => Print(false);
    private void Pdf_Click(object sender, RoutedEventArgs e) => Print(true);
    private void Print(bool pdf)
    {
        try
        {
            if (viewModel.PrintModel is null || PrintViewer.Document is null) { viewModel.Message = "請先更新列印預覽"; return; }
            var dialog = new PrintDialog();
            if (pdf)
            {
                using var server = new LocalPrintServer();
                var queue = server.GetPrintQueues().FirstOrDefault(q => q.Name == "Microsoft Print to PDF");
                if (queue is null) { viewModel.Message = "找不到 Microsoft Print to PDF，請先在 Windows 啟用此印表機。"; return; }
                dialog.PrintQueue = queue;
            }
            dialog.PrintTicket.PageOrientation = viewModel.PrintModel.Layout.Orientation == PrintOrientation.Landscape
                ? PageOrientation.Landscape : PageOrientation.Portrait;
            dialog.PrintTicket.PageMediaSize = new PageMediaSize(PageMediaSizeName.ISOA4);
            if (dialog.ShowDialog() == true)
                dialog.PrintDocument(PrintViewer.Document.DocumentPaginator, $"{viewModel.PrintModel.Title}-{viewModel.PrintModel.Number}");
        }
        catch (Exception) { viewModel.Message = "列印未完成，請確認印表機或 PDF 儲存位置後重試。"; }
    }
}

public sealed class MoneyConverter : IValueConverter
{
    public static MoneyConverter Instance { get; } = new();
    public object Convert(object value, Type type, object parameter, CultureInfo culture) => value is long amount ? UiText.Money(amount) : "—";
    public object ConvertBack(object value, Type type, object parameter, CultureInfo culture) => Binding.DoNothing;
}
public sealed class CategoryConverter : IValueConverter
{
    public object Convert(object value, Type type, object parameter, CultureInfo culture) => value is WorkLineCategory category ? UiText.Category(category) : "";
    public object ConvertBack(object value, Type type, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class TaipeiDateTimeConverter : IValueConverter
{
    public static TaipeiDateTimeConverter Instance { get; } = new();
    public object Convert(object value, Type type, object parameter, CultureInfo culture) => value is DateTimeOffset time
        ? time.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy/MM/dd HH:mm") : "—";
    public object ConvertBack(object value, Type type, object parameter, CultureInfo culture) => Binding.DoNothing;
}

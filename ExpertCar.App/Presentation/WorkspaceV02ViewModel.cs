using System.Collections.ObjectModel;
using ExpertCar.Core;

namespace ExpertCar.App.Presentation;

public sealed class WorkspaceV02ViewModel : Observable
{
    private readonly IExpertCarV02Service service;
    private readonly IStorageService storage;
    private readonly IAppPreferencesStore preferences;
    private readonly List<AsyncCommand> commands = [];
    private bool dirty, loading;
    private long? draftVehicleId, draftCustomerId;
    private string draftPlate = "", draftCustomerName = "", draftVehicleInfo = "";
    private int catalogPage = 1, catalogTotal;
    private int partsPage = 1, partsTotal;
    private int orderPage = 1, orderTotal;
    private long nextAdditionId = 1;
    private VehicleQuickResult? displayedVehicle;
    private long catalogVersion;
    public WorkspaceV02ViewModel(IExpertCarV02Service service, IStorageService storage, IAppPreferencesStore preferences)
    {
        this.service = service; this.storage = storage; this.preferences = preferences;
        SearchCommand = Command(SearchAsync);
        OpenVehicleCommand = Command(OpenVehicleAsync, () => (SelectedVehicle ?? displayedVehicle) is not null);
        OpenOtherVehicleCommand = Command(() => OpenVehicleCoreAsync(SelectedOtherVehicle!), () => SelectedOtherVehicle is not null);
        HistoryNewOrderCommand = Command(() => NewOrderCoreAsync(displayedVehicle!), () => displayedVehicle is not null);
        NewOrderCommand = Command(NewOrderAsync, () => (SelectedVehicle ?? displayedVehicle) is not null);
        OpenOrderCommand = Command(OpenOrderAsync, () => SelectedHistoryOrder is not null);
        SaveCommand = Command(SaveAsync, () => Editable);
        SaveQuoteCommand = Command(async () => { await QuoteAsync(); SelectedTab = 2; }, () => draftVehicleId is not null);
        QuoteCommand = Command(QuoteAsync, () => draftVehicleId is not null);
        NextStatusCommand = Command(AdvanceAsync, () => CurrentOrder is not null && NextStatus is not null);
        CancelOrderCommand = Command(CancelAsync, () => CurrentOrder is not null && WorkOrderTransitions.CanMove(CurrentOrder.Status, WorkOrderStatus.Cancelled));
        DeleteDraftCommand = Command(DeleteDraftAsync, () => CurrentOrder?.Status == WorkOrderStatus.Estimate && CurrentOrder.Payments.Count == 0);
        AddLineCommand = Command(() => { var line = new LineEditor { HourlyRate = HourlyRate }; Lines.Add(line); SelectedLine = line; return Task.CompletedTask; }, () => Editable);
        RemoveLineCommand = Command(() => { if (SelectedLine is not null) Lines.Remove(SelectedLine); return Task.CompletedTask; }, () => Editable && SelectedLine is not null);
        AddAdditionCommand = Command(() => { var row = new AdditionEditor { Id = nextAdditionId++, CustomerName = draftCustomerName }; Additions.Add(row); SelectedAddition = row; return Task.CompletedTask; }, () => Editable);
        AssignAdditionCommand = Command(() => { if (SelectedLine is not null && SelectedAddition is not null) SelectedLine.AdditionId = SelectedAddition.Id; return Task.CompletedTask; }, () => Editable && SelectedLine is not null && SelectedAddition is not null);
        ClearAdditionCommand = Command(() => { if (SelectedLine is not null) SelectedLine.AdditionId = null; return Task.CompletedTask; }, () => Editable && SelectedLine is not null);
        SearchCatalogCommand = Command(() => SearchCatalogAsync(true));
        PreviousCatalogCommand = Command(async () => { catalogPage--; await SearchCatalogAsync(false); }, () => catalogPage > 1);
        NextCatalogCommand = Command(async () => { catalogPage++; await SearchCatalogAsync(false); }, () => catalogPage * 30 < catalogTotal);
        SearchPartsCommand = Command(() => SearchPartsAsync(true));
        PreviousPartsCommand = Command(async () => { partsPage--; await SearchPartsAsync(false); }, () => partsPage > 1);
        NextPartsCommand = Command(async () => { partsPage++; await SearchPartsAsync(false); }, () => partsPage * 30 < partsTotal);
        AddCatalogCommand = Command(AddCatalogAsync, () => Editable && SelectedCatalogItem is not null);
        AddCatalogWithPartsCommand = Command(AddCatalogWithPartsAsync, () => Editable && SelectedCatalogItem is not null);
        PayCommand = Command(PayAsync, () => CurrentOrder?.Status == WorkOrderStatus.AwaitingCheckout && SelectedQuote is not null && !dirty);
        PrintCommand = Command(PreparePrintAsync, () => draftVehicleId is not null);
        SaveSettingsCommand = Command(SaveSettingsAsync);
        DrillImportCommand = Command(DrillImportAsync, () => !string.IsNullOrWhiteSpace(ImportFolder));
        CommitImportCommand = Command(CommitImportAsync, () => importReady && ImportConfirmed);
        RefreshCommand = Command(RefreshHomeAsync);
        PreviousOrdersCommand = Command(async () => { orderPage--; await RefreshHomeAsync(); }, () => orderPage > 1);
        NextOrdersCommand = Command(async () => { orderPage++; await RefreshHomeAsync(); }, () => orderPage * 100 < orderTotal);
        SaveVehicleCommand = Command(SaveVehicleAsync, () => displayedVehicle is not null);
        TransferVehicleCommand = Command(TransferVehicleAsync, () => displayedVehicle is not null);
        SearchTransferCustomersCommand = Command(async () =>
        {
            Replace(TransferCustomers, (await storage.SearchCustomersAsync(TransferQuery.Trim())).Take(50));
            SelectedTransferCustomer = TransferCustomers.Count == 1 ? TransferCustomers[0] : null;
        });
        DismissTourCommand = Command(() => { preferences.Save(CapturePreferences() with { TourSeen = true }); ShowTour = false; return Task.CompletedTask; });
        Lines.CollectionChanged += (_, e) => { if (e.NewItems is not null) foreach (LineEditor line in e.NewItems) line.Changed += MarkDirty; MarkDirty(); };
        Additions.CollectionChanged += (_, e) => { if (e.NewItems is not null) foreach (AdditionEditor addition in e.NewItems) addition.Changed += MarkDirty; MarkDirty(); };
        Changed += () => { foreach (var c in commands) c.Refresh(); };
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(Number) or nameof(Diagnosis) or nameof(RepairDescription) or nameof(ExpiresOn) or nameof(Discount) or nameof(SignatureLabel) or nameof(AdditionalNotice) or nameof(PromisedDeliveryOn)) MarkDirty();
            if (e.PropertyName is nameof(IssueInvoice) or nameof(PaymentMethod)) { SelectedQuote = null; DueText = "付款條件已改，請重新試算"; }
            if (e.PropertyName == nameof(ImportFolder)) { importReady = false; ImportConfirmed = false; ImportSources.Clear(); ImportProblems.Clear(); ImportSummary = "來源已變更，請重新演練"; }
            if (e.PropertyName == nameof(CurrentOrder)) { Notify(nameof(StatusText)); Notify(nameof(Editable)); Notify(nameof(NextActionText)); }
            if (e.PropertyName is nameof(PrintOrientation) or nameof(ShopAddress) or nameof(ShopTaxId) or nameof(DocumentHeader) or nameof(ShopName) or nameof(ShopContact) or nameof(PrintKind)) PrintModel = null;
            if (e.PropertyName == nameof(SelectedTab) && SelectedTab == 0 && SelectedVehicle is null && displayedVehicle is not null)
                SelectedVehicle = Vehicles.FirstOrDefault(v => v.VehicleId == displayedVehicle.VehicleId);
        };
    }
    private AsyncCommand Command(Func<Task> action, Func<bool>? predicate = null)
    {
        var command = new AsyncCommand(() => GuardAsync(action), () => !Busy && !HasInputErrors && (predicate?.Invoke() ?? true));
        commands.Add(command); return command;
    }
    public async Task GuardAsync(Func<Task> action)
    {
        if (Busy) return;
        Busy = true;
        try { Message = ""; await action(); }
        catch (Exception ex) { Message = UiText.Error(ex); }
        finally { Busy = false; }
    }
    private void MarkDirty()
    {
        if (loading) return;
        dirty = true; SelectedQuote = CashQuote = InvoiceQuote = CardQuote = null;
        PrintModel = null; DueText = "內容已變更，請儲存並重新試算";
        foreach (var c in commands) c.Refresh();
    }
    public ObservableCollection<VehicleQuickResult> Vehicles { get; } = [];
    public ObservableCollection<VehicleQuickResult> OtherVehicles { get; } = [];
    public ObservableCollection<CustomerRow> TransferCustomers { get; } = [];
    public ObservableCollection<OwnershipPeriod> Ownerships { get; } = [];
    public ObservableCollection<HistoryOrder> HistoryOrders { get; } = [];
    public ObservableCollection<HistoryOrder> DeliveryOrders { get; } = [];
    public ObservableCollection<HistoryOrder> UnpaidOrders { get; } = [];
    public ObservableCollection<HistoryOrder> Orders { get; } = [];
    public string OrderPageStatus => $"全站工單 {orderTotal} 筆 · 第 {orderPage} 頁";
    public ObservableCollection<ReminderView> Reminders { get; } = [];
    public ObservableCollection<ReminderView> VehicleReminders { get; } = [];
    public ObservableCollection<LineEditor> Lines { get; } = [];
    public ObservableCollection<AdditionEditor> Additions { get; } = [];
    public ObservableCollection<CatalogRow> Catalog { get; } = [];
    public ObservableCollection<CatalogRow> SuggestedParts { get; } = [];
    public ObservableCollection<CatalogRow> RecommendedParts { get; } = [];
    public ObservableCollection<PaymentEntry> Payments { get; } = [];
    public ObservableCollection<ImportSourceCount> ImportSources { get; } = [];
    public ObservableCollection<ImportProblem> ImportProblems { get; } = [];
    public IReadOnlyList<string> PaymentMethods { get; } = ["現金", "刷卡", "轉帳"];
    public IReadOnlyList<string> CatalogKinds { get; } = ["全部", "項目", "零件"];
    public bool Editable => draftVehicleId is not null && (CurrentOrder is null || (CurrentOrder.Payments.Count == 0 && CurrentOrder.Status is not (WorkOrderStatus.Settled or WorkOrderStatus.Cancelled or WorkOrderStatus.Void)));
    public string DraftVehicle => $"{draftPlate} ／ {draftCustomerName}";
    public string DraftVehicleInfo => draftVehicleInfo;
    private bool hasInputErrors;
    public bool HasInputErrors { get => hasInputErrors; set => Set(ref hasInputErrors, value); }
    private static string VehicleDetails(VehicleQuickResult v) => $"{v.Make ?? "廠牌待確認"} ／ {v.Model ?? "車型待確認"} ／ {v.ModelYear?.ToString() ?? v.RawYear ?? "年份待確認"}\n車色：{v.Color ?? "待確認"}\nVIN：{v.Vin ?? "待確認（17 碼）"}";
    public string StatusText => CurrentOrder is null ? "尚未儲存" : $"{UiText.Status(CurrentOrder.Status)} · 修訂 {CurrentOrder.Revision}";
    public WorkOrderStatus? NextStatus => CurrentOrder?.Status switch
    {
        WorkOrderStatus.Estimate => WorkOrderStatus.Accepted, WorkOrderStatus.Accepted => WorkOrderStatus.InProgress,
        WorkOrderStatus.InProgress => WorkOrderStatus.AwaitingCheckout, WorkOrderStatus.AwaitingCheckout => WorkOrderStatus.Settled,
        WorkOrderStatus.ImportReview => WorkOrderStatus.Estimate, _ => null
    };
    public string NextActionText => NextStatus switch { WorkOrderStatus.Accepted => "客人同意，轉工單", WorkOrderStatus.InProgress => "開始施工",
        WorkOrderStatus.AwaitingCheckout => "完工，待結帳", WorkOrderStatus.Settled => "已付清，完成交車", WorkOrderStatus.Estimate => "確認來源，轉估價", _ => "流程已完成" };
    public string CashText => CashQuote is null ? "請先試算" : UiText.Money(CashQuote.TotalMinor);
    public string InvoiceText => InvoiceQuote is null ? "請先試算" : UiText.Money(InvoiceQuote.TotalMinor);
    public string CardText => CardQuote is null ? "請先試算" : UiText.Money(CardQuote.TotalMinor);
    public string TotalText => SelectedQuote is null ? "請先試算" : UiText.Money(SelectedQuote.TotalMinor);
    public bool ShowCardSurcharge => CardSurchargeEnabled && SelectedQuote?.CardSurchargeApplied == true;
    public bool ShowCardPreviewSurcharge => CardSurchargeEnabled && CardQuote?.CardSurchargeApplied == true;
    private string query = "";
    public string Query { get => query; set => Set(ref query, value); }
    private string catalogQuery = "";
    public string CatalogQuery { get => catalogQuery; set => Set(ref catalogQuery, value); }
    private string partsQuery = "";
    public string PartsQuery { get => partsQuery; set => Set(ref partsQuery, value); }
    private string partsStatus = "";
    public string PartsStatus { get => partsStatus; set => Set(ref partsStatus, value); }
    private string catalogKind = "全部";
    public string CatalogKind { get => catalogKind; set => Set(ref catalogKind, value); }
    private int selectedTab = 0;
    public int SelectedTab { get => selectedTab; set => Set(ref selectedTab, value); }
    private VehicleQuickResult? selectedOtherVehicle;
    public VehicleQuickResult? SelectedOtherVehicle { get => selectedOtherVehicle; set => Set(ref selectedOtherVehicle, value); }
    private VehicleQuickResult? selectedVehicle = null;
    public VehicleQuickResult? SelectedVehicle { get => selectedVehicle; set => Set(ref selectedVehicle, value); }
    private HistoryOrder? selectedHistoryOrder = null;
    public HistoryOrder? SelectedHistoryOrder { get => selectedHistoryOrder; set => Set(ref selectedHistoryOrder, value); }
    private CatalogRow? selectedCatalogItem = null;
    public CatalogRow? SelectedCatalogItem { get => selectedCatalogItem; set => Set(ref selectedCatalogItem, value); }
    private LineEditor? selectedLine = null;
    public LineEditor? SelectedLine { get => selectedLine; set => Set(ref selectedLine, value); }
    private AdditionEditor? selectedAddition = null;
    public AdditionEditor? SelectedAddition { get => selectedAddition; set => Set(ref selectedAddition, value); }
    private string number = "";
    public string Number { get => number; set => Set(ref number, value); }
    private string diagnosis = "";
    public string Diagnosis { get => diagnosis; set => Set(ref diagnosis, value); }
    private string repairDescription = "";
    public string RepairDescription { get => repairDescription; set => Set(ref repairDescription, value); }
    private string expiresOn = "";
    public string ExpiresOn { get => expiresOn; set => Set(ref expiresOn, value); }
    private string promisedDeliveryOn = "";
    public string PromisedDeliveryOn { get => promisedDeliveryOn; set => Set(ref promisedDeliveryOn, value); }
    private string vehicleVin = "", vehicleMake = "", vehicleModel = "", vehicleYear = "", vehicleColor = "";
    public string VehicleVin { get => vehicleVin; set => Set(ref vehicleVin, value); }
    public string VehicleMake { get => vehicleMake; set => Set(ref vehicleMake, value); }
    public string VehicleModel { get => vehicleModel; set => Set(ref vehicleModel, value); }
    public string VehicleYear { get => vehicleYear; set => Set(ref vehicleYear, value); }
    public string VehicleColor { get => vehicleColor; set => Set(ref vehicleColor, value); }
    private string transferQuery = "";
    public string TransferQuery { get => transferQuery; set => Set(ref transferQuery, value); }
    private CustomerRow? selectedTransferCustomer;
    public CustomerRow? SelectedTransferCustomer
    {
        get => selectedTransferCustomer;
        set { if (Set(ref selectedTransferCustomer, value)) TransferCustomerId = value?.Id.ToString() ?? ""; }
    }
    private string transferCustomerId = "", transferDate = "";
    public string TransferCustomerId { get => transferCustomerId; set => Set(ref transferCustomerId, value); }
    public string TransferDate { get => transferDate; set => Set(ref transferDate, value); }
    private string discount = "0";
    public string Discount { get => discount; set => Set(ref discount, value); }
    private string signatureLabel = "客人授權簽名";
    public string SignatureLabel { get => signatureLabel; set => Set(ref signatureLabel, value); }
    private string additionalNotice = "施工中可能發現額外項目，未經同意不施工";
    public string AdditionalNotice { get => additionalNotice; set => Set(ref additionalNotice, value); }
    private bool issueInvoice = false;
    public bool IssueInvoice { get => issueInvoice; set => Set(ref issueInvoice, value); }
    private string paymentMethod = "現金";
    public string PaymentMethod { get => paymentMethod; set => Set(ref paymentMethod, value); }
    private string paymentAmount = "";
    public string PaymentAmount { get => paymentAmount; set => Set(ref paymentAmount, value); }
    private string shopName = "";
    public string ShopName { get => shopName; set => Set(ref shopName, value); }
    private string shopContact = "";
    public string ShopContact { get => shopContact; set => Set(ref shopContact, value); }
    private int reminderDays = 30;
    public int ReminderDays { get => reminderDays; set => Set(ref reminderDays, value); }
    private decimal taxPercent = 5;
    public decimal TaxPercent { get => taxPercent; set => Set(ref taxPercent, value); }
    private bool cardSurchargeEnabled = false;
    public bool CardSurchargeEnabled { get => cardSurchargeEnabled; set { if (Set(ref cardSurchargeEnabled, value)) { Notify(nameof(CashText)); Notify(nameof(InvoiceText)); Notify(nameof(CardText)); Notify(nameof(TotalText)); Notify(nameof(ShowCardSurcharge)); Notify(nameof(ShowCardPreviewSurcharge)); } } }
    private decimal cardPercent = 0;
    public decimal CardPercent { get => cardPercent; set => Set(ref cardPercent, value); }
    private decimal hourlyRate = 1200;
    public decimal HourlyRate { get => hourlyRate; set => Set(ref hourlyRate, value); }
    private string documentHeader = "";
    public string DocumentHeader { get => documentHeader; set => Set(ref documentHeader, value); }
    private PrintOrientation printOrientation = PrintOrientation.Portrait;
    public PrintOrientation PrintOrientation { get => printOrientation; set => Set(ref printOrientation, value); }
    private string shopAddress = "", shopTaxId = "";
    public string ShopAddress { get => shopAddress; set => Set(ref shopAddress, value); }
    public string ShopTaxId { get => shopTaxId; set => Set(ref shopTaxId, value); }
    private string importFolder = "";
    public string ImportFolder { get => importFolder; set => Set(ref importFolder, value); }
    private string importSummary = "尚未演練";
    public string ImportSummary { get => importSummary; set => Set(ref importSummary, value); }
    private string importNextStep = "選擇來源資料夾後執行演練。";
    public string ImportNextStep { get => importNextStep; set => Set(ref importNextStep, value); }
    private string message = "";
    public string Message { get => message; set => Set(ref message, value); }
    private bool busy = false;
    public bool Busy { get => busy; set => Set(ref busy, value); }
    private bool showTour = false;
    public bool ShowTour { get => showTour; set => Set(ref showTour, value); }
    private string vehicleInfo = "請先搜尋並選取車輛";
    public string VehicleInfo { get => vehicleInfo; set => Set(ref vehicleInfo, value); }
    private string odometer = "里程待確認";
    public string Odometer { get => odometer; set => Set(ref odometer, value); }
    private string ownerContact = "";
    public string OwnerContact { get => ownerContact; set => Set(ref ownerContact, value); }
    private string dashboardText = "";
    public string DashboardText { get => dashboardText; set => Set(ref dashboardText, value); }
    private string catalogStatus = "";
    public string CatalogStatus { get => catalogStatus; set => Set(ref catalogStatus, value); }
    private WorkOrderDetail? currentOrder = null;
    public WorkOrderDetail? CurrentOrder { get => currentOrder; set => Set(ref currentOrder, value); }
    private CheckoutQuote? cashQuote = null;
    public CheckoutQuote? CashQuote { get => cashQuote; set { if (Set(ref cashQuote, value)) { Notify(nameof(CashText)); Notify(nameof(InvoiceText)); Notify(nameof(CardText)); Notify(nameof(TotalText)); Notify(nameof(ShowCardSurcharge)); Notify(nameof(ShowCardPreviewSurcharge)); } } }
    private CheckoutQuote? invoiceQuote = null;
    public CheckoutQuote? InvoiceQuote { get => invoiceQuote; set { if (Set(ref invoiceQuote, value)) { Notify(nameof(CashText)); Notify(nameof(InvoiceText)); Notify(nameof(CardText)); Notify(nameof(TotalText)); Notify(nameof(ShowCardSurcharge)); Notify(nameof(ShowCardPreviewSurcharge)); } } }
    private CheckoutQuote? cardQuote = null;
    public CheckoutQuote? CardQuote { get => cardQuote; set { if (Set(ref cardQuote, value)) { Notify(nameof(CashText)); Notify(nameof(InvoiceText)); Notify(nameof(CardText)); Notify(nameof(TotalText)); Notify(nameof(ShowCardSurcharge)); Notify(nameof(ShowCardPreviewSurcharge)); } } }
    private CheckoutQuote? selectedQuote = null;
    public CheckoutQuote? SelectedQuote { get => selectedQuote; set { if (Set(ref selectedQuote, value)) { Notify(nameof(CashText)); Notify(nameof(InvoiceText)); Notify(nameof(CardText)); Notify(nameof(TotalText)); Notify(nameof(ShowCardSurcharge)); Notify(nameof(ShowCardPreviewSurcharge)); } } }
    private string dueText = "尚未試算";
    public string DueText { get => dueText; set => Set(ref dueText, value); }
    private string paidText = "尚無收款";
    public string PaidText { get => paidText; set => Set(ref paidText, value); }
    private PrintDocumentModel? printModel = null;
    public PrintDocumentModel? PrintModel { get => printModel; set => Set(ref printModel, value); }
    private string printKind = "Estimate";
    public string PrintKind { get => printKind; set => Set(ref printKind, value); }
    public AsyncCommand SearchCommand { get; }
    public AsyncCommand OpenVehicleCommand { get; }
    public AsyncCommand OpenOtherVehicleCommand { get; }
    public AsyncCommand HistoryNewOrderCommand { get; }
    public AsyncCommand NewOrderCommand { get; }
    public AsyncCommand OpenOrderCommand { get; }
    public AsyncCommand SaveCommand { get; }
    public AsyncCommand QuoteCommand { get; }
    public AsyncCommand SaveQuoteCommand { get; }
    public AsyncCommand NextStatusCommand { get; }
    public AsyncCommand CancelOrderCommand { get; }
    public AsyncCommand DeleteDraftCommand { get; }
    public AsyncCommand AddLineCommand { get; }
    public AsyncCommand RemoveLineCommand { get; }
    public AsyncCommand AddAdditionCommand { get; }
    public AsyncCommand AssignAdditionCommand { get; }
    public AsyncCommand ClearAdditionCommand { get; }
    public AsyncCommand SearchCatalogCommand { get; }
    public AsyncCommand SearchPartsCommand { get; }
    public AsyncCommand PreviousPartsCommand { get; }
    public AsyncCommand NextPartsCommand { get; }
    public AsyncCommand PreviousCatalogCommand { get; }
    public AsyncCommand NextCatalogCommand { get; }
    public AsyncCommand AddCatalogCommand { get; }
    public AsyncCommand AddCatalogWithPartsCommand { get; }
    public AsyncCommand PayCommand { get; }
    public AsyncCommand PrintCommand { get; }
    public AsyncCommand SaveSettingsCommand { get; }
    public AsyncCommand DrillImportCommand { get; }
    public AsyncCommand CommitImportCommand { get; }
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand PreviousOrdersCommand { get; }
    public AsyncCommand NextOrdersCommand { get; }
    public AsyncCommand SaveVehicleCommand { get; }
    public AsyncCommand TransferVehicleCommand { get; }
    public AsyncCommand SearchTransferCustomersCommand { get; }
    public AsyncCommand DismissTourCommand { get; }
    private bool importReady;
    private bool importConfirmed;
    public bool ImportConfirmed { get => importConfirmed; set => Set(ref importConfirmed, value); }
    public async Task InitializeAsync()
    {
        var settings = await storage.GetShopSettingsAsync();
        ShopName = settings.Name; ShopContact = settings.Contact; ReminderDays = settings.ReminderDays;
        TaxPercent = settings.TaxPercent; CardPercent = settings.CardFeePercent; CardSurchargeEnabled = settings.CardSurchargeEnabled;
        var pref = preferences.Load(); ShowTour = !pref.TourSeen;
        PrintOrientation = Enum.IsDefined(pref.PrintOrientation) ? pref.PrintOrientation : PrintOrientation.Portrait;
        if (pref.HourlyRateMinor != 120000 || pref.DocumentHeader.Length > 0 || pref.ShopAddress.Length > 0 || pref.ShopTaxId.Length > 0)
        {
            settings = settings with
            {
                HourlyRateMinor = settings.HourlyRateMinor == ShopSettings.Default.HourlyRateMinor ? pref.HourlyRateMinor : settings.HourlyRateMinor,
                DocumentHeader = settings.DocumentHeader.Length == 0 ? pref.DocumentHeader : settings.DocumentHeader,
                Address = settings.Address.Length == 0 ? pref.ShopAddress : settings.Address,
                TaxId = settings.TaxId.Length == 0 ? pref.ShopTaxId : settings.TaxId
            };
            await storage.SaveShopSettingsAsync(settings);
        }
        HourlyRate = settings.HourlyRateMinor / 100m; DocumentHeader = settings.DocumentHeader;
        ShopAddress = settings.Address; ShopTaxId = settings.TaxId;
        if (pref.CatalogParts is not null)
            foreach (var (id, parts) in pref.CatalogParts)
                try { if ((await service.GetSuggestedPartIdsAsync(id)).Count == 0) await service.SaveSuggestedPartIdsAsync(id, parts); }
                catch (ArgumentException) { /* 舊偏好可能引用已刪除的目錄項目；不阻擋啟動。 */ }
        if (pref.CatalogParts is not null || pref.DocumentHeader.Length > 0 || pref.ShopAddress.Length > 0 || pref.ShopTaxId.Length > 0 || pref.HourlyRateMinor != 120000)
            preferences.Save(new(TourSeen: pref.TourSeen, PrintOrientation: PrintOrientation));
        await RefreshHomeAsync(); await SearchAsync(); await SearchCatalogAsync(true);
    }
    public async Task SearchAsync()
    {
        var rows = (await service.QuickFindVehiclesAsync(Query.Trim())).ToList();
        // 支援舊資料聯絡欄位的快查。
        if (rows.Count == 0 && !string.IsNullOrWhiteSpace(Query))
            foreach (var customer in await storage.SearchCustomersAsync(Query.Trim()))
                foreach (var vehicle in await storage.GetVehiclesAsync(customer.Id))
                {
                    if (rows.Count >= 20) break;
                    var history = await service.GetVehicleHistoryAsync(vehicle.Id);
                    if (history is not null && rows.All(v => v.VehicleId != vehicle.Id)) rows.Add(history.Vehicle);
                }
        Replace(Vehicles, rows);
        SelectedVehicle = Vehicles.Count == 1 ? Vehicles[0] : null;
        Message = Vehicles.Count == 0 ? "找不到車輛；可到客戶維護新增後重新搜尋。" : $"找到 {Vehicles.Count} 台車（快查最多 20 筆）";
    }
    public Task OpenVehicleAsync() => OpenVehicleCoreAsync(SelectedVehicle ?? displayedVehicle ?? throw new ArgumentException("請先選取車輛"));
    private async Task OpenVehicleCoreAsync(VehicleQuickResult selected)
    {
        var history = await service.GetVehicleHistoryAsync(selected.VehicleId) ?? throw new InvalidOperationException("找不到車輛履歷");
        var v = history.Vehicle; displayedVehicle = v; SelectedVehicle = v;
        SelectedTransferCustomer = null; TransferCustomerId = ""; TransferCustomers.Clear();
        VehicleVin = v.Vin ?? ""; VehicleMake = v.Make ?? ""; VehicleModel = v.Model ?? "";
        VehicleYear = v.ModelYear?.ToString() ?? ""; VehicleColor = v.Color ?? "";
        TransferDate = DateOnly.FromDateTime(UiText.Now.DateTime).ToString("yyyy-MM-dd");
        VehicleInfo = $"{v.Plate}　{v.CustomerName}\n{v.Make ?? "廠牌待確認"} ／ {v.Model ?? "車型待確認"} ／ {v.ModelYear?.ToString() ?? v.RawYear ?? "年份待確認"}\n車色：{v.Color ?? "待確認"}\nVIN：{v.Vin ?? "待確認（17 碼）"}";
        Replace(Ownerships, history.Ownerships);
        Replace(VehicleReminders, history.Reminders.Select(r => new ReminderView(r)));
        var customers = await storage.SearchCustomersAsync(v.CustomerName);
        var customer = customers.FirstOrDefault(c => c.Id == v.CustomerId);
        OwnerContact = customer?.Phone ?? "聯絡電話待確認";
        var other = await storage.GetVehiclesAsync(v.CustomerId);
        Odometer = other.FirstOrDefault(x => x.Id == v.VehicleId)?.OdometerKm is { } km ? $"{km:N0} km" : "里程待確認";
        // 同客戶車輛由既有 storage 列舉，避免以全站快查上限截斷。
        var siblings = new List<VehicleQuickResult>();
        foreach (var row in other.Where(x => x.Id != v.VehicleId))
        {
            var sibling = await service.GetVehicleHistoryAsync(row.Id);
            if (sibling is not null) siblings.Add(sibling.Vehicle);
        }
        Replace(OtherVehicles, siblings);
        var rows = new List<HistoryOrder>();
        foreach (var summary in history.Orders)
        {
            var detail = await service.GetWorkOrderAsync(summary.Id);
            rows.Add(new(summary, detail is null || detail.Draft.Lines.Count == 0 ? "來源無施工明細" : detail.Draft.RepairDescription ?? "維修工單"));
        }
        Replace(HistoryOrders, rows.OrderByDescending(x => x.Date));
        SelectedTab = 1;
    }
    public async Task SaveVehicleAsync()
    {
        var v = displayedVehicle ?? throw new ArgumentException("請先開啟車輛");
        await service.UpdateVehicleDetailsAsync(new(v.VehicleId, VehicleVin, VehicleMake, VehicleModel,
            string.IsNullOrWhiteSpace(VehicleYear) ? null : int.Parse(VehicleYear), VehicleColor));
        await OpenVehicleCoreAsync(v); Message = "車籍已儲存";
    }
    public async Task TransferVehicleAsync()
    {
        var v = displayedVehicle ?? throw new ArgumentException("請先開啟車輛");
        await service.TransferVehicleAsync(v.VehicleId, long.Parse(TransferCustomerId), DateOnly.ParseExact(TransferDate, "yyyy-MM-dd"));
        var updated = await service.GetVehicleHistoryAsync(v.VehicleId);
        await OpenVehicleCoreAsync(updated!.Vehicle); Message = "過戶已記錄；舊工單仍保留原車主";
    }
    public async Task RefreshHomeAsync()
    {
        var dashboard = await storage.GetDashboardAsync();
        DashboardText = $"客戶 {dashboard.CustomerCount} 位 · 車輛 {dashboard.VehicleCount} 台 · {UiText.Now:yyyy/MM/dd HH:mm} 台北";
        var today = DateOnly.FromDateTime(UiText.Now.DateTime);
        Replace(Reminders, (await storage.GetRemindersAsync()).Where(r => r.DueDate is null ||
            r.DueDate <= today.AddDays(ReminderDays) || ReminderGrouping.MileageDue(r)).Select(r => new ReminderView(r)));
        var page = await service.SearchWorkOrdersAsync(orderPage, 100);
        orderTotal = page.TotalCount; Notify(nameof(OrderPageStatus));
        Replace(Orders, page.Items.Select(x => new HistoryOrder(x.Order, x.Plate)));
        Replace(DeliveryOrders, (await service.GetTodayDeliveryOrdersAsync(today))
            .Select(r => new HistoryOrder(r.Order, r.Plate, r.Order.TotalMinor != 0)));
        Replace(UnpaidOrders, Orders.Where(r => r.Source.DueMinor > 0 && r.Source.Status is not (WorkOrderStatus.Cancelled or WorkOrderStatus.Void)));
        foreach (var c in commands) c.Refresh();
    }
    public Task NewOrderAsync() => NewOrderCoreAsync(SelectedVehicle ?? displayedVehicle ?? throw new ArgumentException("請先查車並選取車輛"));
    private Task NewOrderCoreAsync(VehicleQuickResult v)
    {
        if (dirty && draftVehicleId is not null) throw new InvalidOperationException("目前工單有未儲存內容，請先儲存草稿再開新單");
        loading = true;
        try
        {
            CurrentOrder = null; draftVehicleId = v.VehicleId; draftCustomerId = v.CustomerId; draftPlate = v.Plate; draftCustomerName = v.CustomerName; draftVehicleInfo = VehicleDetails(v);
            Number = $"E-{UiText.Now:yyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}";
            Diagnosis = RepairDescription = PromisedDeliveryOn = ""; Discount = "0"; ExpiresOn = DateOnly.FromDateTime(UiText.Now.DateTime).AddDays(14).ToString("yyyy-MM-dd");
            Lines.Clear(); Additions.Clear(); Payments.Clear(); SelectedQuote = CashQuote = InvoiceQuote = CardQuote = null;
            SelectedLine = null; SelectedAddition = null; PrintModel = null;
        }
        finally { loading = false; }
        dirty = true; Notify(nameof(DraftVehicle)); Notify(nameof(DraftVehicleInfo)); Notify(nameof(Editable)); SelectedTab = 2;
        DueText = "尚未試算"; PaidText = "尚無收款"; return Task.CompletedTask;
    }
    public async Task OpenOrderAsync()
    {
        if (dirty) throw new InvalidOperationException("目前工單有未儲存內容，請先儲存");
        var detail = await service.GetWorkOrderAsync(SelectedHistoryOrder!.Id);
        if (detail is null) throw new InvalidOperationException("來源只有歷史摘要，無施工明細可開啟");
        loading = true;
        try
        {
            CurrentOrder = detail; var draft = detail.Draft;
            draftVehicleId = draft.VehicleId; draftCustomerId = draft.CustomerId;
            var history = await service.GetVehicleHistoryAsync(draft.VehicleId);
            draftPlate = history?.Vehicle.Plate ?? "車牌待確認";
            draftCustomerName = (await storage.SearchCustomersAsync(""))
                .FirstOrDefault(c => c.Id == draft.CustomerId)?.Name ?? "車主待確認";
            draftVehicleInfo = history is not null ? VehicleDetails(history.Vehicle) : "車輛資料待確認";
            Number = draft.Number; Diagnosis = draft.Diagnosis ?? ""; RepairDescription = draft.RepairDescription ?? "";
            ExpiresOn = draft.EstimateExpiresOn?.ToString("yyyy-MM-dd") ?? ""; SignatureLabel = draft.SignatureLabel ?? "客人授權簽名";
            PromisedDeliveryOn = draft.PromisedDeliveryOn?.ToString("yyyy-MM-dd") ?? "";
            AdditionalNotice = draft.AdditionalWorkNotice; Discount = (draft.OrderDiscountMinor / 100m).ToString();
            Replace(Lines, draft.Lines.Select(LineEditor.From)); Replace(Additions, draft.Additions.Select(AdditionEditor.From));
            nextAdditionId = Math.Max(1, Additions.Select(a => a.Id).DefaultIfEmpty().Max() + 1);
            Replace(Payments, detail.Payments); SelectedQuote = detail.LastQuote; PrintModel = null;
            CashQuote = InvoiceQuote = CardQuote = null;
            var receivable = await service.GetReceivableAsync(detail.Id);
            DueText = receivable is null ? "請先試算" : UiText.Money(receivable.DueMinor);
            PaidText = receivable is null ? "尚無收款" : UiText.Money(receivable.PaidMinor);
        }
        finally { loading = false; }
        dirty = false; Notify(nameof(DraftVehicle)); Notify(nameof(DraftVehicleInfo)); Notify(nameof(Editable)); SelectedTab = 2;
    }
    public WorkOrderDraft BuildDraft()
    {
        if (draftVehicleId is null || draftCustomerId is null) throw new ArgumentException("請先選取車輛開新工單");
        if (string.IsNullOrWhiteSpace(Number)) throw new ArgumentException("請輸入單號");
        if (string.IsNullOrWhiteSpace(SignatureLabel) || string.IsNullOrWhiteSpace(AdditionalNotice)) throw new ArgumentException("請保留簽名欄與加修注意事項");
        return new(CurrentOrder?.Id, Number.Trim(), draftCustomerId.Value, draftVehicleId.Value,
            CurrentOrder?.Draft.OrderDate ?? DateOnly.FromDateTime(UiText.Now.DateTime), Diagnosis, RepairDescription,
            string.IsNullOrWhiteSpace(ExpiresOn) ? null : DateOnly.ParseExact(ExpiresOn, "yyyy-MM-dd"), SignatureLabel,
            AdditionalNotice, Lines.Select(l => l.ToDraft()).ToArray(), Additions.Select(a => a.ToDraft()).ToArray(), UiText.Amount(Discount),
            string.IsNullOrWhiteSpace(PromisedDeliveryOn) ? null : DateOnly.ParseExact(PromisedDeliveryOn, "yyyy-MM-dd"));
    }
    public async Task SaveAsync()
    {
        if (!Editable) throw new InvalidOperationException("已有收款或已結束的工單不可改價");
        if (!dirty && CurrentOrder is not null) return;
        var id = await service.SaveWorkOrderAsync(BuildDraft());
        CurrentOrder = await service.GetWorkOrderAsync(id) ?? throw new InvalidOperationException("儲存後找不到工單");
        dirty = false;
        await RefreshHomeAsync();
        Message = "草稿已儲存至本機資料庫";
    }
    private CheckoutOptions Options(bool invoice, bool card) => new(invoice, card, CardSurchargeEnabled, TaxPercent / 100m, CardPercent / 100m);
    public async Task QuoteAsync()
    {
        if (dirty || CurrentOrder is null) await SaveAsync();
        var id = CurrentOrder!.Id;
        SelectedQuote = null;
        async Task<CheckoutQuote?> Preview(bool invoice, bool card)
        {
            try { return await service.QuoteCheckoutAsync(id, Options(invoice, card)); }
            catch (InvalidOperationException) when (Payments.Count > 0) { return null; }
        }
        CashQuote = await Preview(false, false);
        InvoiceQuote = await Preview(true, false);
        CardQuote = await Preview(IssueInvoice, true);
        // 服務會記住最後一次報價，必須把實際選擇放最後。
        SelectedQuote = await service.QuoteCheckoutAsync(id, Options(IssueInvoice, PaymentMethod == "刷卡"));
        CurrentOrder = await service.GetWorkOrderAsync(id);
        var receivable = await service.GetReceivableAsync(id);
        DueText = receivable is null ? "請先試算" : UiText.Money(receivable.DueMinor);
        PaidText = receivable is null ? "尚無收款" : UiText.Money(receivable.PaidMinor);
        await RefreshHomeAsync();
        SelectedTab = 3;
    }
    public async Task PayAsync()
    {
        if (CurrentOrder?.Status != WorkOrderStatus.AwaitingCheckout) throw new InvalidOperationException("請先將工單轉為待結帳");
        if (dirty || SelectedQuote is null) throw new InvalidOperationException("請先儲存並試算本次結帳");
        var receipt = await service.RecordPaymentAsync(CurrentOrder.Id, PaymentMethod, UiText.Amount(PaymentAmount));
        DueText = UiText.Money(receipt.DueMinor); PaidText = UiText.Money(receipt.PaidMinor);
        CurrentOrder = await service.GetWorkOrderAsync(CurrentOrder.Id);
        Replace(Payments, CurrentOrder!.Payments); PaymentAmount = ""; PrintModel = null;
        Message = receipt.DueMinor == 0 ? "款項已收齊，可完成交車" : "已記錄部分付款，剩餘保留應收";
        await RefreshHomeAsync();
    }
    public async Task AdvanceAsync()
    {
        if (dirty || CurrentOrder is null) await SaveAsync();
        var next = NextStatus ?? throw new InvalidOperationException("目前無下一個狀態");
        await service.TransitionWorkOrderAsync(CurrentOrder!.Id, CurrentOrder.Status, next);
        CurrentOrder = await service.GetWorkOrderAsync(CurrentOrder.Id);
        PrintModel = null; await RefreshHomeAsync();
    }
    public async Task CancelAsync()
    {
        await service.TransitionWorkOrderAsync(CurrentOrder!.Id, CurrentOrder.Status, WorkOrderStatus.Cancelled);
        CurrentOrder = await service.GetWorkOrderAsync(CurrentOrder.Id); dirty = false;
        PrintModel = null; await RefreshHomeAsync();
    }
    public async Task DeleteDraftAsync()
    {
        await service.DeleteDraftWorkOrderAsync(CurrentOrder!.Id);
        CurrentOrder = null; draftVehicleId = draftCustomerId = null;
        loading = true; Lines.Clear(); Additions.Clear(); loading = false; dirty = false;
        SelectedQuote = null; PrintModel = null; Notify(nameof(Editable)); SelectedTab = 0; await RefreshHomeAsync();
    }
    public async Task SearchCatalogAsync(bool reset)
    {
        var version = ++catalogVersion;
        var pageNumber = reset ? 1 : catalogPage;
        var page = await service.SearchCatalogAsync(new(CatalogQuery.Trim(), CatalogKind == "全部" ? null : CatalogKind, pageNumber, 30));
        if (version != catalogVersion) return;
        catalogPage = page.Page; catalogTotal = page.TotalCount;
        SelectedCatalogItem = null;
        Replace(Catalog, page.Items); CatalogStatus = $"共 {page.TotalCount} 筆 · 第 {page.Page} 頁";
        RecommendedParts.Clear(); SuggestedParts.Clear(); PartsStatus = "";
        foreach (var c in commands) c.Refresh();
    }
    public async Task SuggestPartsAsync()
    {
        SuggestedParts.Clear(); RecommendedParts.Clear();
        var item = SelectedCatalogItem;
        if (item?.Kind != "項目") { PartsStatus = "請先在左側選取「項目」"; return; }
        await SearchPartsAsync(true);
        var parts = await service.GetSuggestedPartsAsync(item.Id);
        if (SelectedCatalogItem != item) return;
        Replace(RecommendedParts, parts);
    }
    public async Task SearchPartsAsync(bool reset)
    {
        var page = await service.SearchCatalogAsync(new(PartsQuery.Trim(), "零件", reset ? 1 : partsPage, 30));
        partsPage = page.Page; partsTotal = page.TotalCount;
        Replace(SuggestedParts, page.Items);
        PartsStatus = $"共 {partsTotal} 筆 · 第 {partsPage} 頁";
        foreach (var command in commands) command.Refresh();
    }
    public async Task SaveSuggestedPartsAsync(IEnumerable<long> ids)
    {
        if (SelectedCatalogItem?.Kind != "項目") throw new ArgumentException("請先選取工資項目，再設定搭配零件");
        var additions = ids.Distinct().ToArray();
        if (additions.Length == 0) throw new ArgumentException("請先在候選清單選取零件");
        await service.SaveSuggestedPartIdsAsync(SelectedCatalogItem.Id, RecommendedParts.Select(p => p.Id).Concat(additions).Distinct().ToArray());
        await SuggestPartsAsync(); Message = "建議搭配零件已儲存至資料庫；帶入時複製數量 1 與目錄價格";
    }
    public async Task RemoveSuggestedPartAsync(long id)
    {
        if (SelectedCatalogItem?.Kind != "項目" || RecommendedParts.All(p => p.Id != id))
            throw new ArgumentException("請先選取已設定的建議零件");
        await service.SaveSuggestedPartIdsAsync(SelectedCatalogItem.Id, RecommendedParts.Where(p => p.Id != id).Select(p => p.Id).ToArray());
        await SuggestPartsAsync(); Message = "已移除這顆建議零件";
    }
    public async Task BackupToAsync(string folder)
    {
        var path = await storage.CreateBackupToAsync(folder);
        Message = $"備份完成：{path}";
    }
    public async Task ClearAllDataAsync(Func<bool> firstConfirmation, Func<string?> typedConfirmation)
    {
        var backup = await storage.CreateBackupAsync();
        if (!File.Exists(backup)) throw new IOException("自動備份未完成，資料未清空");
        if (!firstConfirmation() || typedConfirmation()?.Trim() != "清空")
        {
            Message = $"已取消清空；自動備份保留在：{backup}";
            return;
        }
        await storage.ResetToEmptyAsync();
        preferences.Save(new AppPreferences(TourSeen: true));
        loading = true;
        try
        {
            CurrentOrder = null; SelectedQuote = CashQuote = InvoiceQuote = CardQuote = null; PrintModel = null;
            draftVehicleId = draftCustomerId = null; draftPlate = draftCustomerName = draftVehicleInfo = "";
            displayedVehicle = null; nextAdditionId = 1; catalogPage = 1; catalogTotal = 0; partsPage = 1; partsTotal = 0; orderPage = 1; dirty = false;
            Vehicles.Clear(); Catalog.Clear(); Lines.Clear(); Additions.Clear(); Payments.Clear(); RecommendedParts.Clear(); SuggestedParts.Clear();
            HistoryOrders.Clear(); Ownerships.Clear(); VehicleReminders.Clear(); OtherVehicles.Clear(); TransferCustomers.Clear();
            ImportSources.Clear(); ImportProblems.Clear();
            SelectedVehicle = SelectedOtherVehicle = null; SelectedHistoryOrder = null;
            SelectedCatalogItem = null; SelectedLine = null; SelectedAddition = null; SelectedTransferCustomer = null;
            VehicleInfo = "請先搜尋並選取車輛"; OwnerContact = ""; Odometer = "里程待確認";
            Number = Diagnosis = RepairDescription = ExpiresOn = PromisedDeliveryOn = "";
            VehicleVin = VehicleMake = VehicleModel = VehicleYear = VehicleColor = "";
            TransferQuery = TransferDate = TransferCustomerId = "";
            Query = CatalogQuery = PartsQuery = PartsStatus = ""; CatalogKind = "全部"; CatalogStatus = "共 0 筆 · 第 1 頁";
            Discount = "0"; SignatureLabel = "客人授權簽名";
            AdditionalNotice = "施工中可能發現額外項目，未經同意不施工";
            IssueInvoice = false; PaymentMethod = "現金"; PaymentAmount = "";
            PrintKind = "Estimate"; PaidText = "尚無收款"; DueText = "尚未試算";
            importReady = false; ImportConfirmed = false; ImportFolder = "";
            ImportSummary = "尚未演練"; ImportNextStep = "選擇來源資料夾後執行演練。";
            SelectedTab = 0;
        }
        finally { loading = false; dirty = false; }
        Notify(nameof(DraftVehicle)); Notify(nameof(DraftVehicleInfo)); Notify(nameof(Editable));
        foreach (var command in commands) command.Refresh();
        await InitializeAsync();
        Message = $"資料已清空，可匯入自家資料。清空前備份保留在：{backup}";
    }
    public Task AddCatalogAsync()
    {
        AddCatalogRow(SelectedCatalogItem ?? throw new ArgumentException("請先選取目錄項目"));
        SelectedTab = 2; return Task.CompletedTask;
    }
    public void AddCatalogRow(CatalogRow item)
    {
        if (!Editable) throw new InvalidOperationException("請先開啟可編輯工單");
        Lines.Add(new() { Category = item.Kind == "項目" ? WorkLineCategory.Labor : WorkLineCategory.Part,
            Code = item.Code, PartNumber = item.Kind == "零件" ? item.Code : "", Name = item.Name, Unit = item.Unit,
            Price = item.PriceMinor / 100m, HourlyRate = HourlyRate });
    }
    public async Task AddCatalogWithPartsAsync()
    {
        var item = SelectedCatalogItem ?? throw new ArgumentException("請先選取目錄項目");
        var parts = item.Kind == "項目" ? await service.GetSuggestedPartsAsync(item.Id) : [];
        AddCatalogRow(item);
        foreach (var part in parts) AddCatalogRow(part);
        SelectedTab = 2;
    }
    public async Task PreparePrintAsync()
    {
        if (dirty || CurrentOrder is null) await SaveAsync();
        if (SelectedQuote is null) await QuoteAsync();
        var snapshot = await service.CreatePrintSnapshotAsync(CurrentOrder!.Id, PrintKind);
        if (snapshot.Revision != CurrentOrder.Revision) throw new InvalidOperationException("工單版本已改變，請重新開啟工單後再列印");
        var vehicle = await service.GetVehicleHistoryAsync(CurrentOrder.Draft.VehicleId);
        var storageVehicle = (await storage.GetVehiclesAsync(CurrentOrder.Draft.CustomerId)).FirstOrDefault(v => v.Id == CurrentOrder.Draft.VehicleId);
        var v = vehicle?.Vehicle;
        var context = new PrintVehicleContext(v is null ? "廠牌／車型／年份待確認" :
            $"{v.Make ?? "廠牌待確認"} ／ {v.Model ?? "車型待確認"} ／ {v.ModelYear?.ToString() ?? v.RawYear ?? "年份待確認"}",
            storageVehicle?.OdometerKm is { } km ? $"{km:N0} km" : "里程待確認");
        PrintModel = PrintDocumentModel.Build(snapshot, DocumentHeader.Length > 0 ? DocumentHeader : ShopName, ShopContact,
            CurrentOrder.Draft, Payments.ToArray(), PaidText, DueText, context, PrintOrientation, ShopAddress, ShopTaxId);
        SelectedTab = 4;
    }
    private AppPreferences CapturePreferences() => new(TourSeen: !ShowTour, PrintOrientation: PrintOrientation);
    public async Task SaveSettingsAsync()
    {
        if (HourlyRate < 0 || ReminderDays is < 0 or > 3650 || TaxPercent is < 0 or > 100 || CardPercent is < 0 or > 100)
            throw new ArgumentException("工時費率須非負，提醒天數 0–3650，稅率／刷卡率 0–100%");
        if (!Enum.IsDefined(PrintOrientation)) throw new ArgumentException("請選擇 A4 直式或 A4 橫式");
        await storage.SaveShopSettingsAsync(new(ShopName.Trim(), ShopContact.Trim(), ReminderDays, CardPercent, TaxPercent,
            CardSurchargeEnabled, Pricing.ToMinor(HourlyRate), DocumentHeader.Trim(), ShopAddress.Trim(), ShopTaxId.Trim()));
        preferences.Save(CapturePreferences());
        SelectedQuote = CashQuote = InvoiceQuote = CardQuote = null; PrintModel = null;
        Message = "店家設定已儲存；工時費率只套用新行項，請重新試算結帳";
    }
    public async Task DrillImportAsync()
    {
        importReady = false; ImportConfirmed = false; ImportProblems.Clear(); ImportSources.Clear();
        if (!Directory.Exists(ImportFolder)) throw new ArgumentException("找不到來源資料夾");
        var paths = Directory.EnumerateFiles(ImportFolder).Where(p => Path.GetExtension(p).ToLowerInvariant() is ".mdb" or ".dbf").ToArray();
        if (paths.Length == 0) throw new ArgumentException("資料夾沒有 MDB 或 DBF 檔案");
        var result = await service.DrillImportAsync(paths);
        Replace(ImportSources, result.Sources); Replace(ImportProblems, result.Summary.Problems);
        var r = result.Summary; ImportSummary = $"演練：成功 {r.Success} ／ 跳過 {r.Skipped} ／ 待確認 {r.Pending} ／ 失敗 {r.Failed}";
        importReady = r.Success > 0 && r.Failed == 0;
        ImportNextStep = importReady ? "核對結果後提交；服務會先備份再以交易匯入。" : "沒有可新增資料；請核對待確認清單，重跑不會重複新增。";
    }
    public async Task CommitImportAsync()
    {
        if (!importReady || !ImportConfirmed) throw new InvalidOperationException("請先成功演練、核對清單並勾選確認再提交");
        importReady = false; ImportConfirmed = false;
        ImportCommitResult result;
        try { result = await service.CommitDrilledImportAsync(); }
        catch { ImportNextStep = "提交未完成；請重新演練後核對再提交。"; throw; }
        var r = result.Summary;
        Replace(ImportSources, result.Sources);
        ImportSummary = $"匯入完成：成功 {r.Success} ／ 跳過 {r.Skipped} ／ 待確認 {r.Pending} ／ 失敗 {r.Failed}；備份：{r.BackupPath ?? "無"}";
        ImportNextStep = r.Pending > 0 || r.Failed > 0 ? "請核對下方來源與問題清單；保險、驗車語意待人工確認。" : "請回首頁查車並核對履歷。";
        importReady = false; await RefreshHomeAsync(); await SearchCatalogAsync(true);
    }
    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    { target.Clear(); foreach (var value in values) target.Add(value); }
}

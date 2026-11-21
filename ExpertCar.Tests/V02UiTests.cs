using ExpertCar.App.Presentation;
using ExpertCar.Core;
using ExpertCar.Data;

namespace ExpertCar.Tests;

public sealed class V02UiTests
{
    [Fact]
    public async Task FirstUseLoadsSettingsAndTourCanBeDismissed()
    {
        var f = new UiFixture(); await f.Vm.InitializeAsync();
        Assert.True(f.Vm.ShowTour);
        Assert.False(f.Vm.CardSurchargeEnabled);
        Assert.Equal(PrintOrientation.Portrait, f.Vm.PrintOrientation);
        Assert.Equal(1200m, f.Vm.HourlyRate);
        await f.Vm.DismissTourCommand.ExecuteAsync();
        Assert.False(f.Vm.ShowTour); Assert.True(f.Preferences.Value.TourSeen);
        Assert.False(f.Vm.Busy);
    }
    [Fact]
    public async Task DefaultCatalogListsItemsAndPartsAndCandidatesUsePages()
    {
        var f = new UiFixture(); await f.Vm.InitializeAsync();
        Assert.Equal("全部", f.Vm.CatalogKind);
        Assert.Contains(f.Vm.Catalog, row => row.Kind == "項目");
        await f.Vm.NextCatalogCommand.ExecuteAsync();
        Assert.Contains(f.Vm.Catalog, row => row.Kind == "零件");
        await f.Vm.PreviousCatalogCommand.ExecuteAsync();
        f.Vm.SelectedCatalogItem = f.Vm.Catalog.First(row => row.Kind == "項目");
        await f.Vm.SuggestPartsAsync();
        Assert.Single(f.Vm.SuggestedParts);
        Assert.Contains("共 1 筆", f.Vm.PartsStatus);
    }
    [Fact]
    public void WholeDollarDisplaysWithoutDecimalZeros()
    {
        Assert.Equal("NT$ 3,785", UiText.Money(378500));
        Assert.Equal("NT$ 110.25", UiText.Money(11025));
        Assert.Equal("200", new CatalogRow(1, "零件", "P", "合成零件", "個", 20000).Price);
    }
    [Fact]
    public async Task SearchHistoryAndOtherCarsBindCompleteVehicleData()
    {
        var f = new UiFixture(); f.Vm.Query = "TEST1234";
        await f.Vm.SearchCommand.ExecuteAsync();
        f.Vm.SelectedVehicle = Assert.Single(f.Vm.Vehicles);
        await f.Vm.OpenVehicleCommand.ExecuteAsync();
        Assert.Contains("12345678901234567", f.Vm.VehicleInfo);
        Assert.Contains("2020", f.Vm.VehicleInfo);
        Assert.Contains("白", f.Vm.VehicleInfo);
        Assert.Equal("82,450 km", f.Vm.Odometer);
        Assert.Single(f.Vm.OtherVehicles); Assert.Single(f.Vm.Ownerships);
        Assert.Contains(f.Vm.VehicleReminders, r => r.Kind == "保險（待確認）");
        Assert.Equal("來源無施工明細", Assert.Single(f.Vm.HistoryOrders).Description);
        Assert.Equal(1, f.Vm.SelectedTab);
    }
    [Fact]
    public async Task DraftPreservesDiagnosisVinContextExpiryAndAllLineTypes()
    {
        var f = new UiFixture(); await f.StartOrder();
        f.Vm.Diagnosis = "合成異音診斷"; f.Vm.RepairDescription = "合成維修說明";
        f.Vm.ExpiresOn = "2026-12-05";
        f.Vm.Lines.Add(new() { Name = "工資", Category = WorkLineCategory.Labor, Hourly = true, Minutes = 90, HourlyRate = 1200 });
        f.Vm.Lines.Add(new() { Name = "零件", Category = WorkLineCategory.Part, PartNumber = "PN-01", Quantity = 2, Price = 200 });
        f.Vm.Lines.Add(new() { Name = "機油", Category = WorkLineCategory.Consumable, Price = 300 });
        f.Vm.Lines.Add(new() { Name = "廢油處理", Category = WorkLineCategory.Disposal, Price = 100 });
        f.Vm.Lines.Add(new() { Name = "校正", Category = WorkLineCategory.Outsource, Vendor = "合成外包廠商", Price = 500 });
        await f.Vm.SaveCommand.ExecuteAsync();
        var draft = Assert.IsType<WorkOrderDetail>(f.Vm.CurrentOrder).Draft;
        Assert.Equal("合成異音診斷", draft.Diagnosis); Assert.Equal("合成維修說明", draft.RepairDescription);
        Assert.Equal(new DateOnly(2026, 12, 5), draft.EstimateExpiresOn);
        Assert.Equal("PN-01", draft.Lines[1].PartNumber);
        Assert.Equal(90, draft.Lines[0].LaborMinutes); Assert.Equal(120000, draft.Lines[0].HourlyRateMinor);
        Assert.Equal(5, draft.Lines.Count); Assert.Equal("合成外包廠商", draft.Lines[4].VendorName);
        Assert.Contains("未經同意不施工", draft.AdditionalWorkNotice);
        Assert.False(string.IsNullOrWhiteSpace(draft.SignatureLabel));
    }
    [Fact]
    public void LineEditorRoundTripRetainsDiscountVendorAndHourlyFields()
    {
        var original = new WorkLineDraft(WorkLineCategory.Outsource, "校正", "O-1", "P-1", "次", 1500, 20000, 100,
            LaborPriceMode.Fixed, 30, 120000, 7, 9, "合成廠商");
        var saved = LineEditor.From(original).ToDraft();
        Assert.Equal(original.Name, saved.Name); Assert.Equal(original.DiscountMinor, saved.DiscountMinor);
        Assert.Equal(original.VendorId, saved.VendorId); Assert.Equal(original.AdditionId, saved.AdditionId);
        Assert.Equal(original.VendorName, saved.VendorName);
        Assert.Equal(original.PartNumber, saved.PartNumber); Assert.Equal(original.QuantityScaled, saved.QuantityScaled);
    }
    [Fact]
    public async Task CommandRejectsInvalidFieldsInChineseWithoutSaving()
    {
        var f = new UiFixture(); await f.StartOrder();
        f.Vm.Lines.Add(new() { Name = "外包", Category = WorkLineCategory.Outsource });
        await f.Vm.SaveCommand.ExecuteAsync();
        Assert.Contains("廠商", f.Vm.Message); Assert.Null(f.Vm.CurrentOrder); Assert.False(f.Vm.Busy);
        f.Vm.Lines.Clear(); f.Vm.ExpiresOn = "錯誤日期";
        await f.Vm.SaveCommand.ExecuteAsync();
        Assert.Contains("格式", f.Vm.Message); Assert.Null(f.Vm.CurrentOrder);
    }
    [Fact]
    public async Task NewOrderCannotDiscardUnsavedWork()
    {
        var f = new UiFixture(); await f.StartOrder();
        var number = f.Vm.Number;
        await f.Vm.NewOrderCommand.ExecuteAsync();
        Assert.Equal(number, f.Vm.Number); Assert.Contains("未儲存", f.Vm.Message);
    }
    [Fact]
    public async Task AdditionalWorkRequiresConsentAndExcludedUntilAccepted()
    {
        var f = new UiFixture(); await f.StartOrder();
        f.Vm.Lines.Add(new() { Name = "基本維修", Price = 1000 });
        await f.Vm.AddAdditionCommand.ExecuteAsync();
        var addition = Assert.Single(f.Vm.Additions);
        f.Vm.Lines.Add(new() { Name = "加修用料", Price = 500 }); f.Vm.SelectedLine = f.Vm.Lines[1];
        await f.Vm.AssignAdditionCommand.ExecuteAsync(); await f.Vm.QuoteCommand.ExecuteAsync();
        Assert.Equal(100000, f.Vm.CashQuote!.TotalMinor);
        addition.Accepted = true;
        await f.Vm.SaveCommand.ExecuteAsync(); Assert.Contains("同意方式", f.Vm.Message);
        addition.Method = "現場授權"; addition.CustomerName = "合成客戶";
        await f.Vm.QuoteCommand.ExecuteAsync();
        Assert.Equal(150000, f.Vm.CashQuote!.TotalMinor);
        Assert.NotNull(f.Vm.CurrentOrder!.Draft.Additions[0].AcceptedAt);
        Assert.Contains("+08:00", addition.ConsentTime);
    }
    [Theory]
    [InlineData(false, 525000, 0)]
    [InlineData(true, 540800, 15800)]
    public async Task ParallelPreviewsEndWithActualSelectedQuote(bool enabled, long total, long fee)
    {
        var f = new UiFixture(); await f.StartOrder();
        f.Vm.Lines.Add(new() { Name = "維修", Price = 5000 });
        f.Vm.CardSurchargeEnabled = enabled; f.Vm.CardPercent = 3; f.Vm.IssueInvoice = true; f.Vm.PaymentMethod = "刷卡";
        await f.Vm.QuoteCommand.ExecuteAsync();
        Assert.Equal(500000, f.Vm.CashQuote!.TotalMinor); Assert.Equal(525000, f.Vm.InvoiceQuote!.TotalMinor);
        Assert.Equal(total, f.Vm.SelectedQuote!.TotalMinor); Assert.Equal(fee, f.Vm.SelectedQuote.CardSurchargeMinor);
        Assert.Equal(enabled, f.Vm.ShowCardSurcharge);
        Assert.Equal(new CheckoutOptions(true, true, enabled, 0.05m, 0.03m), f.Service.Options.Last());
        Assert.Equal(f.Vm.SelectedQuote, (await f.Service.GetWorkOrderAsync(f.Vm.CurrentOrder!.Id))!.LastQuote);
    }
    [Fact]
    public async Task ViewModelDisplaysServiceQuoteAndDoesNotRecalculateIt()
    {
        var f = new UiFixture(); await f.StartOrder();
        f.Vm.Lines.Add(new() { Name = "單價與回傳報價刻意不同", Price = 1 });
        f.Service.QuoteOverride = new(111, 22, 89, 77, 55, 987654, .07m, .04m, true, true, true);
        await f.Vm.QuoteCommand.ExecuteAsync();
        Assert.Equal(UiText.Money(987654), f.Vm.TotalText);
        Assert.Equal(987654, f.Vm.SelectedQuote!.TotalMinor); Assert.Equal(77, f.Vm.SelectedQuote.TaxMinor);
    }
    [Fact]
    public async Task EditingAnyLineInvalidatesQuotePrintAndPayCommand()
    {
        var f = new UiFixture(); await f.StartOrder(); f.Vm.Lines.Add(new() { Name = "維修", Price = 1000 });
        await f.Vm.QuoteCommand.ExecuteAsync(); await f.Vm.PrintCommand.ExecuteAsync(); Assert.NotNull(f.Vm.PrintModel);
        f.Vm.Lines[0].Price = 1100;
        Assert.Null(f.Vm.SelectedQuote); Assert.Null(f.Vm.PrintModel); Assert.Null(f.Vm.CashQuote);
        Assert.False(f.Vm.PayCommand.CanExecute(null));
    }
    [Fact]
    public async Task CommandsTransitionAndRecordMultiplePartialPayments()
    {
        var f = new UiFixture(); await f.StartOrder(); f.Vm.Lines.Add(new() { Name = "維修", Price = 5000 });
        await f.Vm.SaveCommand.ExecuteAsync();
        await f.Vm.NextStatusCommand.ExecuteAsync(); Assert.Equal(WorkOrderStatus.Accepted, f.Vm.CurrentOrder!.Status);
        await f.Vm.NextStatusCommand.ExecuteAsync(); Assert.Equal(WorkOrderStatus.InProgress, f.Vm.CurrentOrder.Status);
        await f.Vm.NextStatusCommand.ExecuteAsync(); Assert.Equal(WorkOrderStatus.AwaitingCheckout, f.Vm.CurrentOrder.Status);
        await f.Vm.QuoteCommand.ExecuteAsync();
        f.Vm.PaymentAmount = "3000"; await f.Vm.PayCommand.ExecuteAsync();
        Assert.Equal(UiText.Money(200000), f.Vm.DueText); Assert.Single(f.Vm.Payments); Assert.False(f.Vm.Editable);
        await f.Vm.NextStatusCommand.ExecuteAsync(); Assert.Contains("尚未付清", f.Vm.Message);
        f.Vm.PaymentMethod = "轉帳"; await f.Vm.QuoteCommand.ExecuteAsync();
        f.Vm.PaymentAmount = "2000"; await f.Vm.PayCommand.ExecuteAsync();
        Assert.Equal(2, f.Vm.Payments.Count); Assert.Equal(UiText.Money(0), f.Vm.DueText);
        await f.Vm.NextStatusCommand.ExecuteAsync(); Assert.Equal(WorkOrderStatus.Settled, f.Vm.CurrentOrder.Status);
        Assert.False(f.Vm.NextStatusCommand.CanExecute(null)); Assert.Empty(f.Vm.UnpaidOrders);
    }
    [Fact]
    public async Task LowerAlternativePreviewAfterPartialInvoicePaymentDoesNotBlockActualQuote()
    {
        var f = new UiFixture(); await f.StartOrder(); f.Vm.Lines.Add(new() { Name = "維修", Price = 5000 });
        await f.Vm.SaveCommand.ExecuteAsync();
        for (var i = 0; i < 3; i++) await f.Vm.NextStatusCommand.ExecuteAsync();
        f.Vm.IssueInvoice = true; await f.Vm.QuoteCommand.ExecuteAsync();
        f.Vm.PaymentAmount = "5100"; await f.Vm.PayCommand.ExecuteAsync();
        await f.Vm.QuoteCommand.ExecuteAsync();
        Assert.Null(f.Vm.CashQuote); Assert.Equal(525000, f.Vm.SelectedQuote!.TotalMinor);
        Assert.Equal(UiText.Money(15000), f.Vm.DueText);
    }
    [Fact]
    public async Task OverpaymentAndZeroPaymentAreRejectedAndKeepHistory()
    {
        var f = new UiFixture(); await f.StartOrder(); f.Vm.Lines.Add(new() { Name = "維修", Price = 1000 });
        await f.Vm.SaveCommand.ExecuteAsync(); for (var i = 0; i < 3; i++) await f.Vm.NextStatusCommand.ExecuteAsync();
        await f.Vm.QuoteCommand.ExecuteAsync();
        f.Vm.PaymentAmount = "1001"; await f.Vm.PayCommand.ExecuteAsync(); Assert.Contains("超過", f.Vm.Message);
        f.Vm.PaymentAmount = "0"; await f.Vm.PayCommand.ExecuteAsync(); Assert.NotEmpty(f.Vm.Message);
        Assert.Empty(f.Vm.Payments);
    }
    [Fact]
    public async Task CatalogPagingFilteringAndBundleCopyKeepCatalogUnchanged()
    {
        var f = new UiFixture(); await f.StartOrder();
        await f.Vm.SearchCatalogCommand.ExecuteAsync(); Assert.Equal(30, f.Vm.Catalog.Count);
        Assert.True(f.Vm.NextCatalogCommand.CanExecute(null)); await f.Vm.NextCatalogCommand.ExecuteAsync();
        Assert.Contains("第 2 頁", f.Vm.CatalogStatus);
        f.Vm.CatalogQuery = "LAB-001"; await f.Vm.SearchCatalogCommand.ExecuteAsync();
        f.Vm.SelectedCatalogItem = Assert.Single(f.Vm.Catalog);
        await f.Vm.SaveSuggestedPartsAsync([100]); await f.Vm.AddCatalogWithPartsCommand.ExecuteAsync();
        Assert.Equal(2, f.Vm.Lines.Count); Assert.Equal("PART-100", f.Vm.Lines[1].PartNumber);
        f.Vm.Lines[1].Price = 9;
        Assert.Equal(20000, (await f.Service.SearchCatalogAsync(new("PART-100", null))).Items[0].PriceMinor);
        Assert.Equal(new long[] { 100 }, await f.Service.GetSuggestedPartIdsAsync(1));
        await f.Vm.SaveSettingsCommand.ExecuteAsync(); Assert.Contains(100L, await f.Service.GetSuggestedPartIdsAsync(1));
    }
    [Fact]
    public async Task ReopenedOrderRetainsFieldsAndDoesNotSaveOnRead()
    {
        var f = new UiFixture(); await f.StartOrder(); f.Vm.Lines.Add(new() { Name = "維修", Price = 1000, LineDiscount = 10 });
        f.Vm.Diagnosis = "待核對"; await f.Vm.SaveCommand.ExecuteAsync(); var detail = f.Vm.CurrentOrder!;
        f.Vm.SelectedHistoryOrder = new(new(detail.Id, detail.Draft.Number, detail.Status, detail.Draft.OrderDate, 99000, 99000), "維修");
        await f.Vm.OpenOrderCommand.ExecuteAsync(); await f.Vm.SaveCommand.ExecuteAsync();
        Assert.Equal(detail.Revision, f.Vm.CurrentOrder!.Revision); Assert.Equal("待核對", f.Vm.Diagnosis);
        Assert.Equal(10, Assert.Single(f.Vm.Lines).LineDiscount);
    }
    [Fact]
    public async Task SettingsAndAppSpecificPreferencesPersistAndValidate()
    {
        var f = new UiFixture(); await f.Vm.InitializeAsync();
        f.Vm.HourlyRate = 1500; f.Vm.DocumentHeader = "合成汽車服務中心";
        f.Vm.CardSurchargeEnabled = true; f.Vm.CardPercent = 3;
        await f.Vm.SaveSettingsCommand.ExecuteAsync();
        Assert.Equal(150000, f.Storage.Settings.HourlyRateMinor);
        Assert.Equal("合成汽車服務中心", f.Storage.Settings.DocumentHeader);
        Assert.True(f.Storage.Settings.CardSurchargeEnabled);
        f.Vm.TaxPercent = 101; await f.Vm.SaveSettingsCommand.ExecuteAsync(); Assert.Contains("0–100", f.Vm.Message);
        Assert.Equal(5, f.Storage.Settings.TaxPercent);
    }
    [Fact]
    public async Task ImportResultShowsAllCountersAndNextStepWithoutInventingMemoryImport()
    {
        var f = new UiFixture(); var directory = Path.Combine(Path.GetTempPath(), "expertcar-ui-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "synthetic.dbf"), "synthetic");
            f.Vm.ImportFolder = directory; await f.Vm.DrillImportCommand.ExecuteAsync();
            Assert.Contains("成功 0", f.Vm.ImportSummary); Assert.False(f.Vm.CommitImportCommand.CanExecute(null));
            Assert.Contains("沒有可新增", f.Vm.ImportNextStep);
            f.Service.DrillOverride = new(new(2, 3, 4, 0, [new("synthetic.dbf", "test", 1, "DuplicatePlate")], false),
                [new("synthetic.dbf", "test", 10, 2, 3, 4, 1)]);
            await f.Vm.DrillImportCommand.ExecuteAsync(); Assert.False(f.Vm.CommitImportCommand.CanExecute(null));
            f.Vm.ImportConfirmed = true; Assert.True(f.Vm.CommitImportCommand.CanExecute(null));
            Assert.Contains("待確認 4", f.Vm.ImportSummary); Assert.Single(f.Vm.ImportProblems);
            f.Service.CommitOverride = new(new(2, 3, 4, 1, "private-backup", 1), f.Service.DrillOverride.Sources);
            await f.Vm.CommitImportCommand.ExecuteAsync();
            Assert.Contains("匯入完成", f.Vm.ImportSummary); Assert.Contains("人工確認", f.Vm.ImportNextStep);
            Assert.False(f.Vm.CommitImportCommand.CanExecute(null));
            f.Vm.ImportFolder = directory + "-other"; Assert.Empty(f.Vm.ImportSources);
        }
        finally { Directory.Delete(directory, true); }
    }
    [Theory]
    [InlineData("Estimate", "估價單")]
    [InlineData("WorkOrder", "維修單")]
    [InlineData("Receipt", "收據")]
    public async Task PrintModelsUseSnapshotMoneyAndPreserveRequiredFields(string kind, string title)
    {
        var f = new UiFixture(); await f.StartOrder();
        f.Vm.Diagnosis = "合成診斷"; f.Vm.RepairDescription = "合成維修說明"; f.Vm.ExpiresOn = "2026-12-05";
        f.Vm.Lines.Add(new() { Name = "合成零件", PartNumber = "PN-ONLY", Price = 5000 });
        await f.Vm.QuoteCommand.ExecuteAsync(); f.Vm.PrintKind = kind; await f.Vm.PrintCommand.ExecuteAsync();
        var model = Assert.IsType<PrintDocumentModel>(f.Vm.PrintModel);
        Assert.Equal(title, model.Title); Assert.Equal(f.Vm.SelectedQuote, model.Checkout);
        var content = string.Join("\n", model.Pages.SelectMany(p => p.Rows).Select(r => r.Text));
        Assert.Contains("12345678901234567", content); Assert.Contains("合成診斷", content);
        Assert.Contains("合成維修說明", content); Assert.Contains("PN-ONLY", content);
        Assert.Equal(kind == "Estimate", content.Contains("估價有效期限：2026/12/05"));
        Assert.Equal(PrintOrientation.Portrait, model.Layout.Orientation);
        Assert.True(model.Layout.PageHeight > model.Layout.PageWidth);
        Assert.Contains("未經同意不施工", content); Assert.DoesNotContain("刷卡加成", content);
        Assert.Equal("客人授權簽名", model.SignatureLabel);
        Assert.All(model.Pages, p => Assert.InRange(p.Rows.Count, 1, model.RowsPerPage));
    }
    [Fact]
    public async Task PrintPaginatesLongDescriptionsWithoutLosingRowsOrUnicode()
    {
        var f = new UiFixture(); await f.StartOrder(); f.Vm.Diagnosis = new string('測', 260) + "🛠尾端";
        for (var i = 0; i < 70; i++) f.Vm.Lines.Add(new() { Name = $"項目-{i:D3}-" + new string('修', 75), Price = 1 });
        await f.Vm.QuoteCommand.ExecuteAsync(); await f.Vm.PrintCommand.ExecuteAsync();
        var model = f.Vm.PrintModel!; Assert.True(model.Pages.Count > 5);
        Assert.Equal(Enumerable.Range(1, model.Pages.Count), model.Pages.Select(p => p.Number));
        Assert.All(model.Pages, p => Assert.Equal(model.Pages.Count, p.TotalPages));
        Assert.All(model.Pages, p => Assert.InRange(p.Rows.Count, 1, model.RowsPerPage));
        var rows = model.Pages.SelectMany(p => p.Rows).ToArray();
        Assert.Equal(70, rows.Count(r => r.Quantity.Length > 0 && !r.IsHeading));
        var text = string.Join("", rows.Select(r => r.Text)); Assert.Contains("🛠尾端", text);
        Assert.Contains("項目-069", text); Assert.DoesNotContain('\uFFFD', text);
        Assert.All(rows, r => Assert.True(r.Text.EnumerateRunes().Count() <= 65));
    }
    [Fact]
    public async Task PrintRejectsMissingSnapshotCheckoutAndMismatchedRevision()
    {
        var f = new UiFixture(); await f.StartOrder(); f.Vm.Lines.Add(new() { Name = "維修", Price = 1 });
        await f.Vm.QuoteCommand.ExecuteAsync(); var snapshot = await f.Service.CreatePrintSnapshotAsync(f.Vm.CurrentOrder!.Id, "Estimate");
        Assert.Throws<InvalidOperationException>(() => PrintDocumentModel.Build(snapshot with { Checkout = null }, "", "", f.Vm.BuildDraft(), [], "", ""));
        f.Service.BadSnapshotRevision = true; await f.Vm.PrintCommand.ExecuteAsync();
        Assert.Null(f.Vm.PrintModel); Assert.Contains("版本", f.Vm.Message);
    }
    [Theory]
    [InlineData("Estimate", PrintOrientation.Portrait)]
    [InlineData("WorkOrder", PrintOrientation.Portrait)]
    [InlineData("Receipt", PrintOrientation.Portrait)]
    [InlineData("Estimate", PrintOrientation.Landscape)]
    [InlineData("WorkOrder", PrintOrientation.Landscape)]
    [InlineData("Receipt", PrintOrientation.Landscape)]
    public async Task FiftyLinesRepeatHeadersAndPageNumbersAndSignOnlyOnLastPage(string kind, PrintOrientation orientation)
    {
        var f = new UiFixture(); await f.StartOrder();
        f.Vm.PrintOrientation = orientation; f.Vm.ShopAddress = "合成地址"; f.Vm.ShopTaxId = "";
        f.Vm.PrintKind = kind;
        for (var i = 1; i <= 50; i++) f.Vm.Lines.Add(new() { Name = $"明細-{i:D3}", PartNumber = $"P-{i:D3}", Price = i });
        await f.Vm.PrintCommand.ExecuteAsync();
        var model = Assert.IsType<PrintDocumentModel>(f.Vm.PrintModel);
        Assert.Equal(orientation, model.Layout.Orientation);
        Assert.Equal(orientation == PrintOrientation.Portrait, model.Layout.PageHeight > model.Layout.PageWidth);
        Assert.True(model.Pages.Count >= 2);
        Assert.Equal(Enumerable.Range(1, model.Pages.Count), model.Pages.Select(p => p.Number));
        Assert.Contains(model.HeaderRows, r => r.Text.Contains("地址：合成地址"));
        Assert.Contains(model.HeaderRows, r => r.Text.Contains("電話：合成聯絡方式　統編："));
        Assert.Contains(model.HeaderRows, r => r.Text.Contains("單號：") && r.Text.Contains("日期："));
        Assert.All(model.Pages, page =>
        {
            Assert.Equal(model.Pages.Count, page.TotalPages);
            Assert.Equal($"第 {page.Number}／{model.Pages.Count} 頁", page.PageLabel);
            var heading = Assert.Single(page.Rows, r => r.Kind == PrintRowKind.TableHeader);
            Assert.Equal("類別", heading.Category); Assert.Equal("品名／料號", heading.Text);
            Assert.Equal("數量", heading.Quantity); Assert.Equal("單價（元）", heading.UnitPrice); Assert.Equal("小計（元）", heading.Amount);
            Assert.InRange(page.Rows.Count, 1, model.RowsPerPage);
            Assert.True(model.Layout.BodyTop(model.HeaderRows.Count) + page.Rows.Count * model.Layout.RowHeight
                <= model.Layout.PageHeight - PrintLayout.Margin - PrintLayout.FooterHeight);
        });
        Assert.All(model.Pages.Skip(1), p => Assert.Equal(PrintRowKind.TableHeader, p.Rows[0].Kind));
        Assert.All(model.Pages.SkipLast(1), p => Assert.DoesNotContain(p.Rows, r => r.Kind == PrintRowKind.Signature || r.Text.Contains("客人授權簽名")));
        var last = model.Pages[^1];
        Assert.Contains(last.Rows, r => r.Kind == PrintRowKind.Signature && r.Text.Contains("客人授權簽名") && r.Text.Contains("日期："));
        Assert.Contains(last.Rows, r => r.Kind == PrintRowKind.Signature && r.Text.Contains("技師：") && r.Text.Contains("接待："));
        var details = model.Pages.SelectMany(p => p.Rows).Where(r => r.Kind == PrintRowKind.Detail).ToArray();
        Assert.Equal(50, details.Count(r => r.Quantity.Length > 0));
        for (var i = 1; i <= 50; i++)
        {
            var line = Assert.Single(details, r => r.Text.Contains($"明細-{i:D3}"));
            Assert.Equal("零件", line.Category); Assert.Contains($"P-{i:D3}", line.Text);
            Assert.Equal($"{i:N0}", line.Amount);
        }
        var portrait = PrintDocumentModel.Build(await f.Service.CreatePrintSnapshotAsync(f.Vm.CurrentOrder!.Id, kind), "", "",
            f.Vm.BuildDraft(), [], "", "", orientation: PrintOrientation.Portrait);
        var landscape = PrintDocumentModel.Build(await f.Service.CreatePrintSnapshotAsync(f.Vm.CurrentOrder.Id, kind), "", "",
            f.Vm.BuildDraft(), [], "", "", orientation: PrintOrientation.Landscape);
        Assert.True(portrait.RowsPerPage > landscape.RowsPerPage);
        Assert.True(landscape.Pages.Count > portrait.Pages.Count);
    }
    [Theory]
    [InlineData(PrintOrientation.Portrait)]
    [InlineData(PrintOrientation.Landscape)]
    public async Task PrintShowsHoursAndCategorySubtotalsFromSnapshot(PrintOrientation orientation)
    {
        var f = new UiFixture(); await f.StartOrder(); f.Vm.PrintOrientation = orientation;
        f.Vm.Lines.Add(new() { Name = "檢修", Category = WorkLineCategory.Labor, Hourly = true, Minutes = 150, HourlyRate = 800 });
        f.Vm.Lines.Add(new() { Name = "合成零件", Category = WorkLineCategory.Part, Price = 300, LineDiscount = 20 });
        f.Vm.Lines.Add(new() { Name = "合成耗材", Category = WorkLineCategory.Consumable, Price = 100 });
        f.Vm.Lines.Add(new() { Name = "環保處理", Category = WorkLineCategory.Disposal, Price = 50 });
        f.Vm.Lines.Add(new() { Name = "外包檢測", Category = WorkLineCategory.Outsource, Vendor = "合成廠商", Price = 500 });
        f.Vm.Discount = "30"; f.Vm.IssueInvoice = true; f.Vm.PaymentMethod = "刷卡";
        f.Vm.CardSurchargeEnabled = true; f.Vm.CardPercent = 3;
        await f.Vm.PrintCommand.ExecuteAsync();
        var model = Assert.IsType<PrintDocumentModel>(f.Vm.PrintModel);
        var text = string.Join("", model.Pages.SelectMany(p => p.Rows).Select(r => r.Text));
        Assert.Contains("2.5 小時 × 800", text);
        foreach (var (label, amount) in new[] { ("工資", 200000), ("零件", 28000), ("油液耗材", 10000), ("環保處理費", 5000), ("外包", 50000) })
            Assert.Contains($"{label}小計：{UiText.Money(amount)}", text);
        Assert.Contains($"折扣：{UiText.Money(model.Checkout.DiscountMinor)}", text);
        Assert.Contains($"稅額（{model.Checkout.TaxRate:P0}）：{UiText.Money(model.Checkout.TaxMinor)}", text);
        Assert.Contains($"刷卡加成（{model.Checkout.CardRate:P2}）：{UiText.Money(model.Checkout.CardSurchargeMinor)}", text);
        Assert.Contains($"應收總額：{UiText.Money(model.Checkout.TotalMinor)}", text);
        var snapshot = await f.Service.CreatePrintSnapshotAsync(f.Vm.CurrentOrder!.Id, "Estimate");
        var changedLines = snapshot.Model.Lines.Select((line, index) => index == 0 ? line with { TotalMinor = 12345 } : line).ToArray();
        var changed = PrintDocumentModel.Build(snapshot with { Model = snapshot.Model with { Lines = changedLines } }, "", "", f.Vm.BuildDraft(), [], "", "");
        Assert.Contains(changed.Pages.SelectMany(p => p.Rows), r => r.Text == $"工資小計：{UiText.Money(12345)}");
    }
    [Theory]
    [InlineData(PrintOrientation.Portrait)]
    [InlineData(PrintOrientation.Landscape)]
    public async Task SignatureBlockMovesTogetherWhenPageHasOnlyOneRowLeft(PrintOrientation orientation)
    {
        var f = new UiFixture(); await f.StartOrder();
        f.Vm.Lines.Add(new() { Name = "維修", Price = 1 }); await f.Vm.QuoteCommand.ExecuteAsync();
        var snapshot = await f.Service.CreatePrintSnapshotAsync(f.Vm.CurrentOrder!.Id, "Estimate");
        PrintDocumentModel Build(int extra) => PrintDocumentModel.Build(snapshot with
        { Model = snapshot.Model with { Diagnosis = string.Join('\n', Enumerable.Repeat("測試診斷", extra)) } },
            "合成店名", "合成電話", f.Vm.BuildDraft(), [], "", "", orientation: orientation);
        var model = Enumerable.Range(1, 80).Select(Build).First(m => m.Pages.Count > 1 &&
            m.Pages[^2].Rows.Count == m.RowsPerPage - 1 && m.Pages[^1].Rows.Skip(1).All(r => r.Kind == PrintRowKind.Signature));
        Assert.All(model.Pages.SkipLast(1), p => Assert.DoesNotContain(p.Rows, r => r.Kind == PrintRowKind.Signature));
        Assert.Equal(2, model.Pages[^1].Rows.Count(r => r.Kind == PrintRowKind.Signature));
    }
    [Theory]
    [InlineData(PrintOrientation.Portrait)]
    [InlineData(PrintOrientation.Landscape)]
    public async Task VeryLongSingleItemAndDiagnosisKeepUnicodeAndAllCellsAcrossPages(PrintOrientation orientation)
    {
        var f = new UiFixture(); await f.StartOrder(); f.Vm.PrintOrientation = orientation;
        f.Vm.Diagnosis = new string('診', 4000) + "🛠診斷尾端";
        f.Vm.Lines.Add(new() { Name = "項目起點" + new string('修', 4000) + "🛠明細尾端", PartNumber = "PN-END", Price = 123 });
        await f.Vm.PrintCommand.ExecuteAsync();
        var model = Assert.IsType<PrintDocumentModel>(f.Vm.PrintModel);
        var rows = model.Pages.SelectMany(p => p.Rows).ToArray();
        var text = string.Join("", rows.Where(r => r.Kind != PrintRowKind.TableHeader).Select(r => r.Text));
        Assert.Contains(f.Vm.Diagnosis, text); Assert.Contains(f.Vm.Lines[0].Name, text); Assert.Contains("PN-END", text);
        Assert.DoesNotContain('\uFFFD', text);
        Assert.Single(rows, r => r.Kind == PrintRowKind.Detail && r.Amount.Length > 0);
        Assert.All(model.Pages, p => Assert.InRange(p.Rows.Count, 1, model.RowsPerPage));
        Assert.All(model.Pages.SkipLast(1), p => Assert.DoesNotContain(p.Rows, r => r.Kind == PrintRowKind.Signature));
    }
    [Fact]
    public async Task AsyncCommandPreventsDuplicateSubmissionAndNotifiesAvailability()
    {
        var gate = new TaskCompletionSource(); var runs = 0; var changes = 0;
        var command = new AsyncCommand(async () => { runs++; await gate.Task; });
        command.CanExecuteChanged += (_, _) => changes++;
        var task = command.ExecuteAsync(); Assert.False(command.CanExecute(null));
        await command.ExecuteAsync(); Assert.Equal(1, runs);
        gate.SetResult(); await task; Assert.True(command.CanExecute(null)); Assert.Equal(2, changes);
    }
    [Fact]
    public void AppPreferencesJsonRoundTripsWithoutWpf()
    {
        var directory = Path.Combine(Path.GetTempPath(), "expertcar-pref-" + Guid.NewGuid());
        try
        {
            var store = new JsonAppPreferencesStore(Path.Combine(directory, "prefs.json"));
            Assert.False(store.Load().TourSeen);
            store.Save(new(98700, "合成抬頭", true, new() { [1] = [100, 101] }));
            var read = store.Load(); Assert.Equal(98700, read.HourlyRateMinor); Assert.True(read.TourSeen);
            Assert.Equal(new long[] { 100, 101 }, read.CatalogParts![1]);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(PrintOrientation.Portrait)]
    [InlineData(PrintOrientation.Landscape)]
    public void PrintPreferencesRoundTripAndOldJsonDefaultsToPortrait(PrintOrientation orientation)
    {
        var directory = Path.Combine(Path.GetTempPath(), "expertcar-print-pref-" + Guid.NewGuid());
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "ui-preferences-v02.json");
            File.WriteAllText(path, "{\"HourlyRateMinor\":98700,\"DocumentHeader\":\"合成抬頭\",\"TourSeen\":true,\"CatalogParts\":{\"1\":[100]}}");
            var store = new JsonAppPreferencesStore(path); var legacy = store.Load();
            Assert.Equal(PrintOrientation.Portrait, legacy.PrintOrientation);
            Assert.Equal("", legacy.ShopAddress); Assert.Equal("", legacy.ShopTaxId);
            store.Save(legacy with { PrintOrientation = orientation, ShopAddress = "合成地址", ShopTaxId = "" });
            var read = store.Load();
            Assert.Equal(orientation, read.PrintOrientation); Assert.Equal("合成地址", read.ShopAddress); Assert.Equal("", read.ShopTaxId);
            Assert.Equal(legacy.HourlyRateMinor, read.HourlyRateMinor); Assert.Equal(legacy.DocumentHeader, read.DocumentHeader);
            Assert.Equal(legacy.CatalogParts![1], read.CatalogParts![1]);
            Assert.Contains($"\"PrintOrientation\":\"{orientation}\"", File.ReadAllText(path));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task PrintSettingsSurviveAllPreferenceSavePathsAndReload()
    {
        var f = new UiFixture(); await f.StartOrder();
        f.Vm.PrintOrientation = PrintOrientation.Landscape;
        f.Vm.ShopAddress = "合成店址"; f.Vm.ShopTaxId = "00000000";
        await f.Vm.SaveSettingsCommand.ExecuteAsync();
        await f.Vm.DismissTourCommand.ExecuteAsync();
        f.Vm.CatalogQuery = "LAB-001"; await f.Vm.SearchCatalogCommand.ExecuteAsync();
        f.Vm.SelectedCatalogItem = Assert.Single(f.Vm.Catalog); await f.Vm.SaveSuggestedPartsAsync([100]);
        var loaded = new WorkspaceV02ViewModel(f.Service, f.Storage, f.Preferences); await loaded.InitializeAsync();
        Assert.Equal(PrintOrientation.Landscape, loaded.PrintOrientation);
        Assert.Equal("合成店址", loaded.ShopAddress); Assert.Equal("00000000", loaded.ShopTaxId); Assert.False(loaded.ShowTour);
        loaded.PrintOrientation = PrintOrientation.Portrait; await loaded.SaveSettingsCommand.ExecuteAsync();
        Assert.Equal(PrintOrientation.Portrait, f.Preferences.Value.PrintOrientation);
        Assert.Equal(new long[] { 100 }, await f.Service.GetSuggestedPartIdsAsync(1));
    }

    [Fact]
    public async Task ChangingOrientationOrHeaderClearsOldPreviewAndRegeneratesCorrectPageSize()
    {
        var f = new UiFixture(); await f.StartOrder(); f.Vm.Lines.Add(new() { Name = "維修", Price = 1 });
        await f.Vm.PrintCommand.ExecuteAsync(); Assert.NotNull(f.Vm.PrintModel);
        f.Vm.PrintOrientation = PrintOrientation.Landscape; Assert.Null(f.Vm.PrintModel);
        await f.Vm.PrintCommand.ExecuteAsync();
        Assert.Equal(PrintOrientation.Landscape, f.Vm.PrintModel!.Layout.Orientation);
        f.Vm.ShopAddress = "更新合成店址"; Assert.Null(f.Vm.PrintModel);
        await f.Vm.PrintCommand.ExecuteAsync();
        Assert.Contains(f.Vm.PrintModel!.HeaderRows, r => r.Text == "地址：更新合成店址");
    }

    [Fact]
    public async Task FieldValidationDisablesCommandsUntilCorrected()
    {
        var f = new UiFixture(); await f.StartOrder();
        f.Vm.HasInputErrors = true; Assert.False(f.Vm.SaveCommand.CanExecute(null));
        Assert.False(f.Vm.QuoteCommand.CanExecute(null));
        f.Vm.HasInputErrors = false; Assert.True(f.Vm.SaveCommand.CanExecute(null));
    }
    [Fact]
    public async Task HistoryUsesDisplayedVehicleEvenWhenOtherSelectionChanges()
    {
        var f = new UiFixture(); await f.Vm.InitializeAsync();
        f.Vm.SelectedVehicle = f.Vm.Vehicles[0]; await f.Vm.OpenVehicleCommand.ExecuteAsync();
        f.Vm.SelectedOtherVehicle = Assert.Single(f.Vm.OtherVehicles);
        await f.Vm.OpenOtherVehicleCommand.ExecuteAsync();
        Assert.Contains("OTHER-01", f.Vm.VehicleInfo);
        await f.Vm.HistoryNewOrderCommand.ExecuteAsync();
        Assert.Equal(2, f.Vm.BuildDraft().VehicleId);
        Assert.Contains("OTHER-01", f.Vm.DraftVehicle);
    }
    [Fact]
    public async Task PrintUsesCheckoutSnapshotRatherThanLegacyDocumentTotals()
    {
        var f = new UiFixture(); await f.StartOrder(); f.Vm.Lines.Add(new() { Name = "維修", Price = 1 });
        await f.Vm.QuoteCommand.ExecuteAsync();
        var snapshot = await f.Service.CreatePrintSnapshotAsync(f.Vm.CurrentOrder!.Id, "Estimate");
        var quote = snapshot.Checkout! with { TotalMinor = 765432, CardSurchargeApplied = true, CardSurchargeMinor = 500 };
        var model = PrintDocumentModel.Build(snapshot with { Checkout = quote }, "合成抬頭", "合成聯絡方式", f.Vm.BuildDraft(), [], "", "");
        var text = string.Join("", model.Pages.SelectMany(p => p.Rows).Select(r => r.Text));
        Assert.Equal(765432, model.Checkout.TotalMinor); Assert.Contains(UiText.Money(765432), text);
        Assert.Contains("刷卡加成", text);
    }

    [Fact]
    public async Task AwaitingDeliveryAppearsBeforeCheckoutAndUnknownMoneyIsMarked()
    {
        var f = new UiFixture(); await f.StartOrder(); f.Vm.PromisedDeliveryOn = DateOnly.FromDateTime(UiText.Now.DateTime).ToString("yyyy-MM-dd"); f.Vm.Lines.Add(new() { Name = "維修", Price = 1000 });
        await f.Vm.SaveCommand.ExecuteAsync(); for (var i = 0; i < 3; i++) await f.Vm.NextStatusCommand.ExecuteAsync();
        Assert.Equal("待試算", Assert.Single(f.Vm.DeliveryOrders).Total);
        await f.Vm.QuoteCommand.ExecuteAsync(); await f.Vm.RefreshCommand.ExecuteAsync();
        Assert.Equal(UiText.Money(100000), Assert.Single(f.Vm.UnpaidOrders).Due);
    }

    private sealed class UiFixture
    {
        public PreferencesMemory Preferences { get; } = new();
        public StorageStub Storage { get; } = new();
        public ServiceSpy Service { get; }
        public WorkspaceV02ViewModel Vm { get; }
        public UiFixture()
        {
            var vehicle = new VehicleQuickResult(1, 1, "TEST-1234", "合成客戶", "Toyota", "合成車型", 2020, "109", "白", "12345678901234567");
            var other = vehicle with { VehicleId = 2, Plate = "OTHER-01" };
            var history = new VehicleHistory(vehicle, [new(1, "合成客戶", new(2020, 1, 1), null)],
                [new(99, "OLD-SUMMARY", WorkOrderStatus.Settled, new(2020, 1, 1), 10000, 0)],
                [new(1, 1, "合成客戶", null, vehicle.Plate, "Insurance", null, null, 82450)]);
            var catalog = Enumerable.Range(1, 35).Select(i => new CatalogRow(i, "項目", $"LAB-{i:D3}", $"合成工資{i}", "次", 10000))
                .Append(new(100, "零件", "PART-100", "合成零件", "個", 20000));
            Service = new(new MemoryV02Service([history, new(other, [], [], [])], catalog));
            Vm = new(Service, Storage, Preferences);
        }
        public async Task StartOrder()
        {
            await Vm.InitializeAsync(); Vm.SelectedVehicle = Vm.Vehicles.First(); await Vm.NewOrderCommand.ExecuteAsync();
        }
    }
    private sealed class PreferencesMemory : IAppPreferencesStore
    {
        public AppPreferences Value { get; private set; } = new();
        public AppPreferences Load() => Value;
        public void Save(AppPreferences value) => Value = value;
    }
    private sealed class ServiceSpy(IExpertCarV02Service inner) : IExpertCarV02Service
    {
        public List<CheckoutOptions> Options { get; } = [];
        public CheckoutQuote? QuoteOverride { get; set; }
        public ImportDrillResult? DrillOverride { get; set; }
        public ImportCommitResult? CommitOverride { get; set; }
        public bool BadSnapshotRevision { get; set; }
        public Task<IReadOnlyList<VehicleQuickResult>> QuickFindVehiclesAsync(string q, int limit = 20, CancellationToken ct = default) => inner.QuickFindVehiclesAsync(q, limit, ct);
        public Task<WorkOrderPage> SearchWorkOrdersAsync(int page = 1, int pageSize = 30, CancellationToken ct = default) => inner.SearchWorkOrdersAsync(page, pageSize, ct);
        public Task<IReadOnlyList<WorkOrderListRow>> GetTodayDeliveryOrdersAsync(DateOnly today, CancellationToken ct = default) => inner.GetTodayDeliveryOrdersAsync(today, ct);
        public Task<ReceivableResult?> GetReceivableAsync(long id, CancellationToken ct = default) => inner.GetReceivableAsync(id, ct);
        public Task<IReadOnlyList<long>> GetSuggestedPartIdsAsync(long id, CancellationToken ct = default) => inner.GetSuggestedPartIdsAsync(id, ct);
        public Task<IReadOnlyList<CatalogRow>> GetSuggestedPartsAsync(long id, CancellationToken ct = default) => inner.GetSuggestedPartsAsync(id, ct);
        public Task SaveSuggestedPartIdsAsync(long id, IReadOnlyList<long> ids, CancellationToken ct = default) => inner.SaveSuggestedPartIdsAsync(id, ids, ct);
        public Task<VehicleHistory?> GetVehicleHistoryAsync(long id, CancellationToken ct = default) => inner.GetVehicleHistoryAsync(id, ct);
        public Task<WorkOrderDetail?> GetWorkOrderAsync(long id, CancellationToken ct = default) => inner.GetWorkOrderAsync(id, ct);
        public Task<long> SaveWorkOrderAsync(WorkOrderDraft draft, CancellationToken ct = default) => inner.SaveWorkOrderAsync(draft, ct);
        public Task DeleteDraftWorkOrderAsync(long id, CancellationToken ct = default) => inner.DeleteDraftWorkOrderAsync(id, ct);
        public Task TransitionWorkOrderAsync(long id, WorkOrderStatus from, WorkOrderStatus to, CancellationToken ct = default) => inner.TransitionWorkOrderAsync(id, from, to, ct);
        public Task<CatalogPage> SearchCatalogAsync(CatalogSearchQuery query, CancellationToken ct = default) => inner.SearchCatalogAsync(query, ct);
        public async Task<CheckoutQuote> QuoteCheckoutAsync(long id, CheckoutOptions options, CancellationToken ct = default)
        { Options.Add(options); return QuoteOverride ?? await inner.QuoteCheckoutAsync(id, options, ct); }
        public Task<ReceivableResult> RecordPaymentAsync(long id, string method, long amount, CancellationToken ct = default) => inner.RecordPaymentAsync(id, method, amount, ct);
        public async Task<PrintSnapshot> CreatePrintSnapshotAsync(long id, string kind, CancellationToken ct = default)
        { var result = await inner.CreatePrintSnapshotAsync(id, kind, ct); return BadSnapshotRevision ? result with { Revision = result.Revision + 1 } : result; }
        public Task<ImportDrillResult> DrillImportAsync(IReadOnlyList<string> paths, CancellationToken ct = default) => DrillOverride is { } r ? Task.FromResult(r) : inner.DrillImportAsync(paths, ct);
        public Task<ImportCommitResult> CommitDrilledImportAsync(CancellationToken ct = default) => CommitOverride is { } r ? Task.FromResult(r) : inner.CommitDrilledImportAsync(ct);
    }
    private sealed class StorageStub : IStorageService
    {
        public ShopSettings Settings { get; private set; } = new("合成店家", "合成聯絡方式", 30, 0, 5);
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<DashboardSummary> GetDashboardAsync(CancellationToken ct = default) => Task.FromResult(new DashboardSummary(1, 2, 0));
        public Task<IReadOnlyList<CustomerRow>> SearchCustomersAsync(string query, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CustomerRow>>([new(1, "SYN-1", "合成客戶", "合成電話", null)]);
        public Task<IReadOnlyList<VehicleRow>> GetVehiclesAsync(long id, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<VehicleRow>>([new(1, 1, "TEST-1234", "合成車型", 82450, null, null, null), new(2, 1, "OTHER-01", null, null, null, null, null)]);
        public Task<IReadOnlyList<ReminderRow>> GetRemindersAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ReminderRow>>([]);
        public Task<ShopSettings> GetShopSettingsAsync(CancellationToken ct = default) => Task.FromResult(Settings);
        public Task SaveShopSettingsAsync(ShopSettings value, CancellationToken ct = default) { Settings = value; return Task.CompletedTask; }
        public Task<IReadOnlyList<CatalogRow>> GetCatalogAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CatalogRow>>([]);
        public Task<long> SaveCustomerAsync(CustomerEditor value, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<long> SaveVehicleAsync(VehicleEditor value, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<long> SaveCatalogAsync(CatalogEditor value, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> CreateBackupAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ImportPreview> PreviewImportAsync(IReadOnlyList<string> value, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ImportResult> CommitImportAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<long> CreateCustomerWithVehicleAsync(CustomerDraft customer, VehicleDraft vehicle, CancellationToken ct = default) => throw new NotSupportedException();
    }
}

using ExpertCar.App.Presentation;
using ExpertCar.Core;
using ExpertCar.Import;

namespace ExpertCar.Verification;

/// <summary>以 App 相同組合執行完整畫面命令；只接受合成店家，目標必須是新資料夾。</summary>
public static class V02Workflow
{
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("驗證失敗：" + message); }
    private static async Task Execute(WorkspaceV02ViewModel vm, AsyncCommand command)
    {
        Require(command.CanExecute(null), "命令不可執行");
        await command.ExecuteAsync();
        Require(!vm.Message.Contains("未完成") && !vm.Message.Contains("失敗"), vm.Message);
    }
    public static async Task RunAsync(string source, string target, Action<string> log)
    {
        Require(!Directory.Exists(target), "目標資料夾已存在，避免覆寫");
        Directory.CreateDirectory(target);
        var path = Path.Combine(target, "shop.db");
        using var service = await ExpertCarServices.OpenAsync(path);
        var prefs = new JsonAppPreferencesStore(Path.Combine(target, "ui-preferences-v02.json"));
        var vm = new WorkspaceV02ViewModel(service, service, prefs);
        await vm.InitializeAsync();
        vm.ImportFolder = Path.GetFullPath(source);
        await Execute(vm, vm.DrillImportCommand);
        Require(!vm.CommitImportCommand.CanExecute(null), "尚未人工確認不可提交");
        vm.ImportConfirmed = true;
        Require(vm.CommitImportCommand.CanExecute(null), "演練應有可新增資料");
        Require(vm.ImportProblems.Count > 0, "待確認清單");
        log(vm.ImportSummary);
        foreach (var count in vm.ImportSources) log($"source {count.SourceFile}/{count.SourceTable} scanned={count.Scanned} ready={count.Imported} skipped={count.Skipped} pending={count.Pending} failed={count.Failed}");
        await Execute(vm, vm.CommitImportCommand);
        Require(Directory.GetFiles(Path.Combine(target, "backups"), "*.db").Length > 0, "提交前備份");
        log(vm.ImportSummary.Split('；')[0] + "; backup_exists=yes");
        await Execute(vm, vm.DrillImportCommand);
        Require(vm.ImportSummary.Contains("成功 0") && !vm.CommitImportCommand.CanExecute(null), "重跑零新增");
        log("replay " + vm.ImportSummary);

        vm.Query = "T00-0001";
        await Execute(vm, vm.SearchCommand);
        vm.SelectedVehicle = vm.Vehicles.Single();
        await Execute(vm, vm.OpenVehicleCommand);
        Require(vm.HistoryOrders.Count > 0, "歷史工單");
        log($"quick_find count={vm.Vehicles.Count} history_orders={vm.HistoryOrders.Count}");
        var vehicleId = vm.SelectedVehicle.VehicleId;
        var oldOwner = vm.SelectedVehicle.CustomerId;
        vm.VehicleVin = "12345678901234567"; vm.VehicleMake = "合成廠牌";
        vm.VehicleModel = "合成車型"; vm.VehicleYear = "2022"; vm.VehicleColor = "白";
        await Execute(vm, vm.SaveVehicleCommand);
        Require((await service.GetVehicleHistoryAsync(vehicleId))!.Vehicle.Vin == vm.VehicleVin, "車籍寫入");
        vm.ShopName = "合成示範汽修店"; vm.HourlyRate = 1000; vm.DocumentHeader = "合成示範單據";
        vm.ShopAddress = "合成示範地址（非真實地址）"; vm.ShopTaxId = "00000000";
        vm.CardSurchargeEnabled = true; vm.CardPercent = 3; vm.TaxPercent = 5;
        await Execute(vm, vm.SaveSettingsCommand);
        await Execute(vm, vm.HistoryNewOrderCommand);
        vm.Number = "DEMO-250D-RC1";
        vm.Diagnosis = "合成測試診斷"; vm.RepairDescription = "合成測試維修";
        vm.PromisedDeliveryOn = DateOnly.FromDateTime(UiText.Now.DateTime).ToString("yyyy-MM-dd");
        vm.Lines.Add(new() { Category = WorkLineCategory.Labor, Name = "工時", Hourly = true, Minutes = 120, HourlyRate = vm.HourlyRate });
        vm.Lines.Add(new() { Category = WorkLineCategory.Consumable, Name = "耗材", Price = 300 });
        vm.Lines.Add(new() { Category = WorkLineCategory.Disposal, Name = "環保費", Price = 100 });
        vm.Lines.Add(new() { Category = WorkLineCategory.Outsource, Name = "外包校正", Vendor = "合成外包廠商", Price = 500 });
        vm.Lines.Add(new() { Category = WorkLineCategory.Part, Name = "例行零件", PartNumber = "DEMO-P1", Price = 400 });
        await Execute(vm, vm.SaveCommand);
        Require(vm.CurrentOrder is not null && vm.CurrentOrder.Draft.Lines.Count == 5, "估價儲存");
        var id = vm.CurrentOrder!.Id;
        await Execute(vm, vm.NextStatusCommand);
        Require(vm.CurrentOrder!.Status == WorkOrderStatus.Accepted, "轉工單");
        await Execute(vm, vm.AddAdditionCommand);
        vm.SelectedAddition!.Title = "同意加修"; vm.SelectedAddition.Method = "面談"; vm.SelectedAddition.Accepted = true;
        vm.Lines.Add(new() { Name = "同意零件", PartNumber = "DEMO-A1", Price = 200 });
        vm.SelectedLine = vm.Lines.Last(); await Execute(vm, vm.AssignAdditionCommand);
        await Execute(vm, vm.AddAdditionCommand); vm.SelectedAddition!.Title = "不同意加修";
        vm.Lines.Add(new() { Name = "不同意零件", PartNumber = "DEMO-N1", Price = 9000 });
        vm.SelectedLine = vm.Lines.Last(); await Execute(vm, vm.AssignAdditionCommand);
        await Execute(vm, vm.SaveCommand);
        await Execute(vm, vm.NextStatusCommand); await Execute(vm, vm.NextStatusCommand);
        Require(vm.CurrentOrder!.Status == WorkOrderStatus.AwaitingCheckout && vm.DeliveryOrders.Any(o => o.Id == id), "今日待交車");
        vm.IssueInvoice = true; vm.PaymentMethod = "刷卡";
        await Execute(vm, vm.QuoteCommand);
        var quote = vm.SelectedQuote!;
        Require(quote.BaseMinor == 350000 && quote.TaxMinor == 17500 && quote.CardSurchargeMinor == 11000 && quote.TotalMinor == 378500, "稅及刷卡計價");
        log($"checkout base_minor={quote.BaseMinor} tax_minor={quote.TaxMinor} card_minor={quote.CardSurchargeMinor} total_minor={quote.TotalMinor}");
        vm.PaymentAmount = "1000"; await Execute(vm, vm.PayCommand);
        Require((await service.GetReceivableAsync(id))!.DueMinor == 278500, "第一筆付款");
        log("payment1 paid_minor=100000 due_minor=278500");
        vm.PaymentAmount = "2785"; await Execute(vm, vm.PayCommand);
        var receivable = (await service.GetReceivableAsync(id))!;
        Require(receivable.PaidMinor == 378500 && receivable.DueMinor == 0 && vm.Payments.Count == 2, "第二筆付款");
        log($"payment2 paid_minor={receivable.PaidMinor} due_minor={receivable.DueMinor} payments={vm.Payments.Count}");
        await Execute(vm, vm.NextStatusCommand);
        var pageCounts = new List<int>();
        foreach (var kind in new[] { "Estimate", "WorkOrder", "Receipt" })
        {
            vm.PrintKind = kind; await Execute(vm, vm.PrintCommand);
            var model = vm.PrintModel!;
            Require(model.Layout.Orientation == PrintOrientation.Portrait && model.Checkout == quote, "列印方向及金額");
            var snapshot = await service.CreatePrintSnapshotAsync(id, kind);
            Require(snapshot.Model.Lines.Count == 6 && snapshot.Checkout == quote, "未同意加修不計價");
            pageCounts.Add(model.Pages.Count);
            log($"print {kind} A4=Portrait pages={model.Pages.Count} total_minor={model.Checkout.TotalMinor} approved_lines={snapshot.Model.Lines.Count}");
        }
        Require(pageCounts.Distinct().Count() == 1, "三種單據頁數一致");
        var nextOwner = await service.SaveCustomerAsync(new(null, "合成新車主", null, null));
        vm.TransferQuery = "合成新車主";
        await Execute(vm, vm.SearchTransferCustomersCommand);
        vm.SelectedTransferCustomer = vm.TransferCustomers.Single(c => c.Id == nextOwner);
        await Execute(vm, vm.TransferVehicleCommand);
        Require((await service.GetVehicleHistoryAsync(vehicleId))!.Vehicle.CustomerId == nextOwner, "過戶新車主");
        Require((await service.GetWorkOrderAsync(id))!.Draft.CustomerId == oldOwner, "舊單車主不變");
        Require((await service.CreatePrintSnapshotAsync(id, "Receipt")).Model.CustomerName != "合成新車主", "舊單列印車主不變");
        using var reopened = await ExpertCarServices.OpenAsync(path);
        var reload = new WorkspaceV02ViewModel(reopened, reopened, prefs);
        await reload.InitializeAsync();
        reload.SelectedHistoryOrder = reload.Orders.Single(o => o.Id == id);
        await Execute(reload, reload.OpenOrderCommand);
        Require(reload.Payments.Count == 2 && reload.DueText == UiText.Money(0), "重新開啟保留收款");
        Require(reload.Lines.Single(l => l.Category == WorkLineCategory.Outsource).Vendor == "合成外包廠商", "正式廠商欄位往返");
        Require(reload.HourlyRate == 1000 && reload.DocumentHeader == "合成示範單據", "設定存資料庫");
        log("ownership transferred=yes old_order_owner_unchanged=yes reopen_payments=2 receivable_minor=0");
        log("PASS App composition + ViewModel commands end-to-end");
    }
}

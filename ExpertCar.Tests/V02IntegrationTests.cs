using ExpertCar.App.Presentation;
using ExpertCar.Core;
using ExpertCar.Data;

namespace ExpertCar.Tests;

public sealed class V02IntegrationTests
{
    [Fact]
    public async Task SqliteContractsKeepOldOwnerAndExposeDeliveryReceivableCatalogAndVendor()
    {
        var dir = Path.Combine(Path.GetTempPath(), "expertcar-250d-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var service = new SqliteV02Service(Path.Combine(dir, "shop.db")); await service.InitializeAsync();
            var oldOwner = await service.CreateCustomerWithVehicleAsync(new("OLD", "舊車主"), new("TEST-250D", "TEST250D"));
            var vehicle = Assert.Single(await service.QuickFindVehiclesAsync("TEST-250D"));
            await service.UpdateVehicleDetailsAsync(new(vehicle.VehicleId, "12345678901234567", "Toyota", "測試車", 2022, "白"));
            Assert.Equal("Toyota", (await service.GetVehicleHistoryAsync(vehicle.VehicleId))!.Vehicle.Make);
            await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateVehicleDetailsAsync(new(vehicle.VehicleId, "BAD", null, null, null, null)));

            var item = await service.SaveCatalogAsync(new(null, "項目", "L-250D", "工資", "次", 10000));
            var part = await service.SaveCatalogAsync(new(null, "零件", "P-250D", "零件", "個", 20000));
            await service.SaveSuggestedPartIdsAsync(item, [part]);
            Assert.Equal([part], await service.GetSuggestedPartIdsAsync(item));

            var today = new DateOnly(2026, 11, 20);
            var draft = new WorkOrderDraft(null, "W-250D", oldOwner, vehicle.VehicleId, today, null, null, null,
                "簽名", "加修須同意", [new(WorkLineCategory.Outsource, "外包校正", null, null, "次", 1000, 10000,
                    VendorName: "合成外包廠商")], [], 0, today);
            var id = await service.SaveWorkOrderAsync(draft);
            Assert.Equal("合成外包廠商", Assert.Single((await service.GetWorkOrderAsync(id))!.Draft.Lines).VendorName);
            Assert.Equal(id, Assert.Single((await service.SearchWorkOrdersAsync()).Items).Order.Id);
            await service.TransitionWorkOrderAsync(id, WorkOrderStatus.Estimate, WorkOrderStatus.Accepted);
            await service.TransitionWorkOrderAsync(id, WorkOrderStatus.Accepted, WorkOrderStatus.InProgress);
            await service.TransitionWorkOrderAsync(id, WorkOrderStatus.InProgress, WorkOrderStatus.AwaitingCheckout);
            Assert.Single(await service.GetTodayDeliveryOrdersAsync(today));
            Assert.Empty(await service.GetTodayDeliveryOrdersAsync(today.AddDays(1)));
            var quote = await service.QuoteCheckoutAsync(id, new(false, false));
            Assert.Equal(quote.TotalMinor, (await service.GetReceivableAsync(id))!.DueMinor);
            var nextOwner = await service.SaveCustomerAsync(new(null, "新車主", null, null));
            await service.TransferVehicleAsync(vehicle.VehicleId, nextOwner, today.AddDays(1));
            Assert.Equal(nextOwner, (await service.GetVehicleHistoryAsync(vehicle.VehicleId))!.Vehicle.CustomerId);
            Assert.Equal(oldOwner, (await service.GetWorkOrderAsync(id))!.Draft.CustomerId);
            Assert.Equal("舊車主", (await service.CreatePrintSnapshotAsync(id, "Estimate")).Model.CustomerName);
            Assert.Equal(2, (await service.GetVehicleHistoryAsync(vehicle.VehicleId))!.Ownerships.Count);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task ViewModelCommandsUseRealSqliteAndPersistSettings()
    {
        var dir = Path.Combine(Path.GetTempPath(), "expertcar-vm-250d-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var service = new SqliteV02Service(Path.Combine(dir, "shop.db")); await service.InitializeAsync();
            await service.CreateCustomerWithVehicleAsync(new("C1", "合成車主"), new("VM-250D", "VM250D"));
            var vm = new WorkspaceV02ViewModel(service, service, new JsonAppPreferencesStore(Path.Combine(dir, "ui-preferences-v02.json")));
            await vm.InitializeAsync(); vm.Query = "VM-250D"; await vm.SearchCommand.ExecuteAsync();
            vm.SelectedVehicle = Assert.Single(vm.Vehicles); await vm.OpenVehicleCommand.ExecuteAsync();
            vm.VehicleMake = "Honda"; vm.VehicleVin = "12345678901234567"; await vm.SaveVehicleCommand.ExecuteAsync();
            Assert.Equal("Honda", (await service.GetVehicleHistoryAsync(vm.SelectedVehicle.VehicleId))!.Vehicle.Make);
            await vm.HistoryNewOrderCommand.ExecuteAsync(); vm.PromisedDeliveryOn = DateOnly.FromDateTime(UiText.Now.DateTime).ToString("yyyy-MM-dd");
            vm.Lines.Add(new() { Category = WorkLineCategory.Outsource, Name = "外包", Vendor = "合成廠商", Price = 100 });
            await vm.SaveCommand.ExecuteAsync(); Assert.NotNull(vm.CurrentOrder);
            for (var i = 0; i < 3; i++) await vm.NextStatusCommand.ExecuteAsync();
            Assert.Single(vm.DeliveryOrders);
            vm.HourlyRate = 1500; vm.DocumentHeader = "合成抬頭"; await vm.SaveSettingsCommand.ExecuteAsync();
            Assert.Equal(150000, (await service.GetShopSettingsAsync()).HourlyRateMinor);
            Assert.Equal("合成抬頭", (await service.GetShopSettingsAsync()).DocumentHeader);
        }
        finally { Directory.Delete(dir, true); }
    }
}

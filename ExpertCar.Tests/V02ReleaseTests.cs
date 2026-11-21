using System.IO.Compression;
using ExpertCar.App.Presentation;
using ExpertCar.Core;
using ExpertCar.Import;
using ExpertCar.Verification;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace ExpertCar.Tests;

public sealed class V02ReleaseTests(ITestOutputHelper output)
{
    [Fact]
    public async Task RealCompositionViewModelCommandsCoverImportThroughTransferAndReopen()
    {
        var root = Path.Combine(Path.GetTempPath(), "expertcar-release-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "synthetic");
        Directory.CreateDirectory(source);
        try
        {
            ZipFile.ExtractToDirectory(Path.Combine(AppContext.BaseDirectory, "Fixtures", "supercar-v02.zip"), source);
            await V02Workflow.RunAsync(source, Path.Combine(root, "target"), output.WriteLine);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task LegacyJsonMigratesOnceIncludingPartsAndDbSurvivesRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "expertcar-migrate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var service = await ExpertCarServices.OpenAsync(Path.Combine(root, "shop.db"));
            var item = await service.SaveCatalogAsync(new(null, "項目", "L", "工資", "次", 10000));
            var part = await service.SaveCatalogAsync(new(null, "零件", "P", "零件", "個", 20000));
            var prefs = new JsonAppPreferencesStore(Path.Combine(root, "ui-preferences-v02.json"));
            prefs.Save(new(150000, "舊抬頭", true, new() { [item] = [part] }, PrintOrientation.Landscape, "合成地址", "00000000"));
            await new WorkspaceV02ViewModel(service, service, prefs).InitializeAsync();
            var settings = await service.GetShopSettingsAsync();
            Assert.Equal(150000, settings.HourlyRateMinor); Assert.Equal("舊抬頭", settings.DocumentHeader);
            Assert.Equal("合成地址", settings.Address); Assert.Equal("00000000", settings.TaxId);
            Assert.Equal([part], await service.GetSuggestedPartIdsAsync(item));
            Assert.Null(prefs.Load().CatalogParts); Assert.Equal("", prefs.Load().DocumentHeader);
            Assert.Equal(PrintOrientation.Landscape, prefs.Load().PrintOrientation); Assert.True(prefs.Load().TourSeen);
            await service.SaveShopSettingsAsync(settings with { HourlyRateMinor = 120000, DocumentHeader = "" });
            await service.SaveSuggestedPartIdsAsync(item, []);
            await new WorkspaceV02ViewModel(service, service, prefs).InitializeAsync();
            Assert.Equal(120000, (await service.GetShopSettingsAsync()).HourlyRateMinor);
            Assert.Empty(await service.GetSuggestedPartIdsAsync(item));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task GlobalOrderPagingReachesVehiclesBeyondQuickFindLimit()
    {
        var root = Path.Combine(Path.GetTempPath(), "expertcar-pages-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var service = await ExpertCarServices.OpenAsync(Path.Combine(root, "shop.db"));
            var customer = await service.CreateCustomerWithVehicleAsync(new("C", "合成車主"), new("PAGE-001", "PAGE001"));
            var vehicle = Assert.Single(await service.QuickFindVehiclesAsync("PAGE-001"));
            for (var i = 0; i < 103; i++)
                await service.SaveWorkOrderAsync(new(null, "PAGE-" + i, customer, vehicle.VehicleId, new(2026, 11, 20), null, null, null, null, "加修須同意", [], []));
            var vm = new WorkspaceV02ViewModel(service, service, new JsonAppPreferencesStore(Path.Combine(root, "prefs.json")));
            await vm.InitializeAsync(); Assert.Equal(100, vm.Orders.Count);
            var firstIds = vm.Orders.Select(o => o.Id).ToHashSet();
            await vm.NextOrdersCommand.ExecuteAsync(); Assert.Equal(3, vm.Orders.Count);
            Assert.DoesNotContain(vm.Orders, o => firstIds.Contains(o.Id)); Assert.False(vm.NextOrdersCommand.CanExecute(null));
            await vm.PreviousOrdersCommand.ExecuteAsync(); Assert.Equal(100, vm.Orders.Count);
            Assert.False(vm.PreviousOrdersCommand.CanExecute(null));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SqliteDefaultCatalogAndDirectSuggestedPartsAreVisible()
    {
        var root = Path.Combine(Path.GetTempPath(), "expertcar-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var service = await ExpertCarServices.OpenAsync(Path.Combine(root, "shop.db"));
            var item = await service.SaveCatalogAsync(new(null, "項目", "LAB-1", "合成工資", "次", 10500));
            var part = await service.SaveCatalogAsync(new(null, "零件", "P-1", "合成零件", "個", 20000));
            await service.SaveSuggestedPartIdsAsync(item, [part]);
            var vm = new WorkspaceV02ViewModel(service, service, new JsonAppPreferencesStore(Path.Combine(root, "prefs.json")));
            await vm.InitializeAsync();
            Assert.Equal("全部", vm.CatalogKind);
            Assert.Equal(2, vm.Catalog.Count);
            Assert.Contains(vm.Catalog, p => p.Kind == "項目");
            Assert.Contains(vm.Catalog, p => p.Kind == "零件");
            Assert.Equal("項目", vm.Catalog[0].Kind);
            vm.SelectedCatalogItem = vm.Catalog.Single(p => p.Kind == "項目");
            await vm.SuggestPartsAsync();
            Assert.Equal(part, Assert.Single(vm.RecommendedParts).Id);
            Assert.Single(vm.SuggestedParts);
            Assert.Equal(2, (await service.SearchCatalogAsync(new("", "", 1, 30))).TotalCount);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ViewModelBackupCanOpenAndClearRequiresBothConfirmations()
    {
        var root = Path.Combine(Path.GetTempPath(), "expertcar-reset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var service = await ExpertCarServices.OpenAsync(Path.Combine(root, "shop.db"));
            await service.CreateCustomerWithVehicleAsync(new("TEST", "合成車主"), new("TEST-1", "TEST1"));
            await service.SaveCatalogAsync(new(null, "零件", "PART-1", "合成零件", "個", 10000));
            var vm = new WorkspaceV02ViewModel(service, service, new JsonAppPreferencesStore(Path.Combine(root, "prefs.json")));
            await vm.InitializeAsync();
            var chosen = Path.Combine(root, "chosen");
            await vm.BackupToAsync(chosen);
            var backup = Assert.Single(Directory.GetFiles(chosen, "*.db"));
            using (var db = new SqliteConnection($"Data Source={backup}"))
            {
                db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT count(*) FROM customers";
                Assert.Equal(1L, cmd.ExecuteScalar());
            }
            await vm.ClearAllDataAsync(() => false, () => throw new Exception("second prompt must not open"));
            Assert.Equal(1, (await service.GetDashboardAsync()).CustomerCount);
            await vm.ClearAllDataAsync(() => true, () => "取消");
            Assert.Equal(1, (await service.GetDashboardAsync()).CustomerCount);
            await vm.ClearAllDataAsync(() => true, () => "清空");
            Assert.Equal(0, (await service.GetDashboardAsync()).CustomerCount);
            Assert.Equal(0, (await service.SearchCatalogAsync(new("", null))).TotalCount);
            Assert.Equal(3, Directory.GetFiles(Path.Combine(root, "backups"), "*.db").Length);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ClearThenNewVehicleCanOpenFreshOrderWithoutRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "expertcar-clear-order-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var service = await ExpertCarServices.OpenAsync(Path.Combine(root, "shop.db"));
            await service.CreateCustomerWithVehicleAsync(new("OLD", "試用車主"), new("OLD-1", "OLD1"));
            var vm = new WorkspaceV02ViewModel(service, service, new JsonAppPreferencesStore(Path.Combine(root, "prefs.json")));
            await vm.InitializeAsync();
            vm.Query = "OLD1"; await vm.SearchAsync(); vm.SelectedVehicle = Assert.Single(vm.Vehicles);
            await vm.OpenVehicleAsync(); await vm.NewOrderAsync(); vm.Lines.Add(new() { Name = "試用工資" });
            await vm.ClearAllDataAsync(() => true, () => "清空");
            Assert.Empty(vm.Lines); Assert.Empty(vm.Additions); Assert.Empty(vm.HistoryOrders);
            Assert.Equal("請先搜尋並選取車輛", vm.VehicleInfo);
            Assert.Equal("", vm.DraftVehicle.Trim(' ', '／'));
            Assert.Equal("尚未演練", vm.ImportSummary);
            Assert.Equal(0, vm.SelectedTab);
            Assert.False(vm.Editable);
            await service.CreateCustomerWithVehicleAsync(new("NEW", "正式車主"), new("NEW-1", "NEW1"));
            vm.Query = "NEW1"; await vm.SearchAsync(); vm.SelectedVehicle = Assert.Single(vm.Vehicles);
            await vm.NewOrderAsync();
            Assert.Contains("NEW-1", vm.DraftVehicle);
            Assert.True(vm.Editable);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SuggestedPartsCanBeAddedAcrossSearchesAndEmptySelectionCannotEraseThem()
    {
        var root = Path.Combine(Path.GetTempPath(), "expertcar-suggest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var service = await ExpertCarServices.OpenAsync(Path.Combine(root, "shop.db"));
            var item = await service.SaveCatalogAsync(new(null, "項目", "LAB", "保養", "次", 10000));
            var oil = await service.SaveCatalogAsync(new(null, "零件", "OIL", "機油", "顆", 10000));
            var filter = await service.SaveCatalogAsync(new(null, "零件", "FILTER", "機油芯", "顆", 10000));
            var vm = new WorkspaceV02ViewModel(service, service, new JsonAppPreferencesStore(Path.Combine(root, "prefs.json")));
            await vm.InitializeAsync(); vm.SelectedCatalogItem = vm.Catalog.Single(c => c.Id == item && c.Kind == "項目");
            await vm.SuggestPartsAsync();
            await vm.SaveSuggestedPartsAsync([oil]);
            vm.PartsQuery = "機油芯"; await vm.SearchPartsAsync(true);
            await vm.SaveSuggestedPartsAsync([filter]);
            Assert.Equal(2, vm.RecommendedParts.Count);
            await Assert.ThrowsAsync<ArgumentException>(() => vm.SaveSuggestedPartsAsync([]));
            Assert.Equal(2, (await service.GetSuggestedPartsAsync(item)).Count);
            await vm.RemoveSuggestedPartAsync(oil);
            Assert.Equal(filter, Assert.Single(vm.RecommendedParts).Id);
        }
        finally { Directory.Delete(root, true); }
    }
}

using System.IO.Compression;
using ExpertCar.App.Presentation;
using ExpertCar.Core;
using ExpertCar.Import;
using ExpertCar.Verification;
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
}

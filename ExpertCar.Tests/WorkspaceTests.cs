using ExpertCar.Core;
using ExpertCar.Data;

namespace ExpertCar.Tests;

public class WorkspaceTests
{
    private static (SqliteStorageService Storage, string Directory) CreateStore()
    {
        var directory = Path.Combine(Path.GetTempPath(), "expertcar-workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var storage = new SqliteStorageService(Path.Combine(directory, "test.db"));
        storage.InitializeAsync().GetAwaiter().GetResult();
        return (storage, directory);
    }

    [Fact]
    public async Task SearchByNamePhoneAndPlate_AndOneCustomerManyVehicles()
    {
        var (store, directory) = CreateStore();
        try
        {
            var customerId = await store.SaveCustomerAsync(new(null, "王小明", "0912-345-678", "台北市"));
            await store.SaveVehicleAsync(new(null, customerId, "ABC-1234", "甲車", null, null, null, null));
            await store.SaveVehicleAsync(new(null, customerId, "XYZ-5678", "乙車", null, null, null, null));
            var vm = new WorkspaceViewModel(store);
            Assert.Single(await vm.SearchAsync("王小明"));
            Assert.Single(await vm.SearchAsync("0912"));
            Assert.Single(await vm.SearchAsync("XYZ-5678"));
            Assert.Empty(await vm.SearchAsync("不存在"));
            Assert.Equal(2, (await store.GetVehiclesAsync(customerId)).Count);
            await store.SaveCustomerAsync(new(customerId, "王先生", "02-12345678", "新北市"));
            Assert.Single(await vm.SearchAsync("02-1234"));
            Assert.Empty(await vm.SearchAsync("0912"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ReminderGroups_CrossMonthLeapDayMissingDateAndZeroMileage()
    {
        var (store, directory) = CreateStore();
        try
        {
            var id = await store.SaveCustomerAsync(new(null, "陳先生", null, null));
            await store.SaveVehicleAsync(new(null, id, "A-1", null, null, new DateOnly(2024, 2, 28), null, null));
            await store.SaveVehicleAsync(new(null, id, "A-2", null, null, new DateOnly(2024, 2, 29), null, null));
            await store.SaveVehicleAsync(new(null, id, "A-3", null, null, new DateOnly(2024, 3, 29), null, null));
            await store.SaveVehicleAsync(new(null, id, "A-4", null, 1000, null, 1000, null));
            await store.SaveVehicleAsync(new(null, id, "A-5", null, 0, null, 0, null));
            await store.SaveVehicleAsync(new(null, id, "A-6", null, null, new DateOnly(2024, 4, 1), null, null));
            var groups = await new WorkspaceViewModel(store).GetReminderGroupsAsync(new DateOnly(2024, 2, 29), 30);
            Assert.Contains(groups["逾期"], r => r.Plate == "A-1");
            Assert.Contains(groups["今日"], r => r.Plate == "A-2");
            Assert.Contains(groups["期間內"], r => r.Plate == "A-3");
            Assert.Contains(groups["缺日期"], r => r.Plate == "A-4" && ReminderGrouping.MileageDue(r));
            Assert.Contains(groups["缺日期"], r => r.Plate == "A-5" && !ReminderGrouping.MileageDue(r));
            Assert.Contains(groups["期間外"], r => r.Plate == "A-6");
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ExistingVersionTwoCatalogGainsDefaultUnit()
    {
        var directory = Path.Combine(Path.GetTempPath(), "expertcar-migrate-unit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new SqliteStorageService(Path.Combine(directory, "test.db"));
            store.MigrateTo(2);
            using (var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(directory, "test.db")}"))
            {
                db.Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = "INSERT INTO service_items(code,name) VALUES ('S01','換油')";
                cmd.ExecuteNonQuery();
            }
            await store.InitializeAsync();
            Assert.Contains(await store.GetCatalogAsync(), item => item.Code == "S01" && item.Unit == "次");
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task SettingsAndCatalogPersist_AndBackupExists()
    {
        var (store, directory) = CreateStore();
        try
        {
            var vm = new WorkspaceViewModel(store);
            Assert.Equal(0m, (await vm.LoadSettingsAsync()).CardFeePercent);
            Assert.Equal(5m, (await vm.LoadSettingsAsync()).TaxPercent);
            await vm.SaveSettingsAsync(new("汽修店", "02-12345678", 45, 2.5m, 5m));
            var reopened = new SqliteStorageService(Path.Combine(directory, "test.db"));
            Assert.Equal(45, (await reopened.GetShopSettingsAsync()).ReminderDays);
            Assert.Equal(2.5m, (await reopened.GetShopSettingsAsync()).CardFeePercent);
            var catalogId = await store.SaveCatalogAsync(new(null, "零件", "P01", "機油", "瓶", 25000));
            await store.SaveCatalogAsync(new(catalogId, "零件", "P01", "機油", "罐", 26000));
            Assert.Contains(await reopened.GetCatalogAsync(), item => item.Unit == "罐" && item.PriceMinor == 26000);
            var serviceId = await store.SaveCatalogAsync(new(null, "項目", "S01", "換機油", "次", 50000));
            await store.SaveCatalogAsync(new(serviceId, "項目", "S01", "換機油", "回", 55000));
            Assert.Contains(await reopened.GetCatalogAsync(), item => item.Kind == "項目" && item.Unit == "回" && item.PriceMinor == 55000);
            Assert.True(File.Exists(await store.CreateBackupAsync()));
        }
        finally { Directory.Delete(directory, true); }
    }
}

public class ImportStorageServiceTests
{
    [Fact]
    public async Task DbfPreviewBackupCommitAndRepeatThroughStorageContract()
    {
        var directory = Path.Combine(Path.GetTempPath(), "expertcar-app-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var paths = new[] { "CUSTNAME.DBF", "CUSTPHON.DBF" };
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "synthetic-custname.dbf"), Path.Combine(directory, paths[0]));
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "synthetic-custphon.dbf"), Path.Combine(directory, paths[1]));
            using var storage = new ExpertCar.Import.ImportStorageService(Path.Combine(directory, "data.db"));
            await storage.InitializeAsync();
            var preview = await storage.PreviewImportAsync(paths.Select(p => Path.Combine(directory, p)).ToArray());
            Assert.Equal(2, preview.Success);
            Assert.Equal(2, preview.Pending);
            Assert.True(File.Exists(await storage.CreateBackupAsync()));
            var receipt = await storage.CommitImportAsync();
            Assert.Equal(2, receipt.Success);
            Assert.True(File.Exists(receipt.BackupPath));
            Assert.Equal(2, (await storage.SearchCustomersAsync("")).Count);
            var repeat = await storage.PreviewImportAsync(paths.Select(p => Path.Combine(directory, p)).ToArray());
            Assert.True(repeat.AlreadyImported);
            Assert.Equal(4, repeat.Skipped + repeat.Pending);
        }
        finally { Directory.Delete(directory, true); }
    }
}

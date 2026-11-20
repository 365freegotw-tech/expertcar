using ExpertCar.Data;
using ExpertCar.Import;
using Microsoft.Data.Sqlite;

namespace ExpertCar.Tests;

[Collection("MDBTools PATH isolation")]
public sealed class ImportEngineTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Theory]
    [InlineData("113/05/01", "2024-05-01")]
    [InlineData("1130501", "2024-05-01")]
    [InlineData("2024-05-01", "2024-05-01")]
    [InlineData("20240501", "2024-05-01")]
    [InlineData("", null)]
    [InlineData("0", null)]
    [InlineData("113/02/30", null)]
    public void Dates_accept_roc_western_and_blank_but_reject_invalid(string raw, string? expected) =>
        Assert.Equal(expected, ImportEngine.ParseDate(raw));

    [MdbToolsFact]
    public async Task Preview_commit_and_repeat_are_counted_without_merging_names()
    {
        using var temp = new TempFiles();
        var paths = temp.CopySources();
        var engine = new ImportEngine();
        await new SqliteStorageService(temp.Db).InitializeAsync();
        using var plan = await engine.PreviewAsync(paths, temp.Db);
        Assert.Equal(new ImportCounts(5, 0, 3, 0), plan.Counts);
        Assert.Equal(2, plan.IssueCounts["DuplicatePlate"]);
        Assert.Equal(1, plan.IssueCounts["InvalidDate:maintenanceDate"]);
        Assert.Equal(1, plan.IssueCounts["UnlinkedPhone"]);
        Assert.DoesNotContain(plan.IssueCounts.Keys, code => code.StartsWith("InvalidNumber:", StringComparison.Ordinal));
        Assert.DoesNotContain("InvalidDate:inspectionDate", plan.IssueCounts.Keys);
        Assert.Equal(0, temp.Count("customers"));
        Assert.Equal(0, temp.Count("import_runs"));
        var receipt = await engine.CommitAsync(plan);
        Assert.Equal(5, receipt.Counts.Success);
        Assert.True(File.Exists(receipt.BackupPath));
        Assert.Equal(4, temp.Count("customers"));
        Assert.Equal(2, temp.Count("vehicles"));
        Assert.Equal(0, temp.CountWhere("vehicles", "odometer_km IS NOT NULL"));
        Assert.Equal(1, temp.CountWhere("vehicles", "plate='TT-9012' AND model_year IS NULL"));
        Assert.Equal(1, temp.Count("customer_contacts"));
        Assert.Equal(2, temp.Count("reminders"));
        Assert.Equal(0, temp.CountWhere("reminders", "due_km IS NOT NULL OR interval_days IS NOT NULL"));
        Assert.Equal(8, temp.Count("import_records"));
        Assert.Equal(4, temp.Count("import_issues"));
        Assert.Equal(2, temp.CountWhere("customers", "name='虛構甲'"));
        Assert.Equal(1, temp.CountWhere("reminders", "due_date='2024-05-01'"));
        Assert.Equal(0, temp.CountWhere("reminders", "kind='Insurance'"));
        using var repeat = await engine.PreviewAsync(paths, temp.Db);
        Assert.Equal(new ImportCounts(0, 8, 0, 0), repeat.Counts);
        await engine.CommitAsync(repeat);
        Assert.Equal(1, temp.Count("import_runs"));
        Assert.Equal(4, temp.Count("customers"));
        var changedRule = Path.Combine(temp.Directory, "rule-v2.json");
        var originalRule = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Rules", "customer-vehicle-v1.json"));
        await File.WriteAllTextAsync(changedRule, originalRule.Replace("customer-vehicle-v1", "customer-vehicle-v2", StringComparison.Ordinal));
        var changedEngine = new ImportEngine(changedRule);
        using var changedPreview = await changedEngine.PreviewAsync(paths, temp.Db);
        Assert.Equal(new ImportCounts(0, 8, 0, 0), changedPreview.Counts);
        await changedEngine.CommitAsync(changedPreview);
        Assert.Equal(1, temp.Count("import_runs"));
    }

    [MdbToolsFact]
    public async Task Database_error_rolls_back_entire_batch()
    {
        using var temp = new TempFiles();
        var paths = temp.CopySources();
        await new SqliteStorageService(temp.Db).InitializeAsync();
        temp.Execute("CREATE TRIGGER reject_import BEFORE INSERT ON customers WHEN NEW.name='虛構丁' BEGIN SELECT RAISE(ABORT,'synthetic failure'); END");
        using var plan = await new ImportEngine().PreviewAsync(paths, temp.Db);
        await Assert.ThrowsAsync<SqliteException>(() => new ImportEngine().CommitAsync(plan));
        Assert.Equal(0, temp.Count("customers"));
        Assert.Equal(0, temp.Count("vehicles"));
        Assert.Equal(0, temp.Count("import_runs"));
        Assert.Equal(0, temp.Count("import_records"));
    }

    [Fact]
    public async Task Preview_does_not_create_target_database()
    {
        using var temp = new TempFiles();
        var paths = temp.CopySources(includeMdb: false);
        using var plan = await new ImportEngine().PreviewAsync(paths, temp.Db);
        Assert.Equal(new ImportCounts(2, 0, 2, 0), plan.Counts);
        Assert.False(File.Exists(temp.Db));
    }

    [Fact]
    public async Task Dbf_only_preview_cancellation_and_missing_column_are_safe()
    {
        using var temp = new TempFiles();
        var paths = temp.CopySources(includeMdb: false);
        await new SqliteStorageService(temp.Db).InitializeAsync();
        var engine = new ImportEngine();
        using var plan = await engine.PreviewAsync(paths, temp.Db);
        Assert.Equal(new ImportCounts(2, 0, 2, 0), plan.Counts);
        Assert.Equal(0, temp.Count("import_runs"));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.CommitAsync(plan, cancel.Token));
        Assert.Equal(0, temp.Count("customers"));
        await File.AppendAllTextAsync(paths[0], "changed");
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.CommitAsync(plan));
        Assert.Equal(0, temp.Count("import_runs"));
        var rule = Path.Combine(temp.Directory, "missing.json");
        await File.WriteAllTextAsync(rule, """
            {"version":"missing-test","profile":"customer-vehicle","sources":[{"file":"CUSTNAME.DBF","table":"CUSTNAME","kind":"customer","required":["MISSING"],"fields":{"name":"CUSTNAME"}}]}
            """);
        using var missing = await new ImportEngine(rule).PreviewAsync([paths[0]], temp.Db);
        Assert.Equal(new ImportCounts(0, 0, 0, 2), missing.Counts);
        Assert.Equal(2, missing.IssueCounts["MissingColumn"]);
        Assert.Equal(0, temp.Count("import_runs"));
    }

    [Fact]
    public async Task Dbf_phone_uses_unique_existing_plate_without_name_matching()
    {
        using var temp = new TempFiles();
        var paths = temp.CopySources(includeMdb: false);
        var storage = new SqliteStorageService(temp.Db);
        await storage.InitializeAsync();
        await storage.CreateCustomerWithVehicleAsync(new("X1", "既有虛構車主", null), new("ZZ-5678", "ZZ5678", null));
        var engine = new ImportEngine();
        using var plan = await engine.PreviewAsync(paths, temp.Db);
        Assert.Equal(new ImportCounts(3, 0, 1, 0), plan.Counts);
        await engine.CommitAsync(plan);
        Assert.Equal(3, temp.Count("customers"));
        Assert.Equal(1, temp.Count("customer_contacts"));
        Assert.Equal(1, temp.CountWhere("customer_contacts", "customer_id=(SELECT id FROM customers WHERE code='X1')"));
    }

    private sealed class TempFiles : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "expertcar-import-" + Guid.NewGuid().ToString("N"));
        public string Db => Path.Combine(Directory, "data.db");
        public TempFiles() => System.IO.Directory.CreateDirectory(Directory);
        public string[] CopySources(bool includeMdb = true)
        {
            var files = new List<(string Source, string Target)>();
            if (includeMdb) files.Add(("synthetic-customers.mdb", "Base.mdb"));
            files.Add(("synthetic-custname.dbf", "CUSTNAME.DBF"));
            files.Add(("synthetic-custphon.dbf", "CUSTPHON.DBF"));
            foreach (var (source, target) in files) File.Copy(Fixture(source), Path.Combine(Directory, target));
            return files.Select(f => Path.Combine(Directory, f.Target)).ToArray();
        }
        public int Count(string table) => CountWhere(table, "1=1");
        public int CountWhere(string table, string where)
        {
            using var db = new SqliteConnection($"Data Source={Db}"); db.Open();
            using var command = db.CreateCommand(); command.CommandText = $"SELECT count(*) FROM {table} WHERE {where}";
            return Convert.ToInt32(command.ExecuteScalar());
        }
        public void Execute(string sql)
        {
            using var db = new SqliteConnection($"Data Source={Db}"); db.Open();
            using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        }
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }
}

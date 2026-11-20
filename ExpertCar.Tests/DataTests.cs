using ExpertCar.Core;
using ExpertCar.Data;
using Microsoft.Data.Sqlite;

namespace ExpertCar.Tests;

public sealed class DataTests
{
    [Fact]
    public async Task CreatesAllTablesAndCanUpgradeExistingVersion()
    {
        using var fixture = new DatabaseFixture();
        fixture.Storage.MigrateTo(1);
        Assert.Equal(1, fixture.Scalar("SELECT max(version) FROM schema_migrations"));
        fixture.Execute("INSERT INTO customers(code,name) VALUES ('C001','王○明')");
        await fixture.Storage.InitializeAsync();
        Assert.Equal(3, fixture.Scalar("SELECT max(version) FROM schema_migrations"));
        Assert.Equal(1, fixture.Scalar("SELECT count(*) FROM customers"));
        var backup = Assert.Single(Directory.GetFiles(Path.Combine(fixture.Directory, "backups"), "*.db"));
        using (var saved = new SqliteConnection($"Data Source={backup};Mode=ReadOnly"))
        {
            saved.Open();
            using var command = saved.CreateCommand();
            command.CommandText = "SELECT count(*) FROM customers";
            Assert.Equal(1, (long)command.ExecuteScalar()!);
        }
        var tables = fixture.Names("SELECT name FROM sqlite_master WHERE type='table'");
        foreach (var name in new[] { "customers", "customer_contacts", "vehicles", "service_items", "parts",
            "service_parts", "work_orders", "work_order_lines", "payments", "insurers", "insurance_policies",
            "insurance_policy_items", "reminders", "print_snapshots", "work_order_events", "import_runs",
            "import_records", "import_issues", "app_settings", "schema_migrations" })
            Assert.Contains(name, tables);
        await fixture.Storage.InitializeAsync();
        Assert.Equal(3, fixture.Scalar("SELECT count(*) FROM schema_migrations"));
    }

    [Fact]
    public async Task ForeignAndUniqueConstraintsAreActive()
    {
        using var fixture = new DatabaseFixture();
        await fixture.Storage.InitializeAsync();
        Assert.Equal(1, fixture.Scalar("PRAGMA foreign_keys"));
        Assert.Throws<SqliteException>(() => fixture.Execute(
            "INSERT INTO vehicles(current_customer_id,plate,plate_key) VALUES (999,'TEST-01','TEST01')"));
        fixture.Execute("INSERT INTO customers(code,name) VALUES ('C001','王○明')");
        Assert.Throws<SqliteException>(() => fixture.Execute(
            "INSERT INTO customers(code,name) VALUES ('C001','陳○華')"));
        fixture.Execute("INSERT INTO vehicles(current_customer_id,plate,plate_key) VALUES (1,'TEST-01','TEST01')");
        fixture.Execute("INSERT INTO vehicles(current_customer_id,plate,plate_key) VALUES (1,'TEST 01','TEST01')");
        Assert.Equal(2, fixture.Scalar("SELECT count(*) FROM vehicles WHERE plate_key='TEST01'"));
    }

    [Fact]
    public async Task FailureRollsBackCustomerAndVehicleTransaction()
    {
        using var fixture = new DatabaseFixture();
        await fixture.Storage.InitializeAsync();
        fixture.Execute("CREATE TRIGGER reject_vehicle BEFORE INSERT ON vehicles BEGIN SELECT RAISE(ABORT, 'fixture failure'); END");
        await Assert.ThrowsAsync<SqliteException>(() => fixture.Storage.CreateCustomerWithVehicleAsync(
            new CustomerDraft("C001", "王○明"), new VehicleDraft("TEST-01", "TEST01")));
        Assert.Equal(0, fixture.Scalar("SELECT count(*) FROM customers"));
        Assert.Equal(0, fixture.Scalar("SELECT count(*) FROM vehicles"));
    }

    [Fact]
    public async Task StorageServiceCreatesLinkedRecords()
    {
        using var fixture = new DatabaseFixture();
        IStorageService storage = fixture.Storage;
        await storage.InitializeAsync();
        var id = await storage.CreateCustomerWithVehicleAsync(
            new CustomerDraft("C001", "王○明"), new VehicleDraft("TEST-01", "TEST01"));
        Assert.Equal(1, id);
        Assert.Equal(new DashboardSummary(1, 1, 0), await storage.GetDashboardAsync());
    }

    private sealed class DatabaseFixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "ExpertCar.Tests", Guid.NewGuid().ToString("N"));
        public SqliteStorageService Storage { get; }
        private readonly string _databasePath;

        public DatabaseFixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
            _databasePath = Path.Combine(Directory, "test.db");
            Storage = new SqliteStorageService(_databasePath);
        }

        public long Scalar(string sql)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return (long)command.ExecuteScalar()!;
        }

        public void Execute(string sql)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        public HashSet<string> Names(string sql)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            var names = new HashSet<string>();
            while (reader.Read()) names.Add(reader.GetString(0));
            return names;
        }

        private SqliteConnection Open()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = _databasePath, ForeignKeys = true, Pooling = false }.ToString());
            connection.Open();
            return connection;
        }

        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }
}

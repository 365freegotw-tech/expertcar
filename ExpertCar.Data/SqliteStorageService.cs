using ExpertCar.Core;
using Microsoft.Data.Sqlite;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("ExpertCar.Tests")]

namespace ExpertCar.Data;

public partial class SqliteStorageService(string databasePath) : IStorageService
{
    private readonly string _databasePath = Path.GetFullPath(databasePath);

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        MigrateTo(Schema.CurrentVersion, cancellationToken);
        return Task.CompletedTask;
    }

    internal void MigrateTo(int targetVersion, CancellationToken cancellationToken = default)
    {
        if (targetVersion < 1 || targetVersion > Schema.CurrentVersion)
            throw new ArgumentOutOfRangeException(nameof(targetVersion));
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        using var connection = Open();
        var hasVersionTable = Scalar(connection,
            "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='schema_migrations'") != 0;
        var current = hasVersionTable ? Scalar(connection, "SELECT coalesce(max(version), 0) FROM schema_migrations") : 0;
        if (current > Schema.CurrentVersion)
            throw new InvalidOperationException("資料庫版本高於此程式，拒絕降級");
        if (current >= targetVersion) return;

        // 既有資料庫升級前以 SQLite 線上備份，避免複製 WAL 中的半套資料。
        if (current > 0) Backup(connection);
        for (var version = current + 1; version <= targetVersion; version++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var transaction = connection.BeginTransaction();
            if (version == 1)
                Execute(connection, transaction,
                    "CREATE TABLE schema_migrations (version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL)");
            Execute(connection, transaction, Schema.Migrations[version - 1]);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO schema_migrations(version, applied_at) VALUES ($version, $at)";
            command.Parameters.AddWithValue("$version", version);
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
            transaction.Commit();
        }
    }

    public Task<DashboardSummary> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open();
        var result = new DashboardSummary(
            Scalar(connection, "SELECT count(*) FROM customers"),
            Scalar(connection, "SELECT count(*) FROM vehicles"),
            Scalar(connection, "SELECT count(*) FROM work_orders WHERE status IN ('Estimate','InProgress','Completed','ImportReview')"));
        return Task.FromResult(result);
    }

    public Task<long> CreateCustomerWithVehicleAsync(CustomerDraft customer, VehicleDraft vehicle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(customer);
        ArgumentNullException.ThrowIfNull(vehicle);
        if (string.IsNullOrWhiteSpace(customer.Code) || string.IsNullOrWhiteSpace(customer.Name) ||
            string.IsNullOrWhiteSpace(vehicle.Plate) || string.IsNullOrWhiteSpace(vehicle.PlateKey))
            throw new ArgumentException("客戶代碼、名稱、車牌及正規化車牌不可空白");
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        long customerId;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO customers(code,name,address) VALUES ($code,$name,$address) RETURNING id";
            command.Parameters.AddWithValue("$code", customer.Code);
            command.Parameters.AddWithValue("$name", customer.Name);
            command.Parameters.AddWithValue("$address", (object?)customer.Address ?? DBNull.Value);
            customerId = (long)command.ExecuteScalar()!;
        }
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO vehicles(current_customer_id,plate,plate_key,model) VALUES ($customer,$plate,$key,$model)";
            command.Parameters.AddWithValue("$customer", customerId);
            command.Parameters.AddWithValue("$plate", vehicle.Plate);
            command.Parameters.AddWithValue("$key", vehicle.PlateKey);
            command.Parameters.AddWithValue("$model", (object?)vehicle.Model ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
        return Task.FromResult(customerId);
    }

    public virtual Task<ImportPreview> PreviewImportAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("此儲存服務未設定舊資料匯入功能");
    public virtual Task<ImportResult> CommitImportAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("此儲存服務未設定舊資料匯入功能");

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            ForeignKeys = true,
            Pooling = false,
            DefaultTimeout = 30
        }.ToString());
        connection.Open();
        return connection;
    }

    private void Backup(SqliteConnection source)
    {
        var backupDirectory = Path.Combine(Path.GetDirectoryName(_databasePath)!, "backups");
        Directory.CreateDirectory(backupDirectory);
        var backupPath = Path.Combine(backupDirectory,
            $"expertcar-before-v{Schema.CurrentVersion}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.db");
        using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = backupPath, Pooling = false
        }.ToString());
        destination.Open();
        source.BackupDatabase(destination);
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

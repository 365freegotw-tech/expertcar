using System.Globalization;
using System.Text.Json;
using ExpertCar.Core;
using Microsoft.Data.Sqlite;

namespace ExpertCar.Data;

public partial class SqliteStorageService
{
    public Task<string> CreateBackupAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = Open();
        var directory = Path.Combine(Path.GetDirectoryName(_databasePath)!, "backups");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"expertcar-manual-{DateTimeOffset.Now:yyyyMMddHHmmss}-{Guid.NewGuid():N}.db");
        using var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        destination.Open(); db.BackupDatabase(destination);
        return Task.FromResult(path);
    }

    public Task<IReadOnlyList<CustomerRow>> SearchCustomersAsync(string query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT c.id,c.code,c.name,c.address,
              (SELECT value FROM customer_contacts WHERE customer_id=c.id AND kind='Phone' ORDER BY is_primary DESC,id LIMIT 1)
            FROM customers c
            WHERE $q='' OR c.name LIKE $like ESCAPE '\' OR c.code LIKE $like ESCAPE '\' OR
              EXISTS(SELECT 1 FROM customer_contacts WHERE customer_id=c.id AND value LIKE $like ESCAPE '\') OR
              EXISTS(SELECT 1 FROM vehicles WHERE current_customer_id=c.id AND plate LIKE $like ESCAPE '\')
            ORDER BY c.name,c.id
            """;
        cmd.Parameters.AddWithValue("$q", query);
        cmd.Parameters.AddWithValue("$like", "%" + query.Replace("%", "\\%").Replace("_", "\\_") + "%");
        var rows = new List<CustomerRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) rows.Add(new(r.GetInt64(0), r.GetString(1), r.GetString(2), Text(r, 4), Text(r, 3)));
        return Task.FromResult<IReadOnlyList<CustomerRow>>(rows);
    }

    public Task<IReadOnlyList<VehicleRow>> GetVehiclesAsync(long customerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT v.id,v.current_customer_id,v.plate,v.model,v.odometer_km,
             (SELECT due_date FROM reminders WHERE vehicle_id=v.id AND kind='Maintenance' AND state='Active' ORDER BY id DESC LIMIT 1),
             (SELECT due_km FROM reminders WHERE vehicle_id=v.id AND kind='Maintenance' AND state='Active' ORDER BY id DESC LIMIT 1),
             (SELECT due_date FROM reminders WHERE vehicle_id=v.id AND kind='Inspection' AND state='Active' ORDER BY id DESC LIMIT 1)
            FROM vehicles v WHERE v.current_customer_id=$id ORDER BY v.plate,v.id
            """;
        cmd.Parameters.AddWithValue("$id", customerId);
        var rows = new List<VehicleRow>(); using var r = cmd.ExecuteReader();
        while (r.Read()) rows.Add(new(r.GetInt64(0), r.GetInt64(1), r.GetString(2), Text(r, 3), Long(r, 4), Date(r, 5), Long(r, 6), Date(r, 7)));
        return Task.FromResult<IReadOnlyList<VehicleRow>>(rows);
    }

    public Task<long> SaveCustomerAsync(CustomerEditor customer, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(customer.Name)) throw new ArgumentException("請輸入客戶姓名");
        cancellationToken.ThrowIfCancellationRequested();
        using var db = Open(); using var tx = db.BeginTransaction(); using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        var id = customer.Id;
        if (id is null)
        {
            cmd.CommandText = "INSERT INTO customers(code,name,address) VALUES ($code,$name,$address) RETURNING id";
            cmd.Parameters.AddWithValue("$code", "C-" + Guid.NewGuid().ToString("N"));
            cmd.Parameters.AddWithValue("$name", customer.Name.Trim()); cmd.Parameters.AddWithValue("$address", Db(customer.Address));
            id = (long)cmd.ExecuteScalar()!;
        }
        else
        {
            cmd.CommandText = "UPDATE customers SET name=$name,address=$address WHERE id=$id";
            cmd.Parameters.AddWithValue("$name", customer.Name.Trim()); cmd.Parameters.AddWithValue("$address", Db(customer.Address));
            cmd.Parameters.AddWithValue("$id", id.Value);
            if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException("找不到客戶");
        }
        cmd.Parameters.Clear();
        cmd.CommandText = "SELECT id FROM customer_contacts WHERE customer_id=$id AND kind='Phone' ORDER BY is_primary DESC,id LIMIT 1";
        cmd.Parameters.AddWithValue("$id", id.Value);
        var contactId = cmd.ExecuteScalar() as long?;
        cmd.Parameters.Clear();
        if (string.IsNullOrWhiteSpace(customer.Phone))
        {
            if (contactId is not null)
            {
                cmd.CommandText = "DELETE FROM customer_contacts WHERE id=$id";
                cmd.Parameters.AddWithValue("$id", contactId.Value); cmd.ExecuteNonQuery();
            }
        }
        else
        {
            cmd.CommandText = contactId is null
                ? "INSERT INTO customer_contacts(customer_id,kind,value,is_primary) VALUES ($customer,'Phone',$phone,1)"
                : "UPDATE customer_contacts SET value=$phone,is_primary=1 WHERE id=$id";
            cmd.Parameters.AddWithValue("$phone", customer.Phone.Trim());
            if (contactId is null) cmd.Parameters.AddWithValue("$customer", id.Value);
            else cmd.Parameters.AddWithValue("$id", contactId.Value);
            cmd.ExecuteNonQuery();
        }
        tx.Commit(); return Task.FromResult(id.Value);
    }

    public Task<long> SaveVehicleAsync(VehicleEditor vehicle, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(vehicle.Plate) || vehicle.CustomerId <= 0 || vehicle.OdometerKm < 0 || vehicle.MaintenanceKm < 0)
            throw new ArgumentException("車牌、車主或里程資料不正確");
        cancellationToken.ThrowIfCancellationRequested();
        using var db = Open(); using var tx = db.BeginTransaction(); using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        var id = vehicle.Id;
        cmd.CommandText = id is null
            ? "INSERT INTO vehicles(current_customer_id,plate,plate_key,model,odometer_km) VALUES ($customer,$plate,$key,$model,$km) RETURNING id"
            : "UPDATE vehicles SET plate=$plate,plate_key=$key,model=$model,odometer_km=$km WHERE id=$id AND current_customer_id=$customer RETURNING id";
        cmd.Parameters.AddWithValue("$customer", vehicle.CustomerId); cmd.Parameters.AddWithValue("$plate", vehicle.Plate.Trim());
        cmd.Parameters.AddWithValue("$key", NormalizePlate(vehicle.Plate)); cmd.Parameters.AddWithValue("$model", Db(vehicle.Model));
        cmd.Parameters.AddWithValue("$km", Db(vehicle.OdometerKm)); if (id is not null) cmd.Parameters.AddWithValue("$id", id.Value);
        id = cmd.ExecuteScalar() as long? ?? throw new InvalidOperationException("找不到車輛或車主");
        cmd.Parameters.Clear(); cmd.CommandText = "DELETE FROM reminders WHERE vehicle_id=$id AND kind IN ('Maintenance','Inspection') ";
        cmd.Parameters.AddWithValue("$id", id.Value); cmd.ExecuteNonQuery();
        if (vehicle.MaintenanceDate is not null || vehicle.MaintenanceKm is > 0)
        {
            cmd.Parameters.Clear(); cmd.CommandText = "INSERT INTO reminders(vehicle_id,kind,due_date,due_km) VALUES ($id,'Maintenance',$date,$km)";
            cmd.Parameters.AddWithValue("$id", id.Value); cmd.Parameters.AddWithValue("$date", Db(vehicle.MaintenanceDate?.ToString("yyyy-MM-dd")));
            cmd.Parameters.AddWithValue("$km", Db(vehicle.MaintenanceKm is > 0 ? vehicle.MaintenanceKm : null)); cmd.ExecuteNonQuery();
        }
        if (vehicle.InspectionDate is not null)
        {
            cmd.Parameters.Clear(); cmd.CommandText = "INSERT INTO reminders(vehicle_id,kind,due_date) VALUES ($id,'Inspection',$date)";
            cmd.Parameters.AddWithValue("$id", id.Value); cmd.Parameters.AddWithValue("$date", vehicle.InspectionDate.Value.ToString("yyyy-MM-dd")); cmd.ExecuteNonQuery();
        }
        tx.Commit(); return Task.FromResult(id.Value);
    }

    public Task<IReadOnlyList<CatalogRow>> GetCatalogAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id,'項目',code,name,unit,default_labor_minor FROM service_items UNION ALL SELECT id,'零件',code,name,coalesce(unit,''),default_price_minor FROM parts ORDER BY 2,3";
        var rows = new List<CatalogRow>(); using var r = cmd.ExecuteReader();
        while (r.Read()) rows.Add(new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt64(5)));
        return Task.FromResult<IReadOnlyList<CatalogRow>>(rows);
    }

    public Task<long> SaveCatalogAsync(CatalogEditor item, CancellationToken cancellationToken = default)
    {
        if (item.Kind is not ("項目" or "零件") || string.IsNullOrWhiteSpace(item.Code) || string.IsNullOrWhiteSpace(item.Name) || item.PriceMinor < 0)
            throw new ArgumentException("目錄資料不正確");
        cancellationToken.ThrowIfCancellationRequested(); using var db = Open(); using var cmd = db.CreateCommand();
        var table = item.Kind == "項目" ? "service_items" : "parts";
        var price = item.Kind == "項目" ? "default_labor_minor" : "default_price_minor";
        cmd.CommandText = item.Id is null
            ? $"INSERT INTO {table}(code,name,unit,{price}) VALUES ($code,$name,$unit,$price) RETURNING id"
            : $"UPDATE {table} SET code=$code,name=$name,unit=$unit,{price}=$price WHERE id=$id RETURNING id";
        cmd.Parameters.AddWithValue("$code", item.Code.Trim()); cmd.Parameters.AddWithValue("$name", item.Name.Trim());
        cmd.Parameters.AddWithValue("$price", item.PriceMinor); cmd.Parameters.AddWithValue("$unit", item.Unit.Trim());
        if (item.Id is not null) cmd.Parameters.AddWithValue("$id", item.Id.Value);
        return Task.FromResult(cmd.ExecuteScalar() as long? ?? throw new InvalidOperationException("找不到目錄項目"));
    }

    public Task<IReadOnlyList<ReminderRow>> GetRemindersAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT v.id,c.id,c.name,c.address,v.plate,r.kind,r.due_date,r.due_km,v.odometer_km
            FROM vehicles v JOIN customers c ON c.id=v.current_customer_id
            LEFT JOIN reminders r ON r.vehicle_id=v.id AND r.state='Active' AND r.kind IN ('Maintenance','Inspection')
            ORDER BY r.due_date,c.name,v.plate
            """;
        var rows = new List<ReminderRow>(); using var r = cmd.ExecuteReader();
        while (r.Read()) rows.Add(new(r.GetInt64(0), r.GetInt64(1), r.GetString(2), Text(r, 3), r.GetString(4), Text(r, 5) ?? "Missing", Date(r, 6), Long(r, 7), Long(r, 8)));
        return Task.FromResult<IReadOnlyList<ReminderRow>>(rows);
    }

    public Task<ShopSettings> GetShopSettingsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT value_json FROM app_settings WHERE key='shop'";
        var json = cmd.ExecuteScalar() as string;
        return Task.FromResult(json is null ? ShopSettings.Default : JsonSerializer.Deserialize<ShopSettings>(json) ?? ShopSettings.Default);
    }

    public Task SaveShopSettingsAsync(ShopSettings settings, CancellationToken cancellationToken = default)
    {
        if (settings.ReminderDays < 0 || settings.CardFeePercent is < 0 or > 100 || settings.TaxPercent is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(settings));
        cancellationToken.ThrowIfCancellationRequested(); using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO app_settings(key,value_json) VALUES ('shop',$json) ON CONFLICT(key) DO UPDATE SET value_json=excluded.value_json";
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(settings)); cmd.ExecuteNonQuery(); return Task.CompletedTask;
    }

    private static object Db(object? value) => value ?? DBNull.Value;
    private static string? Text(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static long? Long(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
    private static DateOnly? Date(SqliteDataReader r, int i) => DateOnly.TryParseExact(Text(r, i), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
    private static string NormalizePlate(string plate) => new(plate.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}

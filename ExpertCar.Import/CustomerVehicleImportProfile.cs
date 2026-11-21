using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExpertCar.Data;
using Microsoft.Data.Sqlite;

namespace ExpertCar.Import;

public sealed record ImportIssue(string SourceFile, string SourceTable, long Row, string Code);
public sealed record ImportCounts(int Success, int Skipped, int Pending, int Failed);
public sealed record ImportReceipt(ImportCounts Counts, IReadOnlyDictionary<string, int> IssueCounts, long? RunId, string? BackupPath);

public sealed class ImportPlan : IDisposable
{
    internal ImportPlan(SqliteConnection stage, string targetPath, string[] paths, string fingerprint,
        string ruleVersion, string profileId, ImportCounts counts, IReadOnlyList<ImportIssue> issues, bool alreadyImported)
    {
        Stage = stage; TargetPath = targetPath; Paths = paths; Fingerprint = fingerprint;
        RuleVersion = ruleVersion; ProfileId = profileId; Counts = counts; Issues = issues; AlreadyImported = alreadyImported;
    }

    internal SqliteConnection Stage { get; }
    internal string TargetPath { get; }
    internal string[] Paths { get; }
    internal string Fingerprint { get; }
    internal string RuleVersion { get; }
    internal string ProfileId { get; }
    internal bool AlreadyImported { get; }
    public ImportCounts Counts { get; }
    public IReadOnlyList<ImportIssue> Issues { get; }
    public IReadOnlyDictionary<string, int> IssueCounts => Issues.GroupBy(i => i.Code)
        .ToDictionary(g => g.Key, g => g.Count());
    public void Dispose() => Stage.Dispose();
}

internal sealed class RuleSet
{
    public string Version { get; set; } = "";
    public List<SourceRule> Sources { get; set; } = [];
}

internal sealed class SourceRule
{
    public string File { get; set; } = "";
    public string Table { get; set; } = "";
    public string Kind { get; set; } = "";
    public List<string> Schemas { get; set; } = [];
    public string ReferenceFingerprint { get; set; } = "";
    public List<LegacyColumn> ReferenceColumns { get; set; } = [];
    public List<string> Required { get; set; } = [];
    public Dictionary<string, string> Fields { get; set; } = [];
    public List<string> NoteFields { get; set; } = [];
    public Dictionary<string, string> Contacts { get; set; } = [];
    public List<string> ApprovedRaw { get; set; } = [];
}

internal sealed class StagedPayload
{
    public Dictionary<string, string> Fields { get; set; } = [];
    public Dictionary<string, string> Raw { get; set; } = [];
    public List<ContactValue> Contacts { get; set; } = [];
    public string? PlateKey { get; set; }
    public string? MaintenanceDate { get; set; }
    public string? InspectionDate { get; set; }
    public long? MaintenanceKm { get; set; }
    public long? MaintenanceDays { get; set; }
    public long? Odometer { get; set; }
    public long? ModelYear { get; set; }
}

internal sealed record ContactValue(string Kind, string Value);

/// <summary>Source files stay read only. Preview lives only in an in-memory SQLite staging database.</summary>
public sealed class CustomerVehicleImportProfile : IImportProfile
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly string _rulesPath;

    public string Id => "customer-vehicle";

    public CustomerVehicleImportProfile(string? rulesPath = null) => _rulesPath = rulesPath ??
        (File.Exists(Path.Combine(AppContext.BaseDirectory, "tools", "Rules", "customer-vehicle-v1.json"))
            ? Path.Combine(AppContext.BaseDirectory, "tools", "Rules", "customer-vehicle-v1.json")
            : Path.Combine(AppContext.BaseDirectory, "Rules", "customer-vehicle-v1.json"));

    public async Task<ImportPlan> PreviewAsync(IReadOnlyList<string> sourcePaths, string targetPath,
        CancellationToken cancellationToken = default)
    {
        if (sourcePaths.Count == 0) throw new ArgumentException("至少選一個來源檔", nameof(sourcePaths));
        var paths = sourcePaths.Select(Path.GetFullPath).OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length)
            throw new ArgumentException("來源檔名不可重複", nameof(sourcePaths));
        var ruleText = await File.ReadAllTextAsync(_rulesPath, cancellationToken);
        var rules = JsonSerializer.Deserialize<RuleSet>(ruleText, Json)
            ?? throw new InvalidDataException("對照規則無效");
        if (string.IsNullOrWhiteSpace(rules.Version)) throw new InvalidDataException("對照規則缺少版本");
        var ruleVersion = rules.Version + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ruleText))).ToLowerInvariant();
        var fingerprint = await FingerprintAsync(paths, cancellationToken);
        targetPath = Path.GetFullPath(targetPath);
        // Preview must not create or migrate the target database.
        using var existing = File.Exists(targetPath) ? Open(targetPath, readOnly: true) : null;
        var existingVehicles = existing is not null && HasTable(existing, "vehicles") ? existing : null;
        var alreadyImported = existing is not null && HasTable(existing, "import_runs") && Exists(existing,
            "SELECT count(*) FROM import_runs WHERE source_fingerprint=$fingerprint AND status='Completed'",
            ("$fingerprint", fingerprint));
        var stage = new SqliteConnection("Data Source=:memory:");
        stage.Open();
        try
        {
            Execute(stage, "CREATE TABLE stage_rows (file TEXT NOT NULL, table_name TEXT NOT NULL, row_no INTEGER NOT NULL, kind TEXT NOT NULL, outcome TEXT NOT NULL, payload TEXT NOT NULL, customer_ref INTEGER, PRIMARY KEY(file,table_name,row_no))");
            Execute(stage, "CREATE TABLE stage_issues (file TEXT NOT NULL, table_name TEXT NOT NULL, row_no INTEGER NOT NULL, code TEXT NOT NULL)");
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = Path.GetFileName(path);
                var matches = rules.Sources.Where(r => r.File.Equals(file, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length != 1) throw new InvalidDataException($"無法辨識來源檔案：{file}；請確認所選資料夾。");
                var rule = matches[0];
                ILegacySource source = Path.GetExtension(path).Equals(".mdb", StringComparison.OrdinalIgnoreCase)
                    ? new MdbLegacySource(path) : new DbfLegacySource(path);
                var tables = await source.GetTablesAsync(cancellationToken);
                var actualSchema = SchemaFingerprint.Compute(tables);
                if (rule.Schemas.Count > 0 && !rule.Schemas.Contains(actualSchema, StringComparer.OrdinalIgnoreCase))
                    throw await SchemaMismatchAsync(rule, tables, paths, targetPath, cancellationToken);
                var table = tables.SingleOrDefault(t =>
                    t.Name.Equals(rule.Table, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException($"來源缺少表：{rule.Table}");
                var columns = table.Columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var missing = rule.Required.Where(c => !columns.Contains(c)).ToArray();
                long rowNo = 0;
                await foreach (var row in table.ReadRowsAsync(cancellationToken))
                {
                    rowNo++;
                    var payload = Map(row.Values, rule, columns, file, table.Name, rowNo, stage);
                    var outcome = missing.Length > 0 ? "Failed" : "Ready";
                    if (missing.Length > 0) Issue(stage, file, table.Name, rowNo, "MissingColumn");
                    else if (rule.Kind is "customer" or "customerVehicle" && string.IsNullOrWhiteSpace(Get(payload, "name")))
                    { outcome = "Pending"; Issue(stage, file, table.Name, rowNo, "MissingName"); }
                    else if (rule.Kind == "phone" && (string.IsNullOrWhiteSpace(payload.PlateKey) || payload.Contacts.Count == 0))
                    { outcome = "Pending"; Issue(stage, file, table.Name, rowNo, "UnlinkedPhone"); }
                    InsertStage(stage, file, table.Name, rowNo, rule.Kind, outcome, payload);
                }
            }
            ResolvePlates(stage, existingVehicles);
            ResolvePhones(stage, existingVehicles);
            var issues = ReadIssues(stage);
            var counts = alreadyImported
                ? new ImportCounts(0, Count(stage, "SELECT count(*) FROM stage_rows"), 0, 0)
                : new ImportCounts(Count(stage, "SELECT count(*) FROM stage_rows WHERE outcome='Ready'"), 0,
                    Count(stage, "SELECT count(*) FROM stage_rows WHERE outcome='Pending'"),
                    Count(stage, "SELECT count(*) FROM stage_rows WHERE outcome='Failed'"));
            return new ImportPlan(stage, targetPath, paths, fingerprint, ruleVersion, Id, counts,
                alreadyImported ? [] : issues, alreadyImported);
        }
        catch { stage.Dispose(); throw; }
    }

    internal static async Task<InvalidDataException> SchemaMismatchAsync(SourceRule rule, IReadOnlyList<LegacyTable> tables,
        IReadOnlyList<string> paths, string targetPath, CancellationToken cancellationToken)
    {
        var file = Path.GetFileName(paths.First(p => Path.GetFileName(p).Equals(rule.File, StringComparison.OrdinalIgnoreCase)));
        var targetDirectory = Path.GetDirectoryName(Path.GetFullPath(targetPath))!;
        Directory.CreateDirectory(targetDirectory);
        var diagnostic = Path.Combine(targetDirectory, "import-schema-diagnostic.txt");
        var actualSchema = SchemaFingerprint.Compute(tables);
        var actualTable = tables.FirstOrDefault(t => t.Name.Equals(rule.Table, StringComparison.OrdinalIgnoreCase));
        var differences = CompareColumns(rule.Table, rule.ReferenceColumns, actualTable?.Columns).ToArray();
        var missingRequired = rule.Required.Where(name => !tables.SelectMany(t => t.Columns).Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))).ToArray();
        var detail = $"來源檔：{file}\n已選檔案：{string.Join(", ", paths.Select(Path.GetFileName))}\n" +
            $"實際結構指紋：{actualSchema}\n核准結構指紋：{string.Join(", ", rule.Schemas)}\n" +
            $"欄位差異（對照核准版本 {rule.ReferenceFingerprint}）：\n{string.Join("\n", differences)}\n" +
            $"缺少必要欄位：{(missingRequired.Length == 0 ? "無" : string.Join(", ", missingRequired))}\n" +
            string.Join("\n", tables.Select(t => $"表 {t.Name}: " + string.Join(", ", t.Columns.Select(c => $"{c.Name} {c.Type}{(c.Length is null ? "" : $"({c.Length})")}")))) +
            "\n可能是讀取工具編碼問題，請將診斷檔傳給客服。\n";
        await File.WriteAllTextAsync(diagnostic, detail, cancellationToken);
        return new InvalidDataException($"{file} 無法辨識，欄位結構與核准版本不同：{differences.FirstOrDefault() ?? "結構摘要不同"}。可能是讀取工具編碼問題，請將診斷檔傳給客服：{diagnostic}");
    }

    private static IEnumerable<string> CompareColumns(string table, IReadOnlyList<LegacyColumn> expected, IReadOnlyList<LegacyColumn>? actual)
    {
        if (actual is null) { yield return $"{table} 表不存在"; yield break; }
        var found = actual.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var column in expected)
        {
            if (found.TryGetValue(column.Name, out var value) &&
                (!column.Type.Equals(value.Type, StringComparison.OrdinalIgnoreCase) || column.Length != value.Length || column.Decimals != value.Decimals))
                yield return $"{table}.{column.Name} 預期 {column.Type}({column.Length})，實際 {value.Type}({value.Length})";
        }
        foreach (var column in expected.Where(c => !found.ContainsKey(c.Name)))
            yield return $"{table} 表少了 {column.Name} 欄位";
        var names = expected.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var column in actual.Where(c => !names.Contains(c.Name)))
            yield return $"{table} 表多了 {column.Name} 欄位";
    }

    public async Task<ImportReceipt> CommitAsync(ImportPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();
        if (plan.AlreadyImported) return new ImportReceipt(plan.Counts, plan.IssueCounts, null, null);
        if (await FingerprintAsync(plan.Paths, cancellationToken) != plan.Fingerprint)
            throw new InvalidOperationException("來源在演練後變更，請重新演練");
        var ruleText = await File.ReadAllTextAsync(_rulesPath, cancellationToken);
        var ruleHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ruleText))).ToLowerInvariant();
        if (!plan.RuleVersion.EndsWith(":" + ruleHash, StringComparison.Ordinal))
            throw new InvalidOperationException("規則在演練後變更，請重新演練");
        await new SqliteStorageService(plan.TargetPath).InitializeAsync(cancellationToken);
        using var db = Open(plan.TargetPath);
        if (Exists(db, "SELECT count(*) FROM import_runs WHERE source_fingerprint=$fingerprint AND status='Completed'",
            ("$fingerprint", plan.Fingerprint)))
            return new ImportReceipt(new ImportCounts(0, TotalRows(plan.Stage), 0, 0), new Dictionary<string, int>(), null, null);
        var backupPath = Backup(db, plan.TargetPath);
        using var tx = db.BeginTransaction();
        try
        {
            if (Exists(db, tx, "SELECT count(*) FROM import_runs WHERE source_fingerprint=$fingerprint AND status='Completed'",
                ("$fingerprint", plan.Fingerprint)))
                return new ImportReceipt(new ImportCounts(0, TotalRows(plan.Stage), 0, 0), new Dictionary<string, int>(), null, backupPath);
            // A second writer may have added a plate after preview. Abort before any committed changes.
            using (var reader = Command(plan.Stage, "SELECT payload FROM stage_rows WHERE kind='customerVehicle' AND outcome='Ready'").ExecuteReader())
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var key = Payload(reader.GetString(0)).PlateKey;
                    if (!string.IsNullOrEmpty(key) && Exists(db, tx, "SELECT count(*) FROM vehicles WHERE plate_key=$key", ("$key", key)))
                        throw new InvalidOperationException("演練後出現相同車牌，請重新演練");
                }
            var runId = Insert(db, tx,
                "INSERT INTO import_runs(source_fingerprint,rule_version,status,scanned_count,imported_count,issue_count) VALUES ($fingerprint,$version,'Completed',$scanned,$imported,$issues)",
                ("$fingerprint", plan.Fingerprint), ("$version", plan.RuleVersion),
                ("$scanned", TotalRows(plan.Stage)), ("$imported", plan.Counts.Success), ("$issues", plan.Issues.Count));
            var customerIdsByPlate = new Dictionary<string, long>();
            using (var reader = Command(plan.Stage, "SELECT file,table_name,row_no,kind,outcome,payload FROM stage_rows ORDER BY CASE WHEN kind='phone' THEN 1 ELSE 0 END,file,row_no").ExecuteReader())
            {
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var file = reader.GetString(0); var table = reader.GetString(1); var row = reader.GetInt64(2);
                    var kind = reader.GetString(3); var outcome = reader.GetString(4);
                    var payload = Payload(reader.GetString(5));
                    long? customerId = null;
                    if (outcome == "Ready")
                    {
                        if (kind == "phone")
                        {
                            customerId = customerIdsByPlate.TryGetValue(payload.PlateKey!, out var stagedId)
                                ? stagedId : FindExistingCustomer(db, tx, payload.PlateKey!);
                            if (customerId is null) throw new InvalidOperationException("電話關聯在演練後變更，請重新演練");
                        }
                        else
                        {
                            customerId = Insert(db, tx, "INSERT INTO customers(code,name,address,note) VALUES ($code,$name,$address,$note)",
                                ("$code", $"IMP-{runId}-{file}-{row}"), ("$name", Get(payload, "name")),
                                ("$address", Null(Get(payload, "address"))), ("$note", Null(Get(payload, "note"))));
                            if (payload.PlateKey is not null) customerIdsByPlate[payload.PlateKey] = customerId.Value;
                        }
                        foreach (var contact in payload.Contacts.Distinct())
                            Insert(db, tx, "INSERT INTO customer_contacts(customer_id,kind,value) VALUES ($customer,$kind,$value)",
                                ("$customer", customerId.Value), ("$kind", contact.Kind), ("$value", contact.Value));
                        if (kind == "customerVehicle" && !string.IsNullOrEmpty(payload.PlateKey))
                        {
                            var vehicleId = Insert(db, tx,
                                "INSERT INTO vehicles(current_customer_id,plate,plate_key,model,model_year,odometer_km) VALUES ($customer,$plate,$key,$model,$year,$odometer)",
                                ("$customer", customerId.Value), ("$plate", Get(payload, "plate")), ("$key", payload.PlateKey),
                                ("$model", Null(Get(payload, "model"))), ("$year", Null(payload.ModelYear)), ("$odometer", Null(payload.Odometer)));
                            if (payload.MaintenanceDate is not null || payload.MaintenanceKm is not null)
                                Insert(db, tx, "INSERT INTO reminders(vehicle_id,kind,due_date,due_km,interval_days,source_ref) VALUES ($vehicle,'Maintenance',$date,$km,$days,$ref)",
                                    ("$vehicle", vehicleId), ("$date", Null(payload.MaintenanceDate)),
                                    ("$km", Null(payload.MaintenanceKm)), ("$days", Null(payload.MaintenanceDays)), ("$ref", $"{runId}:{file}:{row}"));
                            if (payload.InspectionDate is not null)
                                Insert(db, tx, "INSERT INTO reminders(vehicle_id,kind,due_date,source_ref) VALUES ($vehicle,'Inspection',$date,$ref)",
                                    ("$vehicle", vehicleId), ("$date", payload.InspectionDate), ("$ref", $"{runId}:{file}:{row}"));
                        }
                    }
                    Insert(db, tx,
                        "INSERT INTO import_records(run_id,source_file,source_table,source_key,source_row,target_entity,target_id,approved_values_json) VALUES ($run,$file,$table,$key,$row,$entity,$id,$raw)",
                        ("$run", runId), ("$file", file), ("$table", table), ("$key", row.ToString(CultureInfo.InvariantCulture)),
                        ("$row", row), ("$entity", outcome == "Ready" ? "customers" : DBNull.Value),
                        ("$id", Null(customerId)), ("$raw", JsonSerializer.Serialize(payload.Raw)));
                }
            }
            foreach (var issue in plan.Issues)
                Insert(db, tx, "INSERT INTO import_issues(run_id,source_row,error_code,detail) VALUES ($run,$row,$code,$detail)",
                    ("$run", runId), ("$row", issue.Row), ("$code", issue.Code), ("$detail", $"{issue.SourceFile}/{issue.SourceTable}"));
            if (Count(db, tx, "SELECT count(*) FROM import_records WHERE run_id=$run", ("$run", runId)) != TotalRows(plan.Stage))
                throw new InvalidOperationException("匯入核對筆數不符");
            cancellationToken.ThrowIfCancellationRequested();
            tx.Commit();
            return new ImportReceipt(plan.Counts, plan.IssueCounts, runId, backupPath);
        }
        catch { tx.Rollback(); throw; }
    }

    private static StagedPayload Map(IReadOnlyDictionary<string, string> values, SourceRule rule, HashSet<string> columns,
        string file, string table, long row, SqliteConnection stage)
    {
        var payload = new StagedPayload();
        foreach (var (key, column) in rule.Fields)
            if (columns.Contains(column) && values.TryGetValue(column, out var value))
            { payload.Fields[key] = value.Trim(); payload.Raw[column] = value; }
        foreach (var column in rule.ApprovedRaw)
            if (columns.Contains(column) && values.TryGetValue(column, out var value)) payload.Raw[column] = value;
        foreach (var column in rule.NoteFields)
            if (columns.Contains(column) && values.TryGetValue(column, out var value) && !string.IsNullOrWhiteSpace(value))
            { payload.Fields["note"] = value.Trim(); payload.Raw[column] = value; break; }
        foreach (var (column, kind) in rule.Contacts)
            if (columns.Contains(column) && values.TryGetValue(column, out var value) && !string.IsNullOrWhiteSpace(value))
            { payload.Contacts.Add(new ContactValue(kind, value.Trim())); payload.Raw[column] = value; }
        payload.PlateKey = PlateKey(Get(payload, "plate"));
        payload.MaintenanceDate = Date(Get(payload, "maintenanceDate"), "maintenanceDate", file, table, row, stage);
        payload.InspectionDate = Date(Get(payload, "inspectionDate"), "inspectionDate", file, table, row, stage);
        payload.MaintenanceKm = PositiveLong(Get(payload, "maintenanceKm"), "maintenanceKm", file, table, row, stage);
        payload.MaintenanceDays = PositiveLong(Get(payload, "maintenanceDays"), "maintenanceDays", file, table, row, stage);
        payload.Odometer = PositiveLong(Get(payload, "odometer"), "odometer", file, table, row, stage);
        payload.ModelYear = PositiveLong(Get(payload, "year"), "year", file, table, row, stage);
        return payload;
    }

    public static string? PlateKey(string? plate)
    {
        if (string.IsNullOrWhiteSpace(plate)) return null;
        var chars = plate.Normalize(NormalizationForm.FormKC).Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant).ToArray();
        return chars.Length == 0 ? null : new string(chars);
    }

    public static string? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim() == "0") return null;
        var text = value.Trim();
        var parts = text.Contains('/') || text.Contains('-') || text.Contains('.')
            ? text.Split(['/', '-', '.']) : text.Length is 7 or 8
                ? [text[..^4], text[^4..^2], text[^2..]] : [];
        if (parts.Length != 3 || !parts.All(p => p.Length > 0 && p.All(char.IsDigit))) return null;
        if (!int.TryParse(parts[0], out var year) || !int.TryParse(parts[1], out var month) || !int.TryParse(parts[2], out var day)) return null;
        if (parts[0].Length <= 3) year += 1911;
        if (year is < 1900 or > 2100) return null;
        try { return new DateOnly(year, month, day).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static string? Date(string? raw, string field, string file, string table, long row, SqliteConnection stage)
    {
        var result = ParseDate(raw);
        if (!string.IsNullOrWhiteSpace(raw) && raw.Trim() != "0" && result is null) Issue(stage, file, table, row, "InvalidDate:" + field);
        return result;
    }

    private static long? PositiveLong(string? raw, string field, string file, string table, long row, SqliteConnection stage)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) &&
            number == decimal.Truncate(number) && number <= long.MaxValue && number >= 0)
            return number == 0 ? null : (long)number;
        Issue(stage, file, table, row, "InvalidNumber:" + field);
        return null;
    }

    private static void ResolvePlates(SqliteConnection stage, SqliteConnection? existing)
    {
        var rows = new List<(string File, string Table, long Row, string Key)>();
        using (var reader = Command(stage, "SELECT file,table_name,row_no,payload FROM stage_rows WHERE kind='customerVehicle' AND outcome='Ready'").ExecuteReader())
            while (reader.Read())
            {
                var key = Payload(reader.GetString(3)).PlateKey;
                if (key is not null) rows.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2), key));
            }
        foreach (var row in rows)
            if (rows.Count(r => r.Key == row.Key) > 1 || existing is not null &&
                Exists(existing, "SELECT count(*) FROM vehicles WHERE plate_key=$key", ("$key", row.Key)))
            { SetOutcome(stage, row.File, row.Table, row.Row, "Pending"); Issue(stage, row.File, row.Table, row.Row, "DuplicatePlate"); }
    }

    private static void ResolvePhones(SqliteConnection stage, SqliteConnection? existing)
    {
        var cars = new Dictionary<string, int>();
        using (var reader = Command(stage, "SELECT payload FROM stage_rows WHERE kind='customerVehicle' AND outcome='Ready'").ExecuteReader())
            while (reader.Read())
            { var key = Payload(reader.GetString(0)).PlateKey; if (key is not null) cars[key] = cars.GetValueOrDefault(key) + 1; }
        var phones = new List<(string File, string Table, long Row, string? Key)>();
        using (var reader = Command(stage, "SELECT file,table_name,row_no,payload FROM stage_rows WHERE kind='phone' AND outcome='Ready'").ExecuteReader())
            while (reader.Read()) phones.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2), Payload(reader.GetString(3)).PlateKey));
        foreach (var phone in phones)
        {
            var matches = phone.Key is null ? 0 : cars.GetValueOrDefault(phone.Key) +
                (existing is null ? 0 : Count(existing, "SELECT count(*) FROM vehicles WHERE plate_key=$key", ("$key", phone.Key)));
            if (matches == 1) continue;
            SetOutcome(stage, phone.File, phone.Table, phone.Row, "Pending");
            Issue(stage, phone.File, phone.Table, phone.Row, matches > 1 ? "AmbiguousPhoneLink" : "UnlinkedPhone");
        }
    }

    private static long? FindExistingCustomer(SqliteConnection db, SqliteTransaction tx, string key)
    {
        using var command = Command(db, tx, "SELECT current_customer_id FROM vehicles WHERE plate_key=$key", ("$key", key));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var customer = reader.GetInt64(0);
        return reader.Read() ? null : customer;
    }

    private static async Task<string> FingerprintAsync(string[] paths, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in paths)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(path).ToUpperInvariant() + "\0"));
            await using var file = File.OpenRead(path);
            var buffer = new byte[65536];
            int read;
            while ((read = await file.ReadAsync(buffer, ct)) != 0) hash.AppendData(buffer.AsSpan(0, read));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static SqliteConnection Open(string path, bool readOnly = false)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
          ForeignKeys = true, Pooling = false }.ToString());
        db.Open(); return db;
    }

    private static string Backup(SqliteConnection source, string path)
    {
        var directory = Path.Combine(Path.GetDirectoryName(path)!, "backups");
        Directory.CreateDirectory(directory);
        var backup = Path.Combine(directory, $"before-import-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.db");
        using var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backup, Pooling = false }.ToString());
        destination.Open(); source.BackupDatabase(destination); return backup;
    }

    private static void InsertStage(SqliteConnection stage, string file, string table, long row, string kind, string outcome, StagedPayload payload) =>
        Execute(stage, "INSERT INTO stage_rows(file,table_name,row_no,kind,outcome,payload) VALUES ($file,$table,$row,$kind,$outcome,$payload)",
            ("$file", file), ("$table", table), ("$row", row), ("$kind", kind), ("$outcome", outcome), ("$payload", JsonSerializer.Serialize(payload)));
    private static void SetOutcome(SqliteConnection stage, string file, string table, long row, string outcome) =>
        Execute(stage, "UPDATE stage_rows SET outcome=$outcome WHERE file=$file AND table_name=$table AND row_no=$row",
            ("$outcome", outcome), ("$file", file), ("$table", table), ("$row", row));
    private static void Issue(SqliteConnection stage, string file, string table, long row, string code) =>
        Execute(stage, "INSERT INTO stage_issues(file,table_name,row_no,code) VALUES ($file,$table,$row,$code)",
            ("$file", file), ("$table", table), ("$row", row), ("$code", code));
    private static List<ImportIssue> ReadIssues(SqliteConnection stage)
    {
        var issues = new List<ImportIssue>();
        using var reader = Command(stage, "SELECT file,table_name,row_no,code FROM stage_issues ORDER BY file,row_no").ExecuteReader();
        while (reader.Read()) issues.Add(new ImportIssue(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3)));
        return issues;
    }
    private static int TotalRows(SqliteConnection stage) => Count(stage, "SELECT count(*) FROM stage_rows");
    private static int Count(SqliteConnection db, string sql, params (string, object?)[] values)
    {
        using var command = Command(db, sql, values);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
    private static int Count(SqliteConnection db, SqliteTransaction tx, string sql, params (string, object?)[] values)
    {
        using var command = Command(db, tx, sql, values);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
    private static bool Exists(SqliteConnection db, string sql, params (string, object?)[] values) => Count(db, sql, values) > 0;
    private static bool HasTable(SqliteConnection db, string name) => Exists(db,
        "SELECT count(*) FROM sqlite_master WHERE type='table' AND name=$name", ("$name", name));
    private static bool Exists(SqliteConnection db, SqliteTransaction tx, string sql, params (string, object?)[] values) =>
        Convert.ToInt32(Command(db, tx, sql, values).ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    private static long Insert(SqliteConnection db, SqliteTransaction tx, string sql, params (string, object?)[] values)
    {
        using var command = Command(db, tx, sql + "; SELECT last_insert_rowid()", values);
        return (long)command.ExecuteScalar()!;
    }
    private static void Execute(SqliteConnection db, string sql, params (string, object?)[] values)
    {
        using var command = Command(db, sql, values);
        command.ExecuteNonQuery();
    }
    private static SqliteCommand Command(SqliteConnection db, string sql, params (string, object?)[] values) => Command(db, null, sql, values);
    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? tx, string sql, params (string, object?)[] values)
    {
        var command = db.CreateCommand(); command.Transaction = tx; command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
    private static StagedPayload Payload(string json) => JsonSerializer.Deserialize<StagedPayload>(json, Json)!;
    private static string? Get(StagedPayload payload, string key) => payload.Fields.GetValueOrDefault(key);
    private static object Null(object? value) => value ?? DBNull.Value;
}

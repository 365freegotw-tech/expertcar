using System.Text.Json;

namespace ExpertCar.Import;

/// <summary>Each business area supplies a profile; rules select one without changing this coordinator.</summary>
public interface IImportProfile
{
    string Id { get; }
    Task<ImportPlan> PreviewAsync(IReadOnlyList<string> sourcePaths, string targetPath,
        CancellationToken cancellationToken = default);
    Task<ImportReceipt> CommitAsync(ImportPlan plan, CancellationToken cancellationToken = default);
}

public sealed class ImportEngine
{
    private static readonly HashSet<string> V02Files = new(StringComparer.OrdinalIgnoreCase)
    {
        "RCar089.MDB", "SCar089.MDB", "PreRCar089.MDB", "PreSCar089.MDB", "Fixrpt.mdb",
        "Item.MDB", "FixItem.MDB", "Cust_Pay.MDB", "WORK.MDB"
    };
    private readonly string _rulesPath;
    private readonly IReadOnlyDictionary<string, IImportProfile> _profiles;

    public ImportEngine(string? rulesPath = null, IEnumerable<IImportProfile>? profiles = null)
    {
        _rulesPath = rulesPath ?? (File.Exists(Path.Combine(AppContext.BaseDirectory, "tools", "Rules", "customer-vehicle-v1.json"))
            ? Path.Combine(AppContext.BaseDirectory, "tools", "Rules", "customer-vehicle-v1.json")
            : Path.Combine(AppContext.BaseDirectory, "Rules", "customer-vehicle-v1.json"));
        _profiles = (profiles ?? [new CustomerVehicleImportProfile(_rulesPath), new SupercarV02ImportProfile()])
            .ToDictionary(p => p.Id, StringComparer.Ordinal);
    }

    public async Task<ImportPlan> PreviewAsync(IReadOnlyList<string> sourcePaths, string targetPath,
        CancellationToken cancellationToken = default)
    {
        if (sourcePaths.Any(path => V02Files.Contains(Path.GetFileName(path))))
            return await _profiles["supercar-v02"].PreviewAsync(sourcePaths, targetPath, cancellationToken);
        using var rules = JsonDocument.Parse(await File.ReadAllTextAsync(_rulesPath, cancellationToken));
        var id = rules.RootElement.GetProperty("profile").GetString();
        if (id is null || !_profiles.TryGetValue(id, out var profile))
            throw new InvalidDataException("沒有對應的匯入 profile");
        var baseFile = sourcePaths.FirstOrDefault(path => Path.GetFileName(path).Equals("Base.mdb", StringComparison.OrdinalIgnoreCase));
        if (baseFile is not null && id == "customer-vehicle")
        {
            var ruleSet = JsonSerializer.Deserialize<RuleSet>(rules.RootElement.GetRawText(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var rule = ruleSet?.Sources.FirstOrDefault(source => source.File.Equals("Base.mdb", StringComparison.OrdinalIgnoreCase));
            if (rule is not null && rule.Schemas.Count > 0)
            {
                var tables = await new MdbLegacySource(baseFile).GetTablesAsync(cancellationToken);
                if (!rule.Schemas.Contains(SchemaFingerprint.Compute(tables), StringComparer.OrdinalIgnoreCase))
                    throw await CustomerVehicleImportProfile.SchemaMismatchAsync(rule, tables, sourcePaths, targetPath, cancellationToken);
            }
        }
        return await profile.PreviewAsync(sourcePaths, targetPath, cancellationToken);
    }

    public Task<ImportReceipt> CommitAsync(ImportPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!_profiles.TryGetValue(plan.ProfileId, out var profile))
            throw new InvalidOperationException("匯入 profile 已不在此程式中");
        return profile.CommitAsync(plan, cancellationToken);
    }

    public static string[] DiscoverSourceFiles(string folder)
    {
        var files = Directory.EnumerateFiles(folder)
            .Where(p => Path.GetExtension(p).ToLowerInvariant() is ".mdb" or ".dbf")
            .ToArray();
        if (files.Length > 0) return files;
        return new[] { "Base.mdb", "CUSTNAME.DBF", "CUSTPHON.DBF" }
            .Select(name => Path.Combine(folder, name.EndsWith("mdb", StringComparison.OrdinalIgnoreCase) ? "MDB" : "DBF", name))
            .Where(File.Exists).ToArray();
    }

    public static string? ParseDate(string? value) => CustomerVehicleImportProfile.ParseDate(value);
    public static string? PlateKey(string? value) => CustomerVehicleImportProfile.PlateKey(value);
}

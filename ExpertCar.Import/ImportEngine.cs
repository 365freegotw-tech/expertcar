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
    private readonly string _rulesPath;
    private readonly IReadOnlyDictionary<string, IImportProfile> _profiles;

    public ImportEngine(string? rulesPath = null, IEnumerable<IImportProfile>? profiles = null)
    {
        _rulesPath = rulesPath ?? Path.Combine(AppContext.BaseDirectory, "Rules", "customer-vehicle-v1.json");
        _profiles = (profiles ?? [new CustomerVehicleImportProfile(_rulesPath)])
            .ToDictionary(p => p.Id, StringComparer.Ordinal);
    }

    public async Task<ImportPlan> PreviewAsync(IReadOnlyList<string> sourcePaths, string targetPath,
        CancellationToken cancellationToken = default)
    {
        using var rules = JsonDocument.Parse(await File.ReadAllTextAsync(_rulesPath, cancellationToken));
        var id = rules.RootElement.GetProperty("profile").GetString();
        if (id is null || !_profiles.TryGetValue(id, out var profile))
            throw new InvalidDataException("沒有對應的匯入 profile");
        return await profile.PreviewAsync(sourcePaths, targetPath, cancellationToken);
    }

    public Task<ImportReceipt> CommitAsync(ImportPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!_profiles.TryGetValue(plan.ProfileId, out var profile))
            throw new InvalidOperationException("匯入 profile 已不在此程式中");
        return profile.CommitAsync(plan, cancellationToken);
    }

    public static string? ParseDate(string? value) => CustomerVehicleImportProfile.ParseDate(value);
    public static string? PlateKey(string? value) => CustomerVehicleImportProfile.PlateKey(value);
}

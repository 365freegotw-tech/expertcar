using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ExpertCar.Import;

public static class SchemaFingerprint
{
    public static string Compute(IEnumerable<(string Table, IEnumerable<LegacyColumn> Columns)> tables)
    {
        var canonical = new StringBuilder();
        foreach (var (table, columns) in tables.OrderBy(t => t.Table.Trim(), StringComparer.Ordinal))
        {
            Append(table.Trim());
            foreach (var column in columns)
            {
                Append(column.Name.Trim()); Append(NormalizeType(column.Type));
                Append(column.Length?.ToString() ?? "");
                Append(column.Decimals?.ToString() ?? "");
            }
            canonical.Append(';');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();

        void Append(string value) => canonical.Append(value.Length).Append(':').Append(value);
    }

    private static string NormalizeType(string type)
    {
        var value = type.Trim();
        if (value.Length == 1) return value.ToUpperInvariant(); // DBF field codes are case-sensitive in approved fingerprints.
        return value.ToLowerInvariant() switch { "nvarchar" or "character varying" => "varchar", var normalized => normalized };
    }

    public static string Compute(IEnumerable<LegacyTable> tables) =>
        Compute(tables.Select(t => (t.Name, (IEnumerable<LegacyColumn>)t.Columns)));
}

public sealed record SchemaVariant(string SourceFile, string Fingerprint);

public static class SchemaFingerprintCatalog
{
    public static IReadOnlyList<SchemaVariant> Load(string schemaDirectory)
    {
        using var index = JsonDocument.Parse(File.ReadAllText(Path.Combine(schemaDirectory, "index.json")));
        var variants = new List<SchemaVariant>();
        foreach (var entry in index.RootElement.GetProperty("schema_files").EnumerateArray())
        {
            var file = entry.GetProperty("schema_file").GetString()!;
            using var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(schemaDirectory, file)));
            foreach (var database in schema.RootElement.GetProperty("databases").EnumerateArray())
            {
                var tables = database.GetProperty("tables").EnumerateArray().Select(table =>
                    (table.GetProperty("name").GetString()!, (IEnumerable<LegacyColumn>)table.GetProperty("columns")
                        .EnumerateArray().Select(column => new LegacyColumn(
                            column.GetProperty("name").GetString()!, column.GetProperty("type").GetString()!,
                            column.GetProperty("length").ValueKind == JsonValueKind.Number ? column.GetProperty("length").GetInt32() : null,
                            column.TryGetProperty("decimals", out var decimals) && decimals.ValueKind == JsonValueKind.Number ? decimals.GetInt32() : null)).ToArray()))
                    .ToArray();
                variants.Add(new SchemaVariant(database.GetProperty("file").GetString()!, SchemaFingerprint.Compute(tables)));
            }
        }
        return variants;
    }

    public static IReadOnlyList<SchemaVariant> Match(string fileName, string fingerprint,
        IEnumerable<SchemaVariant> catalog) => catalog.Where(v =>
            Path.GetFileName(v.SourceFile).Equals(fileName, StringComparison.OrdinalIgnoreCase) &&
            v.Fingerprint == fingerprint).ToArray();
}

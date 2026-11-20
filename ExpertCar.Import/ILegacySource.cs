namespace ExpertCar.Import;

public sealed record LegacyColumn(string Name, string Type, int? Length = null, int? Decimals = null);

public sealed record LegacyRow(IReadOnlyDictionary<string, string> Values);

public sealed class LegacyTable(
    string name,
    IReadOnlyList<LegacyColumn> columns,
    long? headerRowCount,
    Func<CancellationToken, IAsyncEnumerable<LegacyRow>> readRows)
{
    public string Name { get; } = name;
    public IReadOnlyList<LegacyColumn> Columns { get; } = columns;
    public long? HeaderRowCount { get; } = headerRowCount;
    public long EffectiveRowCount { get; private set; }

    public async IAsyncEnumerable<LegacyRow> ReadRowsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EffectiveRowCount = 0;
        await foreach (var row in readRows(cancellationToken).WithCancellation(cancellationToken))
        {
            EffectiveRowCount++;
            yield return row;
        }
    }
}

public interface ILegacySource
{
    Task<IReadOnlyList<LegacyTable>> GetTablesAsync(CancellationToken cancellationToken = default);
}

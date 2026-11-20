using System.Text;
using DbfDataReader;

namespace ExpertCar.Import;

public sealed class DbfLegacySource(string path) : ILegacySource
{
    private static readonly Encoding Cp950;

    static DbfLegacySource()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Cp950 = Encoding.GetEncoding(950, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    private static DbfDataReaderOptions Options => new()
    {
        Encoding = Cp950,
        SkipDeletedRecords = true,
        UseIndexes = false
    };

    public Task<IReadOnlyList<LegacyTable>> GetTablesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var reader = new DbfDataReader.DbfDataReader(path, Options);
        var columns = reader.DbfTable.Columns
            .Select(c => new LegacyColumn(c.ColumnName!, ((char)c.ColumnType).ToString(), c.Length, c.DecimalCount))
            .ToArray();
        LegacyTable table = new(Path.GetFileNameWithoutExtension(path), columns,
            reader.DbfTable.Header.RecordCount, ReadRowsAsync);
        return Task.FromResult<IReadOnlyList<LegacyTable>>([table]);
    }

    private async IAsyncEnumerable<LegacyRow> ReadRowsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new DbfDataReader.DbfDataReader(path, Options);
        using var raw = File.OpenRead(path);
        var header = reader.DbfTable.Header;
        var bytes = new byte[header.RecordLength];
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            raw.Position = header.HeaderLength + (long)reader.DbfRecord.RecordIndex * header.RecordLength;
            raw.ReadExactly(bytes);
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var column in reader.DbfTable.Columns)
            {
                // DbfDataReader controls row validity/deletion; raw bytes preserve dates and decimals for B03b.
                var value = Cp950.GetString(bytes, column.Start, column.Length).TrimEnd(' ', '\0');
                values.Add(column.ColumnName!, value);
            }
            yield return new LegacyRow(values);
            await Task.Yield();
        }
    }
}

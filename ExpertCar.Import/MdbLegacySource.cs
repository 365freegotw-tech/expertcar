using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace ExpertCar.Import;

public sealed class MdbLegacySource(string path, string? toolsDirectory = null, TimeSpan? timeout = null) : ILegacySource
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(30);
    private static readonly Regex ColumnPattern = new(@"^\s*`(?<name>[^`]+)`\s+(?<type>[a-zA-Z0-9_]+)(?:\s*\(\s*(?<length>\d+))?", RegexOptions.Compiled);

    public async Task<IReadOnlyList<LegacyTable>> GetTablesAsync(CancellationToken cancellationToken = default)
    {
        var names = (await RunTextAsync("mdb-tables", ["-1", path], cancellationToken))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tables = new List<LegacyTable>(names.Length);
        foreach (var name in names)
        {
            var ddl = await RunTextAsync("mdb-schema", ["-T", name, path, "mysql"], cancellationToken);
            var columns = ddl.Split('\n').Select(line => ColumnPattern.Match(line))
                .Where(match => match.Success)
                .Select(match => new LegacyColumn(match.Groups["name"].Value,
                    match.Groups["type"].Value.ToLowerInvariant(),
                    match.Groups["length"].Success ? int.Parse(match.Groups["length"].Value) : null))
                .ToArray();
            if (columns.Length == 0)
                throw new InvalidDataException($"mdb-schema 無法解析表 {name} 的欄位結構。");
            tables.Add(new LegacyTable(name, columns, null, ct => ReadRowsAsync(name, columns, ct)));
        }
        return tables;
    }

    private async IAsyncEnumerable<LegacyRow> ReadRowsAsync(string table, IReadOnlyList<LegacyColumn> columns,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var process = Start("mdb-export", ["-H", path, table]);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            using var output = process.StandardOutput;
            var record = new StringBuilder();
            var inQuotes = false;
            while (true)
            {
                var line = await WithinDeadline(output.ReadLineAsync(deadline.Token).AsTask(), deadline.Token, cancellationToken, "mdb-export");
                if (line is null) break;
                if (record.Length > 0) record.Append('\n');
                record.Append(line);
                for (var i = 0; i < line.Length; i++)
                {
                    if (line[i] != '"') continue;
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"') { i++; continue; }
                    inQuotes = !inQuotes;
                }
                if (inQuotes) continue;
                var cells = ParseCsv(record.ToString());
                record.Clear();
                if (cells.Count != columns.Count)
                    throw new InvalidDataException($"mdb-export 表 {table} 的欄位數不符結構。");
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < columns.Count; i++) values.Add(columns[i].Name, cells[i]);
                yield return new LegacyRow(values);
            }
            if (inQuotes || record.Length != 0) throw new InvalidDataException($"mdb-export 表 {table} 的 CSV 不完整。");
            await WithinDeadline(process.WaitForExitAsync(deadline.Token), deadline.Token, cancellationToken, "mdb-export");
            await WithinDeadline(stderr, deadline.Token, cancellationToken, "mdb-export");
            if (process.ExitCode != 0) throw new InvalidDataException($"mdb-export 表 {table} 失敗（退出碼 {process.ExitCode}）。");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    private async Task<T> WithinDeadline<T>(Task<T> task, CancellationToken deadline, CancellationToken caller, string tool)
    {
        try { return await task; }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new TimeoutException($"{tool} 超過 {_timeout.TotalSeconds:g} 秒。"); }
    }

    private async Task WithinDeadline(Task task, CancellationToken deadline, CancellationToken caller, string tool)
    {
        try { await task; }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new TimeoutException($"{tool} 超過 {_timeout.TotalSeconds:g} 秒。"); }
    }

    private async Task<string> RunTextAsync(string tool, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        using var process = Start(tool, args);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            var result = await stdout;
            await stderr;
            if (process.ExitCode != 0) throw new InvalidDataException($"{tool} 失敗（退出碼 {process.ExitCode}）。");
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"{tool} 超過 {_timeout.TotalSeconds:g} 秒。");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    private Process Start(string tool, IReadOnlyList<string> args)
    {
        var directory = !string.IsNullOrWhiteSpace(toolsDirectory) ? toolsDirectory
            : Environment.GetEnvironmentVariable("EXPERTCAR_MDBTOOLS_DIR");
        if (string.IsNullOrWhiteSpace(directory) && OperatingSystem.IsWindows())
            directory = Path.Combine(AppContext.BaseDirectory, "tools", "mdbtools");
        var localTool = Path.Combine(directory ?? "", OperatingSystem.IsWindows() ? tool + ".exe" : tool);
        var executable = !string.IsNullOrWhiteSpace(directory) && File.Exists(localTool)
            ? Path.GetFullPath(localTool) : tool;
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false, true),
            StandardErrorEncoding = Encoding.UTF8
        };
        info.Environment["MDB_JET3_CHARSET"] = "CP950";
        info.Environment["MDBICONV"] = "UTF-8";
        foreach (var arg in args) info.ArgumentList.Add(arg);
        try { return Process.Start(info)!; }
        catch (Win32Exception ex) { throw new FileNotFoundException($"找不到 MDBTools 命令 {tool}；請設定 EXPERTCAR_MDBTOOLS_DIR 為三個工具所在目錄，或將工具加入 PATH。", executable, ex); }
    }

    private static IReadOnlyList<string> ParseCsv(string record)
    {
        var cells = new List<string>();
        var value = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < record.Length; i++)
        {
            var c = record[i];
            if (c == '"')
            {
                if (quoted && i + 1 < record.Length && record[i + 1] == '"') { value.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (c == ',' && !quoted) { cells.Add(value.ToString()); value.Clear(); }
            else value.Append(c);
        }
        if (quoted) throw new InvalidDataException("mdb-export 的 CSV 引號不完整。");
        cells.Add(value.ToString());
        return cells;
    }
}

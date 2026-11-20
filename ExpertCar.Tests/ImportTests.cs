using ExpertCar.Import;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace ExpertCar.Tests;

[Collection("MDBTools PATH isolation")]
public sealed class ImportTests
{
    private static string Fixture(string file) => Path.Combine(AppContext.BaseDirectory, "Fixtures", file);

    [Fact]
    public async Task Dbf_preserves_cp950_raw_text_and_skips_deleted_rows()
    {
        var source = new DbfLegacySource(Fixture("synthetic-customers.dbf"));
        var table = Assert.Single(await source.GetTablesAsync());
        Assert.Equal(4, table.HeaderRowCount);
        Assert.Equal("C", table.Columns[0].Type);
        var rows = new List<LegacyRow>();
        await foreach (var row in table.ReadRowsAsync()) rows.Add(row);
        Assert.Equal(3, table.EffectiveRowCount);
        Assert.Equal("測試客戶", rows[0].Values["NAME"]);
        Assert.Equal("113/11/20", rows[0].Values["DATE_TEXT"]);
        Assert.Equal("1234.56", rows[0].Values["AMOUNT"].Trim());
        Assert.Equal("20241120", rows[0].Values["DATE"]);
        Assert.Equal("", rows[1].Values["DATE_TEXT"]);
        Assert.Equal("", rows[1].Values["DATE"]);
        Assert.Equal("2024-11-20", rows[2].Values["DATE_TEXT"]);
    }

    [MdbToolsFact]
    public async Task Mdb_streams_rows_and_handles_quoted_multiline_text()
    {
        var source = new MdbLegacySource(Fixture("synthetic-customers.mdb"));
        var table = Assert.Single(await source.GetTablesAsync());
        Assert.Null(table.HeaderRowCount);
        Assert.Contains(table.Columns, c => c.Name == "NAME" && c.Type == "varchar");
        var rows = new List<LegacyRow>();
        await foreach (var row in table.ReadRowsAsync()) rows.Add(row);
        Assert.Equal(4, table.EffectiveRowCount);
        Assert.Equal("測試客戶", rows[0].Values["NAME"]);
        Assert.Equal("113/11/20", rows[0].Values["DATE_TEXT"]);
        Assert.Equal("1234.56", rows[0].Values["AMOUNT"]);
        Assert.Equal("公里", rows[0].Values["MILEAGE"]);
        Assert.Contains("逗號,引號\"與換行\n第二行", rows[0].Values["NOTE"]);
        Assert.Equal("", rows[1].Values["DATE_TEXT"]);
        Assert.Equal("2024-11-20", rows[2].Values["DATE_TEXT"]);
    }

    [MdbToolsFact]
    public async Task Mdb_falls_back_to_path_when_explicit_directory_is_missing()
    {
        var oldPath = Environment.GetEnvironmentVariable("PATH");
        var toolsDirectory = Environment.GetEnvironmentVariable("EXPERTCAR_MDBTOOLS_DIR");
        if (string.IsNullOrWhiteSpace(toolsDirectory) && OperatingSystem.IsWindows())
            toolsDirectory = Path.Combine(AppContext.BaseDirectory, "tools", "mdbtools");
        try
        {
            if (!string.IsNullOrWhiteSpace(toolsDirectory))
                Environment.SetEnvironmentVariable("PATH", toolsDirectory + Path.PathSeparator + oldPath);
            var source = new MdbLegacySource(Fixture("synthetic-customers.mdb"),
                Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
            Assert.Single(await source.GetTablesAsync());
        }
        finally { Environment.SetEnvironmentVariable("PATH", oldPath); }
    }

    [Fact]
    public async Task Same_file_name_with_other_structure_has_other_fingerprint()
    {
        var source = new DbfLegacySource(Fixture("synthetic-customers.dbf"));
        var table = Assert.Single(await source.GetTablesAsync());
        var first = SchemaFingerprint.Compute([table]);
        var changed = SchemaFingerprint.Compute([(table.Name,
            (IEnumerable<LegacyColumn>)[..table.Columns, new LegacyColumn("EXTRA", "C")])]);
        Assert.NotEqual(first, changed);
        var catalog = new[] { new SchemaVariant("DBF/synthetic-customers.dbf", first),
            new SchemaVariant("System/synthetic-customers.dbf", changed) };
        Assert.Single(SchemaFingerprintCatalog.Match("synthetic-customers.dbf", first, catalog));
    }

    [Fact]
    public void Schema_catalog_distinguishes_the_16_known_same_name_variants_when_available()
    {
        var directory = Environment.GetEnvironmentVariable("EXPERTCAR_SCHEMA_DIR");
        if (directory is null) return;
        var catalog = SchemaFingerprintCatalog.Load(directory);
        using var index = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "index.json")));
        var changed = index.RootElement.GetProperty("same_named_files").EnumerateArray()
            .Where(group => !group.GetProperty("same_schema").GetBoolean()).ToArray();
        Assert.Equal(16, changed.Length);
        foreach (var group in changed)
        {
            var variants = group.GetProperty("files").EnumerateArray()
                .Select(file => catalog.Single(v => v.SourceFile == file.GetString()).Fingerprint)
                .Distinct().ToArray();
            Assert.True(variants.Length > 1, group.GetProperty("name").GetString());
        }
    }

    [Fact]
    public async Task Missing_mdbtools_reports_named_command()
    {
        var oldPath = Environment.GetEnvironmentVariable("PATH");
        var emptyDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable("PATH", emptyDirectory);
            var source = new MdbLegacySource(Fixture("synthetic-customers.mdb"), emptyDirectory);
            var error = await Assert.ThrowsAsync<FileNotFoundException>(() => source.GetTablesAsync());
            Assert.Contains("mdb-tables", error.Message);
            Assert.Contains("EXPERTCAR_MDBTOOLS_DIR", error.Message);
        }
        finally { Environment.SetEnvironmentVariable("PATH", oldPath); }
    }

    [Fact]
    public async Task Local_legacy_smoke_checks_counts_only_when_explicitly_enabled()
    {
        var root = Environment.GetEnvironmentVariable("EXPERTCAR_LEGACY_ROOT");
        if (root is null) return;
        var mdb = new MdbLegacySource(Path.Combine(root, "MDB", "Base.mdb"));
        var mdbTables = await mdb.GetTablesAsync();
        var customer = mdbTables.Single(t => t.Name == "CUSTOMER");
        await foreach (var _ in customer.ReadRowsAsync()) { }
        Assert.Equal(30, customer.EffectiveRowCount);
        var dbf = Assert.Single(await new DbfLegacySource(Path.Combine(root, "DBF", "CUSTNAME.DBF")).GetTablesAsync());
        Assert.Equal(21, dbf.HeaderRowCount);
        await foreach (var _ in dbf.ReadRowsAsync()) { }
        Assert.Equal(21, dbf.EffectiveRowCount);
        var schemaDirectory = Environment.GetEnvironmentVariable("EXPERTCAR_SCHEMA_DIR");
        if (schemaDirectory is not null)
        {
            var catalog = SchemaFingerprintCatalog.Load(schemaDirectory);
            Assert.Contains(SchemaFingerprintCatalog.Match("Base.mdb", SchemaFingerprint.Compute(mdbTables), catalog),
                variant => variant.SourceFile == "MDB/Base.mdb");
            Assert.Contains(SchemaFingerprintCatalog.Match("CUSTNAME.DBF", SchemaFingerprint.Compute([dbf]), catalog),
                variant => variant.SourceFile == "DBF/CUSTNAME.DBF");
        }
    }

    [Fact]
    public async Task Hung_mdbtools_is_killed_after_timeout()
    {
        if (OperatingSystem.IsWindows()) return;
        var dir = Path.Combine(Path.GetTempPath(), "expertcar-timeout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var script = Path.Combine(dir, "mdb-tables");
        await File.WriteAllTextAsync(script, "#!/bin/sh\nsleep 10\n");
        System.Diagnostics.Process.Start("chmod", ["+x", script])!.WaitForExit();
        try
        {
            var source = new MdbLegacySource(Fixture("synthetic-customers.mdb"), dir, TimeSpan.FromMilliseconds(100));
            var error = await Assert.ThrowsAsync<TimeoutException>(() => source.GetTablesAsync());
            Assert.Contains("mdb-tables", error.Message);
        }
        finally { Directory.Delete(dir, true); }
    }
}

[CollectionDefinition("MDBTools PATH isolation", DisableParallelization = true)]
public sealed class MdbToolsPathIsolation;

public sealed class MdbToolsFactAttribute : FactAttribute
{
    public MdbToolsFactAttribute()
    {
        var directory = Environment.GetEnvironmentVariable("EXPERTCAR_MDBTOOLS_DIR");
        if (string.IsNullOrWhiteSpace(directory) && OperatingSystem.IsWindows())
            directory = Path.Combine(AppContext.BaseDirectory, "tools", "mdbtools");
        foreach (var tool in new[] { "mdb-tables", "mdb-schema", "mdb-export" })
        {
            var localTool = Path.Combine(directory ?? "", OperatingSystem.IsWindows() ? tool + ".exe" : tool);
            var executable = File.Exists(localTool) ? localTool : tool;
            try
            {
                using var process = Process.Start(new ProcessStartInfo(executable, "--version")
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
                if (process.WaitForExit(2000) && process.ExitCode == 0) continue;
                if (!process.HasExited) process.Kill();
            }
            catch (Win32Exception) { }
            Skip = $"找不到可執行的 {tool}；請設定 EXPERTCAR_MDBTOOLS_DIR 為三個 MDBTools 工具所在目錄，或將工具加入 PATH。";
            return;
        }
    }
}

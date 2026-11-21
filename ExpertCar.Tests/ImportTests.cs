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

    [Fact]
    public void CrLfAndTypeAliasKeepMdbSchemaFingerprint()
    {
        const string ddl = "CREATE TABLE `CUSTOMER`\n (\n `NAME` varchar (50),\n `CAR_ID` varchar (50)\n);\n";
        var unix = MdbLegacySource.ParseColumns(ddl);
        var windows = MdbLegacySource.ParseColumns(ddl.Replace("\n", "\r\n"));
        Assert.Equal(unix, windows);
        var expected = SchemaFingerprint.Compute([("CUSTOMER", (IEnumerable<LegacyColumn>)unix)]);
        var actualColumns = (IEnumerable<LegacyColumn>)[new(" NAME\r", "NVARCHAR\r", 50), new(" CAR_ID ", "VARCHAR", 50)];
        var actual = SchemaFingerprint.Compute([(" CUSTOMER\r ", actualColumns)]);
        Assert.True(expected == actual, $"Parsed: {string.Join(";", unix.Select(c => c.ToString()))}; supplied: {string.Join(";", actualColumns.Select(c => c.ToString()))}");
    }

    [Theory]
    [InlineData("PreRCar089", "PreRCarFix", "8992d072341836fcba9ceec1a7abdcc4757ca5581fe9102266fdfa1e62ea0de2", 5, "T00-0001")]
    [InlineData("PreSCar089", "PreSCarFix", "f08829f60b31066955552f1f32beb0a319e9528b91c5e62d3440112b34bc964f", 7, "P1")]
    public void Big5_mdbtools_bytes_decode_schema_and_export(string file, string table, string approved, int count, string second)
    {
        var ddl = MdbLegacySource.DecodeOutput(File.ReadAllBytes(Fixture(file + "-schema.big5")));
        var columns = MdbLegacySource.ParseColumns(ddl);
        Assert.Equal(count, columns.Length);
        Assert.Contains("估價單號", columns.Select(c => c.Name));
        Assert.Equal(approved, SchemaFingerprint.Compute([(table, (IEnumerable<LegacyColumn>)columns)]));
        var csv = MdbLegacySource.DecodeOutput(File.ReadAllBytes(Fixture(file + "-export.big5")));
        using var reader = new StringReader(csv);
        Assert.Equal(columns.Select(c => c.Name), MdbLegacySource.ParseCsv(reader.ReadLine()!));
        var first = MdbLegacySource.ParseCsv(reader.ReadLine()!);
        Assert.Equal(count, first.Count);
        Assert.Equal(second, first[1]);
    }

    [MdbToolsFact]
    public async Task MdbTools101SyntheticSchemaAndMismatchDiagnostic()
    {
        var root = Environment.GetEnvironmentVariable("EXPERTCAR_SYNTHETIC_ROOT");
        if (root is null) return;
        var baseFile = Path.Combine(root, "Base.mdb");
        var tables = await new MdbLegacySource(baseFile).GetTablesAsync();
        Assert.Equal("bf3287df03c827e42fa4a3e8144ba0b1587dab542f4a9b1f8c2f0129c78b22ab", SchemaFingerprint.Compute(tables));
        var target = Path.Combine(Path.GetTempPath(), "expertcar-schema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        try
        {
            var profile = new CustomerVehicleImportProfile(Path.Combine(AppContext.BaseDirectory, "Rules", "customer-vehicle-v1.json"));
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => profile.PreviewAsync([baseFile], Path.Combine(target, "shop.db")));
            Assert.Contains("Base.mdb", error.Message);
            Assert.Contains("CUSTOMER.CAR_ID 預期 varchar(10)，實際 varchar(50)", error.Message);
            Assert.Contains("可能是讀取工具編碼問題，請將診斷檔傳給客服", error.Message);
            var diagnostic = File.ReadAllText(Path.Combine(target, "import-schema-diagnostic.txt"));
            Assert.Contains("bf3287df03c827e42fa4a3e8144ba0b1587dab542f4a9b1f8c2f0129c78b22ab", diagnostic);
            Assert.Contains("表 CUSTOMER:", diagnostic);
            Assert.Contains("CUSTOMER.CAR_ID 預期 varchar(10)，實際 varchar(50)", diagnostic);
        }
        finally { Directory.Delete(target, true); }
    }

    [MdbToolsFact]
    public async Task MdbTools101ApprovedSchemaAndCrLfMatchRule()
    {
        var root = Environment.GetEnvironmentVariable("EXPERTCAR_APPROVED_ROOT");
        if (root is null) return;
        var file = Path.Combine(root, "Base.mdb");
        var tables = await new MdbLegacySource(file).GetTablesAsync();
        const string approved = "73a0f385d2c6412df4cce3ffb52ea177a8170de86d2c71a742d11a57a3c6f9ca";
        Assert.Equal(approved, SchemaFingerprint.Compute(tables));
        var converted = new List<(string Table, IEnumerable<LegacyColumn> Columns)>();
        foreach (var table in tables)
        {
            var tool = Path.Combine(Environment.GetEnvironmentVariable("EXPERTCAR_MDBTOOLS_DIR")!,
                OperatingSystem.IsWindows() ? "mdb-schema.exe" : "mdb-schema");
            var info = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true };
            info.Environment["MDB_JET3_CHARSET"] = "CP950"; info.Environment["MDBICONV"] = "UTF-8";
            foreach (var arg in new[] { "-T", table.Name, file, "mysql" }) info.ArgumentList.Add(arg);
            using var process = Process.Start(info)!;
            var ddl = await process.StandardOutput.ReadToEndAsync();
            await process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
            converted.Add((table.Name, MdbLegacySource.ParseColumns(ddl.Replace("\n", "\r\n"))));
        }
        Assert.Equal(approved, SchemaFingerprint.Compute(converted));
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

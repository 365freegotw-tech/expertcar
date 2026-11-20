using ExpertCar.Core;
using ExpertCar.Data;

namespace ExpertCar.Import;

/// <summary>WPF 只持有 IStorageService；演練資料保留至確認匯入或重新掃描。</summary>
public sealed class ImportStorageService : SqliteStorageService, IDisposable
{
    private readonly string _databasePath;
    private readonly ImportEngine _engine = new();
    private ImportPlan? _plan;

    public ImportStorageService(string databasePath) : base(databasePath) => _databasePath = databasePath;

    public override async Task<ImportPreview> PreviewImportAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        _plan?.Dispose(); _plan = null;
        if (paths.Count == 0) throw new ArgumentException("請選擇來源資料夾");
        var plan = await _engine.PreviewAsync(paths, _databasePath, cancellationToken);
        _plan = plan;
        return new ImportPreview(plan.Counts.Success, plan.Counts.Skipped, plan.Counts.Pending, plan.Counts.Failed,
            plan.Issues.Select(i => new ImportProblem(i.SourceFile, i.SourceTable, i.Row, i.Code)).ToArray(), plan.AlreadyImported);
    }

    public override async Task<ImportResult> CommitImportAsync(CancellationToken cancellationToken = default)
    {
        var plan = _plan ?? throw new InvalidOperationException("請先執行演練");
        try
        {
            var receipt = await _engine.CommitAsync(plan, cancellationToken);
            return new ImportResult(receipt.Counts.Success, receipt.Counts.Skipped, receipt.Counts.Pending,
                receipt.Counts.Failed, receipt.BackupPath, receipt.RunId);
        }
        finally { _plan = null; plan.Dispose(); }
    }

    public void Dispose() { _plan?.Dispose(); _plan = null; }
}

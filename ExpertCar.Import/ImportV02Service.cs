using ExpertCar.Core;
using ExpertCar.Data;
using Microsoft.Data.Sqlite;

namespace ExpertCar.Import;

/// <summary>SQLite v0.2 工作流程與可確認的 SuperCar 匯入接線。</summary>
public sealed class ImportV02Service(string databasePath) : SqliteV02Service(databasePath), IDisposable
{
    private readonly ImportEngine _engine = new();
    private readonly string _path = Path.GetFullPath(databasePath);
    private ImportPlan? _plan;
    public override async Task<ImportDrillResult> DrillImportAsync(IReadOnlyList<string> paths,CancellationToken cancellationToken=default)
    {
        _plan?.Dispose();_plan=null;
        var plan=await _engine.PreviewAsync(paths,_path,cancellationToken);_plan=plan;
        var p=new ImportPreview(plan.Counts.Success,plan.Counts.Skipped,plan.Counts.Pending,plan.Counts.Failed,
            plan.Issues.Select(i=>new ImportProblem(i.SourceFile,i.SourceTable,i.Row,i.Code)).ToArray(),plan.AlreadyImported);
        return new(p,Sources(plan,true));
    }
    public override async Task<ImportCommitResult> CommitDrilledImportAsync(CancellationToken cancellationToken=default)
    {
        var plan=_plan??throw new InvalidOperationException("請先演練");
        try
        {
            var receipt=await _engine.CommitAsync(plan,cancellationToken);
            var result=new ImportResult(receipt.Counts.Success,receipt.Counts.Skipped,receipt.Counts.Pending,receipt.Counts.Failed,receipt.BackupPath,receipt.RunId);
            return new(result,Sources(plan,false));
        }
        finally{_plan=null;plan.Dispose();}
    }
    private static IReadOnlyList<ImportSourceCount> Sources(ImportPlan plan,bool drill)
    {
        if(plan.ProfileId!="supercar-v02")return [];
        var counts=new List<ImportSourceCount>();
        using var c=plan.Stage.CreateCommand();c.CommandText="SELECT file,table_name,count(*),sum(outcome IN ('Ready','Enrich')),sum(outcome='Imported'),sum(outcome='Skipped'),sum(outcome='Pending') FROM rows GROUP BY file,table_name ORDER BY file,table_name";
        using var r=c.ExecuteReader();while(r.Read())counts.Add(new(r.GetString(0),r.GetString(1),r.GetInt32(2),drill?r.GetInt32(3):r.GetInt32(4),r.GetInt32(5),r.GetInt32(6),0));
        return counts;
    }
    public override async Task<ImportPreview> PreviewImportAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
        => (await DrillImportAsync(paths, cancellationToken)).Summary;
    public override async Task<ImportResult> CommitImportAsync(CancellationToken cancellationToken = default)
        => (await CommitDrilledImportAsync(cancellationToken)).Summary;
    public void Dispose(){_plan?.Dispose();_plan=null;}
}

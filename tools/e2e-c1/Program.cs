using System.Diagnostics;
using ExpertCar.Core;
using ExpertCar.Import;
using Microsoft.Data.Sqlite;

var source=Path.GetFullPath(args[0]);var target=Path.GetFullPath(args[1]);
var original=args.Length>2 && args[2]=="--original";
if(File.Exists(target))File.Delete(target);
using var svc=new ImportV02Service(target);await svc.InitializeAsync();
var names=new[]{"Base.mdb","Item.MDB","FixItem.MDB","RCar089.MDB","SCar089.MDB","Fixrpt.mdb","PreRCar089.MDB","PreSCar089.MDB","Cust_Pay.MDB","WORK.MDB","CUSTNAME.DBF"};
var paths=names.Select(n=>Path.Combine(source,n)).Where(File.Exists).ToArray();
var sw=Stopwatch.StartNew();var preview=await svc.DrillImportAsync(paths);
Console.WriteLine($"drill scanned={preview.Sources.Sum(x=>x.Scanned)} ready={preview.Summary.Success} skipped={preview.Summary.Skipped} pending={preview.Summary.Pending} failed={preview.Summary.Failed} elapsed={sw.Elapsed.TotalSeconds:F1}s");
foreach(var s in preview.Sources)Console.WriteLine($"source {s.SourceFile}/{s.SourceTable}: {s.Scanned} {s.Imported} {s.Skipped} {s.Pending}");
var result=await svc.CommitDrilledImportAsync();Console.WriteLine($"commit imported={result.Summary.Success} skipped={result.Summary.Skipped} pending={result.Summary.Pending} backup={result.Summary.BackupPath} elapsed={sw.Elapsed.TotalSeconds:F1}s");
var replay=await svc.DrillImportAsync(paths);var replayResult=await svc.CommitDrilledImportAsync();Console.WriteLine($"replay imported={replayResult.Summary.Success} skipped={replayResult.Summary.Skipped} pending={replayResult.Summary.Pending}");
using var db=new SqliteConnection($"Data Source={target}");db.Open();
long Count(string sql){using var c=db.CreateCommand();c.CommandText=sql;return (long)c.ExecuteScalar()!;}
foreach(var t in new[]{"customers","vehicles","insurance_policies","parts","service_items","service_parts","work_orders","work_order_lines","payments","receivables","vendors","staff","import_source_links"})Console.WriteLine($"target {t}={Count($"SELECT count(*) FROM {t}")}");
if(original){Console.WriteLine("original package: aggregate import/replay completed; synthetic-only parity and checkout omitted");return;}
var sourceOrders=new Dictionary<string,(string Plate,long Total,int Lines,long Paid)>();
async Task Scan(string filename,string table,Action<IReadOnlyDictionary<string,string>> action)
{
    var tables=await new MdbLegacySource(Path.Combine(source,filename),timeout:TimeSpan.FromMinutes(10)).GetTablesAsync();
    await foreach(var row in tables.Single(t=>t.Name.Equals(table,StringComparison.OrdinalIgnoreCase)).ReadRowsAsync())action(row.Values);
}
await Scan("RCar089.MDB","RCARFIX",r=>{var no=r.GetValueOrDefault("FIX_NO","");var plate=r.GetValueOrDefault("CAR_ID","");if(no.StartsWith('F')&&Enumerable.Range(1,20).Any(n=>plate==$"T00-{n:0000}"))sourceOrders[no]=(plate,long.Parse(r.GetValueOrDefault("REALTOTAL","0"))*100,0,0);});
foreach(var (file,table) in new[]{("SCar089.MDB","SCarfix"),("Fixrpt.mdb","FIXRPT")})
    await Scan(file,table,r=>{var no=r.GetValueOrDefault("Fix_No","");if(sourceOrders.TryGetValue(no,out var o))sourceOrders[no]=(o.Plate,o.Total,o.Lines+1,o.Paid);});
await Scan("Cust_Pay.MDB","Cust_Pay",r=>{var no=r.GetValueOrDefault("Fix_No","");if(sourceOrders.TryGetValue(no,out var o))sourceOrders[no]=(o.Plate,o.Total,o.Lines,o.Paid+long.Parse(r.GetValueOrDefault("Total","0"))*100);});
var samples=0;
for(var n=1;n<=20;n++)
{
    var plate=$"T00-{n:0000}";var found=await svc.QuickFindVehiclesAsync(plate);if(found.Count!=1)throw new Exception($"sample plate {plate} count {found.Count}");
    var h=await svc.GetVehicleHistoryAsync(found[0].VehicleId);if(h is null||h.Orders.Count==0)throw new Exception("empty history");
    var src=sourceOrders.Values.Where(x=>x.Plate==plate).ToArray();
    using var c=db.CreateCommand();c.CommandText="""
        SELECT count(*),coalesce(sum(w.total_minor),0),coalesce(sum((SELECT count(*) FROM work_order_lines l WHERE l.work_order_id=w.id)),0),
        coalesce(sum((SELECT coalesce(sum(p.amount_minor),0) FROM payments p WHERE p.work_order_id=w.id)),0)
        FROM work_orders w WHERE w.vehicle_id=$id AND w.number LIKE 'F%'
        """;c.Parameters.AddWithValue("$id",found[0].VehicleId);
    using var r=c.ExecuteReader();r.Read();var expected=(src.Length,src.Sum(x=>x.Total),src.Sum(x=>x.Lines),src.Sum(x=>x.Paid));
    var actual=(checked((int)r.GetInt64(0)),r.GetInt64(1),checked((int)r.GetInt64(2)),r.GetInt64(3));
    if(expected!=actual)throw new Exception($"source mismatch {plate}: {expected} != {actual}");
    Console.WriteLine($"sample {n:00} orders={actual.Item1} lines={actual.Item3} total_minor={actual.Item2} paid_minor={actual.Item4} match=yes");samples++;
}
Console.WriteLine($"sample vehicles={samples}/20 source detail/amount/payment match=yes");
var vehicle=(await svc.QuickFindVehiclesAsync("T00-0001"))[0];
var draft=new WorkOrderDraft(null,"NEW-250B",vehicle.CustomerId,vehicle.VehicleId,new DateOnly(2026,11,20),"假資料診斷","假資料施工",null,"客戶簽名","加修須同意",[
 new(WorkLineCategory.Labor,"工時","L1",null,"小時",1000,0,0,LaborPriceMode.Hourly,120,100000),
 new(WorkLineCategory.Consumable,"耗材","C1",null,"項",1000,30000),
 new(WorkLineCategory.Disposal,"環保費","D1",null,"項",1000,10000),
 new(WorkLineCategory.Outsource,"外包","O1",null,"項",1000,50000),
 new(WorkLineCategory.Part,"已同意加修","P1","P1","個",1000,20000,AdditionId:1),
 new(WorkLineCategory.Part,"未同意加修","P2","P2","個",1000,500000,AdditionId:2)
 ],[new(1,"已同意","測試",true,DateTimeOffset.Now,"面談","王○明"),new(2,"未同意","測試",false)]);
var id=await svc.SaveWorkOrderAsync(draft);await svc.TransitionWorkOrderAsync(id,WorkOrderStatus.Estimate,WorkOrderStatus.Accepted);await svc.TransitionWorkOrderAsync(id,WorkOrderStatus.Accepted,WorkOrderStatus.InProgress);await svc.TransitionWorkOrderAsync(id,WorkOrderStatus.InProgress,WorkOrderStatus.AwaitingCheckout);
var quote=await svc.QuoteCheckoutAsync(id,new(true,true,true,0.05m,0.03m));
Console.WriteLine($"checkout base={quote.BaseMinor} tax={quote.TaxMinor} card={quote.CardSurchargeMinor} total={quote.TotalMinor}");
if(quote.BaseMinor!=310000 || quote.TaxMinor!=15500 || quote.CardSurchargeMinor!=9800 || quote.TotalMinor!=335300)throw new Exception("pricing mismatch");
var p1=await svc.RecordPaymentAsync(id,"Cash",quote.TotalMinor/2);var p2=await svc.RecordPaymentAsync(id,"Card",quote.TotalMinor-quote.TotalMinor/2);
await svc.TransitionWorkOrderAsync(id,WorkOrderStatus.AwaitingCheckout,WorkOrderStatus.Settled);var snap=await svc.CreatePrintSnapshotAsync(id,"Receipt");
Console.WriteLine($"payment1 due={p1.DueMinor} payment2 due={p2.DueMinor} snapshot={snap.Model.Number} lines={snap.Model.Lines.Count} total={snap.Checkout?.TotalMinor}");
if(p2.DueMinor!=0 || snap.Checkout?.TotalMinor!=quote.TotalMinor || snap.Model.Lines.Count!=5)throw new Exception("payment or snapshot mismatch");
var watch=Stopwatch.StartNew();var catalog=await svc.SearchCatalogAsync(new("虛構零件", "零件",1,30));watch.Stop();Console.WriteLine($"catalog matches={catalog.TotalCount} page={catalog.Items.Count} elapsed_ms={watch.Elapsed.TotalMilliseconds:F1}");
if(catalog.TotalCount!=20000 || watch.Elapsed.TotalMilliseconds>=200)throw new Exception("catalog benchmark failed");

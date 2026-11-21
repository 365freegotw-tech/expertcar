using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExpertCar.Data;
using Microsoft.Data.Sqlite;

namespace ExpertCar.Import;

/// <summary>SuperCar 來源列先在記憶體演練，再以來源鍵與目標交易提交。</summary>
public sealed class SupercarV02ImportProfile : IImportProfile
{
    public string Id => "supercar-v02";
    private const string Version = "supercar-v02-1";
    private static readonly Dictionary<string,(string Table,int Phase)> Sources = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Base.mdb"]=("CUSTOMER",10), ["RCar089.MDB"]=("RCARFIX",30),
        ["SCar089.MDB"]=("SCarfix",40), ["Fixrpt.mdb"]=("FIXRPT",40),
        ["PreRCar089.MDB"]=("PreRCarFix",35), ["PreSCar089.MDB"]=("PreSCarFix",41),
        ["Item.MDB"]=("Item",20), ["FixItem.MDB"]=("FIXITEM",25),
        ["Cust_Pay.MDB"]=("Cust_Pay",50), ["WORK.MDB"]=("CUSTNOTPAY",60),
        ["CUSTNAME.DBF"]=("CUSTNAME",11)
    };
    private static SqliteCommand Cmd(SqliteConnection db,SqliteTransaction? tx,string sql,params (string,object?)[] args)
    {var c=db.CreateCommand();c.Transaction=tx;c.CommandText=sql;foreach(var (k,v) in args)c.Parameters.AddWithValue(k,v??DBNull.Value);return c;}
    private static long Scalar(SqliteConnection db,SqliteTransaction? tx,string sql,params (string,object?)[] args)
    {using var c=Cmd(db,tx,sql,args);return Convert.ToInt64(c.ExecuteScalar(),CultureInfo.InvariantCulture);}
    private static long Insert(SqliteConnection db,SqliteTransaction tx,string sql,params (string,object?)[] args)=>Scalar(db,tx,sql.Contains(" RETURNING id",StringComparison.OrdinalIgnoreCase)?sql:sql+" RETURNING id",args);
    private static void Exec(SqliteConnection db,SqliteTransaction? tx,string sql,params (string,object?)[] args){using var c=Cmd(db,tx,sql,args);c.ExecuteNonQuery();}
    private static string F(IReadOnlyDictionary<string,string> r,string key)=>r.TryGetValue(key,out var v)?v.Trim():"";
    private static string First(IReadOnlyDictionary<string,string> r,params string[] keys)=>keys.Select(k=>F(r,k)).FirstOrDefault(s=>s.Length>0)??"";
    private static object Db(string? value)=>string.IsNullOrWhiteSpace(value)?DBNull.Value:value;
    private static string? Date(string value)=>ImportEngine.ParseDate(value);
    private static string? Year(string value)
    {
        var s=value.Trim();if(s.Length==6 && int.TryParse(s[..4],out var y) && int.TryParse(s[4..],out var m) && y is >=1950 and <=2100 && m is >=1 and <=12)return s[..4];
        if(s.Length==4 && int.TryParse(s,out y) && y is >=1950 and <=2100)return s;
        if(s.Length==3 && int.TryParse(s,out y) && y is >=40 and <=200)return (y+1911).ToString(CultureInfo.InvariantCulture);
        return null;
    }
    private static long Money(string raw)
    {return decimal.TryParse(raw,NumberStyles.Number,CultureInfo.InvariantCulture,out var d) && d>=0 && d<1000000000m ? checked((long)decimal.Round(d*100,0,MidpointRounding.AwayFromZero)):0;}
    private static long Qty(string raw)
    {return decimal.TryParse(raw,NumberStyles.Number,CultureInfo.InvariantCulture,out var d) && d>=0 && d<1000000m?checked((long)decimal.Round(d*1000,0,MidpointRounding.AwayFromZero)):1000;}
    private static string Plate(IReadOnlyDictionary<string,string> r)=>First(r,"CAR_ID","Car_Id","車牌號碼");
    private static string Key(string s)=>ImportEngine.PlateKey(s)??"";
    private static bool NeedsEnrichment(SqliteConnection db,IReadOnlyDictionary<string,string> values)
    {
        using var c=Cmd(db,null,"SELECT make,model_year,raw_year,color FROM vehicles WHERE plate_key=$key",("$key",Key(Plate(values))));
        using var r=c.ExecuteReader();if(!r.Read())return false;
        var missingMake=(r.IsDBNull(0)||r.GetString(0).Length==0)&&F(values,"CARFACTOR2").Length>0;
        var missingYear=(r.IsDBNull(1)||r.GetInt64(1)>2100)&&Year(F(values,"CAR_YEAR")) is not null;
        var missingRaw=(r.IsDBNull(2)||r.GetString(2).Length==0)&&F(values,"CAR_YEAR").Length>0;
        var missingColor=(r.IsDBNull(3)||r.GetString(3).Length==0)&&F(values,"CAR_COLOR").Length>0;
        var result=missingMake||missingYear||missingRaw||missingColor;
        return !r.Read()&&result;
    }
    private static SqliteConnection Open(string path,bool readOnly=false)
    {var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Mode=readOnly?SqliteOpenMode.ReadOnly:SqliteOpenMode.ReadWrite,ForeignKeys=true,Pooling=false,DefaultTimeout=60}.ToString());db.Open();return db;}
    private static async Task<string> Fingerprint(string[] paths,CancellationToken ct)
    {using var h=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);foreach(var p in paths){h.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(p).ToUpperInvariant()+"\0"));await using var s=File.OpenRead(p);var b=new byte[65536];int n;while((n=await s.ReadAsync(b,ct))>0)h.AppendData(b.AsSpan(0,n));}return Convert.ToHexString(h.GetHashAndReset()).ToLowerInvariant();}
    public async Task<ImportPlan> PreviewAsync(IReadOnlyList<string> sourcePaths,string targetPath,CancellationToken cancellationToken=default)
    {
        if(sourcePaths.Count==0)throw new ArgumentException("來源不可空白");
        var paths=sourcePaths.Select(Path.GetFullPath).OrderBy(Path.GetFileName,StringComparer.OrdinalIgnoreCase).ToArray();
        if(paths.Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=paths.Length)throw new ArgumentException("來源檔名重複");
        var fingerprint=await Fingerprint(paths,cancellationToken);targetPath=Path.GetFullPath(targetPath);
        using var existing=File.Exists(targetPath)?Open(targetPath,true):null;
        var completed=existing is not null && Scalar(existing,null,"SELECT count(*) FROM import_runs WHERE source_fingerprint=$fp AND rule_version=$version AND status='Completed'",("$fp",fingerprint),("$version",Version))>0;
        var stage=new SqliteConnection("Data Source=:memory:");stage.Open();
        try
        {
            Exec(stage,null,"CREATE TABLE rows(file TEXT,table_name TEXT,row_no INTEGER,phase INTEGER,payload TEXT,outcome TEXT,reason TEXT,PRIMARY KEY(file,table_name,row_no))");
            Exec(stage,null,"CREATE INDEX ix_rows_phase ON rows(phase,file,row_no)");
            var seenPlate=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var seenCodes=new Dictionary<string,HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var seenCustomers=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            foreach(var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();var file=Path.GetFileName(path);
                if(!Sources.TryGetValue(file,out var spec))throw new InvalidDataException($"不支援來源：{file}");
                ILegacySource source=Path.GetExtension(path).Equals(".dbf",StringComparison.OrdinalIgnoreCase)?new DbfLegacySource(path):new MdbLegacySource(path,timeout:TimeSpan.FromMinutes(10));
                var tables=await source.GetTablesAsync(cancellationToken);
                var chosen=file.Equals("Base.mdb",StringComparison.OrdinalIgnoreCase)?new[]{"INSURE","STAFF","SUPPLIAR","CUSTOMER"}:new[]{spec.Table};
                foreach(var tableName in chosen)
                {
                    var table=tables.FirstOrDefault(t=>t.Name.Equals(tableName,StringComparison.OrdinalIgnoreCase));if(table is null)continue;
                    var phase=tableName.ToUpperInvariant() switch{"INSURE"=>1,"STAFF"=>2,"SUPPLIAR"=>3,_=>spec.Phase};long row=0;
                    await foreach(var item in table.ReadRowsAsync(cancellationToken))
                    {
                        row++;var values=item.Values;var outcome="Ready";string? reason=null;
                        if(existing is not null && Scalar(existing,null,"SELECT count(*) FROM import_source_links WHERE source_file=$f AND source_table=$t AND source_key=$k",("$f",file),("$t",table.Name),("$k",row.ToString(CultureInfo.InvariantCulture)))>0)
                            outcome=tableName.Equals("CUSTOMER",StringComparison.OrdinalIgnoreCase)&&NeedsEnrichment(existing,values)?"Enrich":"Skipped";
                        else if(completed){outcome="Pending";reason="UnresolvedPreviousImport";}
                        else if(tableName.Equals("CUSTOMER",StringComparison.OrdinalIgnoreCase))
                        {
                            var plate=Key(Plate(values));if(plate.Length==0||First(values,"NAME","CUST").Length==0){outcome="Pending";reason="MissingCustomerOrPlate";}
                            else if(!seenPlate.Add(plate)){outcome="Pending";reason="DuplicatePlate";}
                            else if(existing is not null && Scalar(existing,null,"SELECT count(*) FROM vehicles WHERE plate_key=$key",("$key",plate))>1){outcome="Pending";reason="AmbiguousExistingPlate";}
                            var customerCode=First(values,"CUST","CUSTNO");var customerName=First(values,"NAME","CUST");
                            if(customerCode.Length>0 && seenCustomers.TryGetValue(customerCode,out var knownName) && knownName!=customerName){outcome="Pending";reason="DuplicateCustomerCode";}
                            else if(customerCode.Length>0)seenCustomers[customerCode]=customerName;
                        }
                        else if(tableName.Equals("Item",StringComparison.OrdinalIgnoreCase)||tableName.Equals("FIXITEM",StringComparison.OrdinalIgnoreCase))
                        {
                            var code=First(values,tableName.Equals("Item",StringComparison.OrdinalIgnoreCase)?"ItemNo":"FIXITEMNO");
                            var name=First(values,tableName.Equals("Item",StringComparison.OrdinalIgnoreCase)?"ItemName":"FIXITEM");
                            if(code.Length==0||name.Length==0){outcome="Pending";reason="MissingCatalogCodeOrName";}
                            else {var bucket=seenCodes.GetValueOrDefault(tableName);if(bucket is null)seenCodes[tableName]=bucket=new(StringComparer.OrdinalIgnoreCase);if(!bucket.Add(code)){outcome="Pending";reason="DuplicateCatalogCode";}}
                        }
                        else if(tableName.Equals("RCARFIX",StringComparison.OrdinalIgnoreCase)&&F(values,"FIX_NO").Length==0){outcome="Pending";reason="MissingOrderNumber";}
                        else if(tableName.Equals("PreRCarFix",StringComparison.OrdinalIgnoreCase)&&F(values,"估價單號").Length==0){outcome="Pending";reason="MissingEstimateNumber";}
                        else if(tableName.Equals("CUSTNAME",StringComparison.OrdinalIgnoreCase))
                        {
                            var code=F(values,"NO");var name=F(values,"CUSTNAME");
                            if(code.Length==0||name.Length==0){outcome="Pending";reason="MissingCustomerCodeOrName";}
                            else {var bucket=seenCodes.GetValueOrDefault("CUSTNAME");if(bucket is null)seenCodes["CUSTNAME"]=bucket=new(StringComparer.OrdinalIgnoreCase);if(!bucket.Add(code)){outcome="Pending";reason="DuplicateCustomerCode";}}
                        }
                        Exec(stage,null,"INSERT INTO rows VALUES($f,$t,$r,$p,$json,$out,$reason)",("$f",file),("$t",table.Name),("$r",row),("$p",phase),("$json",JsonSerializer.Serialize(values)),("$out",outcome),("$reason",reason));
                    }
                }
            }
            ResolveRelations(stage,existing);
            var issues=new List<ImportIssue>();using(var c=Cmd(stage,null,"SELECT file,table_name,row_no,reason,payload,outcome FROM rows WHERE reason IS NOT NULL OR table_name='CUSTOMER'"))using(var r=c.ExecuteReader())while(r.Read())
            {
                var file=r.GetString(0);var table=r.GetString(1);var row=r.GetInt64(2);
                if(!r.IsDBNull(3))issues.Add(new(file,table,row,r.GetString(3)));
                if(table!="CUSTOMER" || r.GetString(5)=="Skipped")continue;
                var values=JsonSerializer.Deserialize<Dictionary<string,string>>(r.GetString(4))!;
                if(F(values,"VERIFY_DAT").Length>0 || F(values,"RealVerify").Length>0)issues.Add(new(file,table,row,"UnverifiedInspectionSemantics"));
                if(F(values,"SUREDATE11").Length>0 || F(values,"SUREDATE12").Length>0)issues.Add(new(file,table,row,"UnverifiedInsurancePeriod"));
            }
            using(var c=Cmd(stage,null,"SELECT file,table_name,row_no,payload FROM rows WHERE outcome='Ready' AND table_name IN ('Item','FIXITEM','RCARFIX')"))using(var r=c.ExecuteReader())while(r.Read())
            {
                var values=JsonSerializer.Deserialize<Dictionary<string,string>>(r.GetString(3))!;var table=r.GetString(1);
                if(table.Equals("Item",StringComparison.OrdinalIgnoreCase)&&F(values,"Sale_A").Length==0 || table.Equals("FIXITEM",StringComparison.OrdinalIgnoreCase)&&F(values,"FIXCASH").Length==0)
                    issues.Add(new(r.GetString(0),table,r.GetInt64(2),"BlankCatalogPrice"));
                if(table.Equals("RCARFIX",StringComparison.OrdinalIgnoreCase)&&Date(First(values,"DATE","IN_DATE")) is null)
                    issues.Add(new(r.GetString(0),table,r.GetInt64(2),"UnknownOrderDate"));
            }
            var counts=new ImportCounts((int)Scalar(stage,null,"SELECT count(*) FROM rows WHERE outcome IN ('Ready','Enrich')"),(int)Scalar(stage,null,"SELECT count(*) FROM rows WHERE outcome='Skipped'"),(int)Scalar(stage,null,"SELECT count(*) FROM rows WHERE outcome='Pending'"),0);
            return new ImportPlan(stage,targetPath,paths,fingerprint,Version,Id,counts,issues,counts.Success==0 && counts.Skipped>0);
        }
        catch{stage.Dispose();throw;}
    }
    private static void ResolveRelations(SqliteConnection stage,SqliteConnection? existing)
    {
        var plates=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var orderKeys=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var changes=new List<(string File,string Table,long Row,string Reason)>();
        using(var c=Cmd(stage,null,"SELECT file,table_name,row_no,payload,outcome FROM rows ORDER BY phase,file,row_no"))
        using(var reader=c.ExecuteReader())while(reader.Read())
        {
            var table=reader.GetString(1);var outcome=reader.GetString(4);
            if(outcome is not("Ready" or "Skipped"))continue;
            var r=JsonSerializer.Deserialize<Dictionary<string,string>>(reader.GetString(3))!;
            if(table.Equals("CUSTOMER",StringComparison.OrdinalIgnoreCase))
            {plates.Add(Key(Plate(r)));continue;}
            if(table.Equals("RCARFIX",StringComparison.OrdinalIgnoreCase)||table.Equals("PreRCarFix",StringComparison.OrdinalIgnoreCase))
            {
                var number=table.Equals("RCARFIX",StringComparison.OrdinalIgnoreCase)?F(r,"FIX_NO"):"PRE-"+F(r,"估價單號");
                var vehicle=Key(Plate(r));
                if(outcome=="Ready" && !plates.Contains(vehicle) && (existing is null || Scalar(existing,null,"SELECT count(*) FROM vehicles WHERE plate_key=$key",("$key",vehicle))==0))
                    changes.Add((reader.GetString(0),table,reader.GetInt64(2),"MissingVehicle"));
                else if(!orderKeys.Add(number) && outcome=="Ready")changes.Add((reader.GetString(0),table,reader.GetInt64(2),"DuplicateOrderNumber"));
                continue;
            }
            string? parent=table.ToUpperInvariant() switch
            {"SCARFIX" or "FIXRPT"=>First(r,"Fix_No","FIX_NO"),"PRESCARFIX"=>"PRE-"+F(r,"估價單號"),"CUST_PAY" or "CUSTNOTPAY"=>F(r,"Fix_No"),_=>null};
            if(outcome=="Ready" && parent is not null && !orderKeys.Contains(parent) && (existing is null || Scalar(existing,null,"SELECT count(*) FROM work_orders WHERE number=$number",("$number",parent))==0))
                changes.Add((reader.GetString(0),table,reader.GetInt64(2),"OrphanSourceRow"));
        }
        foreach(var x in changes)Exec(stage,null,"UPDATE rows SET outcome='Pending',reason=$reason WHERE file=$file AND table_name=$table AND row_no=$row",("$reason",x.Reason),("$file",x.File),("$table",x.Table),("$row",x.Row));
    }
    public async Task<ImportReceipt> CommitAsync(ImportPlan plan,CancellationToken cancellationToken=default)
    {
        if(plan.ProfileId!=Id)throw new ArgumentException("profile 不符");
        if(await Fingerprint(plan.Paths,cancellationToken)!=plan.Fingerprint)throw new InvalidOperationException("來源在演練後變更");
        using var db=Open(plan.TargetPath);
        if(plan.Counts.Success==0)return new(plan.Counts,plan.IssueCounts,null,null);
        var backupDir=Path.Combine(Path.GetDirectoryName(plan.TargetPath)!,"backups");Directory.CreateDirectory(backupDir);
        var backup=Path.Combine(backupDir,$"before-supercar-import-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.db");
        using(var dst=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=backup,Pooling=false}.ToString())){dst.Open();db.BackupDatabase(dst);}
        using var tx=db.BeginTransaction();
        try
        {
            long run;
            using(var c=Cmd(db,tx,"SELECT id FROM import_runs WHERE source_fingerprint=$fp AND rule_version=$v",("$fp",plan.Fingerprint),("$v",Version)))run=c.ExecuteScalar() as long? ?? 0;
            if(run==0)run=Insert(db,tx,"INSERT INTO import_runs(source_fingerprint,rule_version,status,scanned_count,imported_count,issue_count) VALUES($fp,$v,'Running',$scanned,0,$issues)",("$fp",plan.Fingerprint),("$v",Version),("$scanned",plan.Counts.Success+plan.Counts.Pending+plan.Counts.Skipped),("$issues",plan.Counts.Pending));
            var cars=new Dictionary<string,(long Vehicle,long Customer)>(StringComparer.OrdinalIgnoreCase);
            var orders=new Dictionary<string,long>(StringComparer.OrdinalIgnoreCase);
            var parts=new Dictionary<string,long>(StringComparer.OrdinalIgnoreCase);
            var insurers=new Dictionary<string,long>(StringComparer.OrdinalIgnoreCase);
            using var rows=Cmd(plan.Stage,null,"SELECT file,table_name,row_no,payload,outcome FROM rows ORDER BY phase,file,row_no").ExecuteReader();
            var imported=0;var pending=plan.Counts.Pending;var skipped=plan.Counts.Skipped;
            var updates=new List<(string File,string Table,long Row,string Outcome)>();
            while(rows.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();var file=rows.GetString(0);var table=rows.GetString(1);var row=rows.GetInt64(2);var key=row.ToString(CultureInfo.InvariantCulture);
                var values=JsonSerializer.Deserialize<Dictionary<string,string>>(rows.GetString(3))!;var outcome=rows.GetString(4);
                long? target=null;string entity="";
                using(var c=Cmd(db,tx,"SELECT target_entity,target_id FROM import_source_links WHERE source_file=$f AND source_table=$t AND source_key=$k",("$f",file),("$t",table),("$k",key)))
                using(var r=c.ExecuteReader())if(r.Read()){entity=r.GetString(0);target=r.GetInt64(1);if(outcome!="Enrich")outcome="Skipped";}
                if(outcome=="Enrich")
                {
                    (entity,target)=ImportRow(db,tx,table,values,cars,orders,parts,insurers);
                    if(target is null){pending++;updates.Add((file,table,row,"Pending"));continue;}
                    imported++;updates.Add((file,table,row,"Imported"));continue;
                }
                if(outcome=="Ready")
                {
                    (entity,target)=ImportRow(db,tx,table,values,cars,orders,parts,insurers);
                    if(target is null){pending++;updates.Add((file,table,row,"Pending"));continue;}
                    Exec(db,tx,"INSERT INTO import_source_links(source_file,source_table,source_key,target_entity,target_id,first_run_id) VALUES($f,$t,$k,$e,$id,$run)",("$f",file),("$t",table),("$k",key),("$e",entity),("$id",target),("$run",run));imported++;
                    Exec(db,tx,"INSERT OR IGNORE INTO import_records(run_id,source_file,source_table,source_key,source_row,target_entity,target_id,approved_values_json) VALUES($run,$f,$t,$k,$r,$e,$id,$json)",("$run",run),("$f",file),("$t",table),("$k",key),("$r",row),("$e",entity),("$id",target),("$json",rows.GetString(3)));
                    updates.Add((file,table,row,"Imported"));
                }
                if(outcome=="Skipped" && target is not null)RegisterMap(table,values,target.Value,cars,orders,parts,insurers,db,tx);
            }
            rows.Close();
            foreach(var u in updates)Exec(plan.Stage,null,"UPDATE rows SET outcome=$out WHERE file=$file AND table_name=$table AND row_no=$row",("$out",u.Outcome),("$file",u.File),("$table",u.Table),("$row",u.Row));
            var existingIssues=new HashSet<string>(StringComparer.Ordinal);
            using(var c=Cmd(db,tx,"SELECT source_row,error_code,detail FROM import_issues WHERE run_id=$run",("$run",run)))using(var r=c.ExecuteReader())
                while(r.Read())existingIssues.Add(r.GetInt64(0)+"|"+r.GetString(1)+"|"+(r.IsDBNull(2)?"":r.GetString(2)));
            foreach(var issue in plan.Issues)
            {
                var detail=issue.SourceFile+"/"+issue.SourceTable;
                if(existingIssues.Add(issue.Row+"|"+issue.Code+"|"+detail))
                    Exec(db,tx,"INSERT INTO import_issues(run_id,source_row,error_code,detail) VALUES($run,$row,$code,$detail)",
                        ("$run",run),("$row",issue.Row),("$code",issue.Code),("$detail",detail));
            }
            Exec(db,tx,"UPDATE import_runs SET status='Completed',imported_count=imported_count+$n,issue_count=(SELECT count(*) FROM import_issues WHERE run_id=$id) WHERE id=$id",("$n",imported),("$id",run));
            tx.Commit();return new(new(imported,skipped,pending,0),plan.IssueCounts,run,backup);
        }
        catch{tx.Rollback();throw;}
    }
    private static void RegisterMap(string table,Dictionary<string,string> r,long id,Dictionary<string,(long,long)> cars,Dictionary<string,long> orders,Dictionary<string,long> parts,Dictionary<string,long> insurers,SqliteConnection db,SqliteTransaction tx)
    {
        switch(table.ToUpperInvariant())
        {
            case "CUSTOMER":cars[Key(Plate(r))]=(id,Scalar(db,tx,"SELECT current_customer_id FROM vehicles WHERE id=$id",("$id",id)));break;
            case "RCARFIX":orders[F(r,"FIX_NO")]=id;break;
            case "PRERCARFIX":orders["PRE-"+F(r,"估價單號")]=id;break;
            case "ITEM":parts[F(r,"ItemNo")]=id;break;
            case "INSURE":insurers[F(r,"INSURENO")]=id;break;
        }
    }
    private static (string,long?) ImportRow(SqliteConnection db,SqliteTransaction tx,string table,Dictionary<string,string> r,Dictionary<string,(long Vehicle,long Customer)> cars,Dictionary<string,long> orders,Dictionary<string,long> parts,Dictionary<string,long> insurers)
    {
        switch(table.ToUpperInvariant())
        {
            case "INSURE":
            {
                var code=F(r,"INSURENO");var name=First(r,"INSURE","INSU");if(code.Length==0||name.Length==0)return("insurers",null);
                var id=Insert(db,tx,"INSERT INTO insurers(code,name,phone,address) VALUES($code,$name,$phone,$address) ON CONFLICT(code) DO UPDATE SET name=excluded.name RETURNING id",("$code",code),("$name",name),("$phone",Db(F(r,"PHONE"))),("$address",Db(F(r,"ADDRESS"))));insurers[code]=id;return("insurers",id);
            }
            case "STAFF":
            {
                var name=F(r,"NAME");if(name.Length==0)return("staff",null);
                var id=Insert(db,tx,"INSERT INTO staff(source_code,name,role) VALUES($code,$name,$role) ON CONFLICT(source_code) DO UPDATE SET name=excluded.name,role=excluded.role RETURNING id",("$code",Db(F(r,"STAFFNO"))),("$name",name),("$role",Db(First(r,"TITLE","DEPART"))));return("staff",id);
            }
            case "SUPPLIAR":
            {
                var name=F(r,"SUPPLIAR");if(name.Length==0)return("vendors",null);
                var id=Insert(db,tx,"INSERT INTO vendors(code,name,phone,note) VALUES($code,$name,$phone,$note) ON CONFLICT(code) DO UPDATE SET name=excluded.name RETURNING id",("$code",Db(First(r,"SUPPNO","SUPP"))),("$name",name),("$phone",Db(F(r,"PHONE1"))),("$note",Db(F(r,"NOTE"))));return("vendors",id);
            }
            case "CUSTOMER":
            {
                var plate=Plate(r);var code=First(r,"CUST","CUSTNO");if(code.Length==0)code="PLATE-"+Key(plate);
                var name=First(r,"NAME","CUST");
                long existingVehicle=0,existingCustomer=0;
                using(var c=Cmd(db,tx,"SELECT id,current_customer_id FROM vehicles WHERE plate_key=$key",("$key",Key(plate))))using(var reader=c.ExecuteReader())
                    if(reader.Read()){existingVehicle=reader.GetInt64(0);existingCustomer=reader.GetInt64(1);if(reader.Read())return("vehicles",null);}
                if(existingVehicle>0)
                {
                    var sourceYear=Year(F(r,"CAR_YEAR"));var existingVin=First(r,"VIN","CAR_NO");if(existingVin.Length!=17)existingVin="";
                    Exec(db,tx,"UPDATE vehicles SET make=coalesce(nullif(make,''),$make),model=coalesce(nullif(model,''),$model),model_year=CASE WHEN model_year IS NULL OR model_year>2100 THEN $year ELSE model_year END,raw_year=coalesce(nullif(raw_year,''),$raw),color=coalesce(nullif(color,''),$color),vin=coalesce(nullif(vin,''),$vin) WHERE id=$id",
                        ("$make",Db(F(r,"CARFACTOR2"))),("$model",Db(F(r,"CAR_STYLE"))),("$year",sourceYear is null?DBNull.Value:int.Parse(sourceYear)),("$raw",Db(F(r,"CAR_YEAR"))),("$color",Db(F(r,"CAR_COLOR"))),("$vin",Db(existingVin)),("$id",existingVehicle));
                    cars[Key(plate)]=(existingVehicle,existingCustomer);return("vehicles",existingVehicle);
                }
                long customer;
                using(var c=Cmd(db,tx,"SELECT id,name FROM customers WHERE code=$code",("$code",code)))using(var reader=c.ExecuteReader())
                {if(reader.Read()){if(!reader.GetString(1).Equals(name,StringComparison.Ordinal))return("vehicles",null);customer=reader.GetInt64(0);}else customer=0;}
                if(customer==0)customer=Insert(db,tx,"INSERT INTO customers(code,name,address) VALUES($code,$name,$address)",("$code",code),("$name",name),("$address",Db(F(r,"ADDR_H"))));
                var phone=First(r,"PHONE","ACT_PHONE");if(phone.Length>0 && Scalar(db,tx,"SELECT count(*) FROM customer_contacts WHERE customer_id=$id AND kind='Phone' AND value=$phone",("$id",customer),("$phone",phone))==0)
                    Exec(db,tx,"INSERT INTO customer_contacts(customer_id,kind,value,is_primary) VALUES($id,'Phone',$phone,1)",("$id",customer),("$phone",phone));
                var rawYear=F(r,"CAR_YEAR");var vin=First(r,"VIN","CAR_NO");if(vin.Length!=17)vin="";
                var vehicle=Insert(db,tx,"INSERT INTO vehicles(current_customer_id,plate,plate_key,make,model,model_year,raw_year,color,vin) VALUES($customer,$plate,$key,$make,$model,$year,$raw,$color,$vin)",("$customer",customer),("$plate",plate),("$key",Key(plate)),("$make",Db(F(r,"CARFACTOR2"))),("$model",Db(F(r,"CAR_STYLE"))),("$year",Year(rawYear) is string y?int.Parse(y):DBNull.Value),("$raw",Db(rawYear)),("$color",Db(F(r,"CAR_COLOR"))),("$vin",Db(vin)));
                cars[Key(plate)]=(vehicle,customer);
                Exec(db,tx,"INSERT INTO vehicle_ownerships(vehicle_id,customer_id,start_date,source_note) VALUES($v,$c,'1900-01-01','來源未提供過戶日')",("$v",vehicle),("$c",customer));
                var insurerCode=F(r,"INSURENO1");if(insurerCode.Length>0 && insurers.TryGetValue(insurerCode,out var insurer))
                    Exec(db,tx,"INSERT INTO insurance_policies(vehicle_id,customer_id,insurer_id,kind,start_date,end_date,note,confirmation_state) VALUES($v,$c,$i,'Unknown',$start,$end,$note,'Pending')",("$v",vehicle),("$c",customer),("$i",insurer),("$start",Date(F(r,"SUREDATE11"))),("$end",Date(F(r,"SUREDATE12"))),("$note","保險起訖與 VERIFY_DAT/RealVerify 語意待店家確認"));
                return("vehicles",vehicle);
            }
            case "ITEM":
            {
                var code=F(r,"ItemNo");
                using(var c=Cmd(db,tx,"SELECT id,specification FROM parts WHERE code=$code",("$code",code)))using(var reader=c.ExecuteReader())
                    if(reader.Read())
                    {
                        var existing=reader.GetInt64(0);var stub=!reader.IsDBNull(1)&&reader.GetString(1)=="LegacyAssociationStub";
                        reader.Close();
                        if(!stub)return("parts",null);
                        // shortcut: association stubs have no edit provenance; import Item before editing a stub.
                        Exec(db,tx,"UPDATE parts SET name=$name,unit=$unit,default_price_minor=$price,cost_minor=$cost,specification=NULL WHERE id=$id",
                            ("$name",F(r,"ItemName")),("$unit",Db(F(r,"Unit"))),("$price",Money(First(r,"Sale_A","Sale_B"))),("$cost",Money(F(r,"Sell"))),("$id",existing));
                        parts[code]=existing;return("parts",existing);
                    }
                var id=Insert(db,tx,"INSERT INTO parts(code,name,unit,default_price_minor,cost_minor) VALUES($code,$name,$unit,$price,$cost)",("$code",code),("$name",F(r,"ItemName")),("$unit",Db(F(r,"Unit"))),("$price",Money(First(r,"Sale_A","Sale_B"))),("$cost",Money(F(r,"Sell"))));parts[code]=id;return("parts",id);
            }
            case "FIXITEM":
            {
                var code=F(r,"FIXITEMNO");if(Scalar(db,tx,"SELECT count(*) FROM service_items WHERE code=$code",("$code",code))>0)return("service_items",null);
                var id=Insert(db,tx,"INSERT INTO service_items(code,name,unit,default_labor_minor,default_cost_minor,note) VALUES($code,$name,'次',$price,$cost,$note)",("$code",code),("$name",F(r,"FIXITEM")),("$price",Money(F(r,"FIXCASH"))),("$cost",Money(F(r,"FIXCOST"))),("$note",Db(F(r,"NETIC"))));
                for(var i=1;i<=6;i++){var partCode=F(r,"ItemNo"+i);if(partCode.Length==0)continue;if(!parts.TryGetValue(partCode,out var part)){part=Insert(db,tx,"INSERT INTO parts(code,name,default_price_minor,specification) VALUES($code,$name,$price,'LegacyAssociationStub') ON CONFLICT(code) DO UPDATE SET id=id RETURNING id",("$code",partCode),("$name",First(r,"ItemName"+i,"ItemNo"+i)),("$price",Money(F(r,"ItemSale"+i))));parts[partCode]=part;}
                    Exec(db,tx,"INSERT INTO service_parts(service_id,part_id,line_no,qty_scaled,unit_price_minor,default_use) VALUES($service,$part,$no,$qty,$price,1)",("$service",id),("$part",part),("$no",i),("$qty",Qty(F(r,"ItemQty"+i))),("$price",Money(F(r,"ItemSale"+i))));}
                return("service_items",id);
            }
            case "RCARFIX":case "PRERCARFIX":
            {
                var estimate=table.Equals("PreRCarFix",StringComparison.OrdinalIgnoreCase);
                var number=estimate?"PRE-"+F(r,"估價單號"):F(r,"FIX_NO");var plate=Key(Plate(r));
                if(!cars.TryGetValue(plate,out var owner))return("work_orders",null);
                if(Scalar(db,tx,"SELECT count(*) FROM work_orders WHERE number=$number",("$number",number))>0)return("work_orders",null);
                var date=Date(estimate?F(r,"估價日期"):First(r,"DATE","IN_DATE"))??"1900-01-01";
                var total=estimate?(Money(F(r,"總計"))>0?Money(F(r,"總計")):Money(F(r,"估價金額")))
                    :(Money(F(r,"REALTOTAL"))>0?Money(F(r,"REALTOTAL")):Money(F(r,"TOTAL")));
                var id=Insert(db,tx,"INSERT INTO work_orders(number,customer_id,vehicle_id,status,workflow_status,order_date,plate_snapshot,total_minor,subtotal_minor,note) VALUES($number,$customer,$vehicle,$status,$workflow,$date,$plate,$total,$total,$note)",("$number",number),("$customer",owner.Customer),("$vehicle",owner.Vehicle),("$status",estimate?"Estimate":"ImportReview"),("$workflow",estimate?"Estimate":"ImportReview"),("$date",date),("$plate",Plate(r)),("$total",total),("$note",estimate?"匯入估價":"來源無施工明細（如未匯入明細）"));
                Exec(db,tx,"INSERT INTO receivables(work_order_id,total_minor,paid_minor,due_minor,updated_at) VALUES($id,$total,0,$total,$at)",("$id",id),("$total",total),("$at",DateTimeOffset.UtcNow.ToString("O")));
                if(estimate)Exec(db,tx,"INSERT INTO estimates(work_order_id,version,issued_on,customer_snapshot_json,model_json) VALUES($id,1,$date,$customer,$model)",("$id",id),("$date",date),("$customer",JsonSerializer.Serialize(new{owner.Customer,owner.Vehicle})),("$model",JsonSerializer.Serialize(r)));
                orders[number]=id;return("work_orders",id);
            }
            case "SCARFIX":case "FIXRPT":case "PRESCARFIX":
            {
                var estimate=table.Equals("PreSCarFix",StringComparison.OrdinalIgnoreCase);var number=estimate?"PRE-"+F(r,"估價單號"):First(r,"Fix_No","FIX_NO");
                if(!orders.TryGetValue(number,out var order))return("work_order_lines",null);
                var name=estimate?F(r,"零件名稱"):First(r,"ItemName","FixItemName");if(name.Length==0)name="來源未命名項目";
                var code=estimate?F(r,"零件代號"):First(r,"ItemNo","FixItemNo");var cat=estimate?"Part":table.Equals("FIXRPT",StringComparison.OrdinalIgnoreCase)&&F(r,"FixItemName").Length>0?"Labor":"Part";
                var qty=Qty(estimate?F(r,"數量"):First(r,"Quantity","FixQuantity"));var price=Money(estimate?F(r,"單價"):First(r,"Sale","FixSale"));
                var total=Money(estimate?First(r,"總計","小計"):First(r,"Total","FixTotal"));if(total==0)total=checked((long)decimal.Round((decimal)qty*price/1000,0,MidpointRounding.AwayFromZero));
                var lineNo=Scalar(db,tx,"SELECT coalesce(max(line_no),0)+1 FROM work_order_lines WHERE work_order_id=$id",("$id",order));
                var id=Insert(db,tx,"INSERT INTO work_order_lines(work_order_id,line_no,kind,line_category,code_snapshot,name_snapshot,unit,qty_scaled,unit_price_minor,discount_minor,total_minor) VALUES($order,$no,$kind,$cat,$code,$name,$unit,$qty,$price,0,$total)",("$order",order),("$no",lineNo),("$kind",cat=="Part"?"Part":"Labor"),("$cat",cat),("$code",Db(code)),("$name",name),("$unit",Db(estimate?F(r,"單位"):First(r,"Unit","FixUnit"))),("$qty",qty),("$price",price),("$total",total));
                Exec(db,tx,"UPDATE work_orders SET note=NULL WHERE id=$id",("$id",order));return("work_order_lines",id);
            }
            case "CUST_PAY":
            {
                if(!orders.TryGetValue(F(r,"Fix_No"),out var order))return("payments",null);
                var total=Scalar(db,tx,"SELECT total_minor FROM receivables WHERE work_order_id=$id",("$id",order));var paid=Scalar(db,tx,"SELECT paid_minor FROM receivables WHERE work_order_id=$id",("$id",order));
                var amount=Money(F(r,"Total"));if(amount==0)amount=Money(F(r,"Cash"));if(amount<=0||amount>total-paid)return("payments",null);
                var at=Date(F(r,"In_Date"))??DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
                var method=Money(F(r,"CardPay"))>0?"Card":Money(F(r,"Check"))>0?"Check":"Cash";
                var id=Insert(db,tx,"INSERT INTO payments(work_order_id,paid_on,received_at,method,amount_minor) VALUES($order,$at,$received,$method,$amount)",("$order",order),("$at",at),("$received",at+"T12:00:00+08:00"),("$method",method),("$amount",amount));
                Exec(db,tx,"UPDATE receivables SET paid_minor=$paid,due_minor=$due,updated_at=$at WHERE work_order_id=$order",("$paid",paid+amount),("$due",total-paid-amount),("$at",DateTimeOffset.UtcNow.ToString("O")),("$order",order));return("payments",id);
            }
            case "CUSTNOTPAY":
            {
                if(!orders.TryGetValue(F(r,"Fix_No"),out var order))return("receivables",null);
                var due=Money(F(r,"NotPays"));var actual=Scalar(db,tx,"SELECT due_minor FROM receivables WHERE work_order_id=$id",("$id",order));
                return due==actual?("receivables",order):("receivables",null);
            }
            case "CUSTNAME":
            {
                var name=F(r,"CUSTNAME");if(name.Length==0)return("customers",null);
                var code="DBF-CUSTNAME-"+F(r,"NO");if(Scalar(db,tx,"SELECT count(*) FROM customers WHERE code=$code",("$code",code))>0)return("customers",null);
                var id=Insert(db,tx,"INSERT INTO customers(code,name) VALUES($code,$name)",("$code",code),("$name",name));return("customers",id);
            }
            default:return("unknown",null);
        }
    }
}

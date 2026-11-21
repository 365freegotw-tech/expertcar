using System.IO.Compression;
using ExpertCar.Core;
using ExpertCar.Data;
using ExpertCar.Import;
using Microsoft.Data.Sqlite;

namespace ExpertCar.Tests;

public sealed class V02PersistenceTests
{
    [Fact]
    public async Task Sqlite_workflow_keeps_versions_consents_payments_and_snapshot()
    {
        var dir=Path.Combine(Path.GetTempPath(),"expertcar-v02-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        try
        {
            var path=Path.Combine(dir,"shop.db");var svc=new SqliteV02Service(path);await svc.InitializeAsync();
            var customer=await svc.CreateCustomerWithVehicleAsync(new("C1","王○明"),new("ABC-1234","ABC1234","假車"));
            var vehicle=Assert.Single(await svc.QuickFindVehiclesAsync("abc1234"));Assert.Equal(customer,vehicle.CustomerId);
            var draft=new WorkOrderDraft(null,"W1",customer,vehicle.VehicleId,new DateOnly(2026,11,20),"診斷","施工",null,null,"加修另經同意",[
                new(WorkLineCategory.Labor,"工時",null,null,"小時",1000,0,0,LaborPriceMode.Hourly,90,100000),
                new(WorkLineCategory.Consumable,"耗材",null,null,"個",1000,20000),
                new(WorkLineCategory.Disposal,"環保費",null,null,"次",1000,5000),
                new(WorkLineCategory.Outsource,"外包",null,null,"次",1000,15000),
                new(WorkLineCategory.Part,"同意加修",null,"P1","個",1000,10000,AdditionId:1),
                new(WorkLineCategory.Part,"未同意加修",null,"P2","個",1000,900000,AdditionId:2),
                new(WorkLineCategory.Discount,"行項折扣",null,null,"次",1000,1000)
            ],[new(1,"同意加修","測試",true,DateTimeOffset.UtcNow,"面談","王○明"),new(2,"未同意加修","測試",false)],5000);
            var id=await svc.SaveWorkOrderAsync(draft);Assert.Equal(1,(await svc.GetWorkOrderAsync(id))!.Revision);
            Assert.Equal(id,await svc.SaveWorkOrderAsync(draft with{Id=id}));Assert.Equal(2,(await svc.GetWorkOrderAsync(id))!.Revision);
            await svc.QuoteCheckoutAsync(id,new(false,false));
            await svc.SaveWorkOrderAsync(draft with{Id=id});
            await Assert.ThrowsAsync<InvalidOperationException>(()=>svc.RecordPaymentAsync(id,"Cash",100));
            await svc.TransitionWorkOrderAsync(id,WorkOrderStatus.Estimate,WorkOrderStatus.Accepted);
            await Assert.ThrowsAsync<InvalidOperationException>(()=>svc.DeleteDraftWorkOrderAsync(id));
            await Assert.ThrowsAsync<InvalidOperationException>(()=>svc.TransitionWorkOrderAsync(id,WorkOrderStatus.Estimate,WorkOrderStatus.InProgress));
            await svc.TransitionWorkOrderAsync(id,WorkOrderStatus.Accepted,WorkOrderStatus.InProgress);
            await svc.TransitionWorkOrderAsync(id,WorkOrderStatus.InProgress,WorkOrderStatus.AwaitingCheckout);
            var q=await svc.QuoteCheckoutAsync(id,new(true,true,true,0.05m,0.03m));
            Assert.Equal(194000,q.BaseMinor);Assert.Equal(9700,q.TaxMinor);Assert.Equal(6100,q.CardSurchargeMinor);Assert.Equal(209800,q.TotalMinor);
            Assert.Equal(5000,(await svc.GetWorkOrderAsync(id))!.Draft.OrderDiscountMinor);
            await Assert.ThrowsAsync<InvalidOperationException>(()=>svc.RecordPaymentAsync(id,"Card",q.TotalMinor+1));
            var first=await svc.RecordPaymentAsync(id,"Cash",100000);Assert.Equal(109800,first.DueMinor);
            await Assert.ThrowsAsync<InvalidOperationException>(()=>svc.TransitionWorkOrderAsync(id,WorkOrderStatus.AwaitingCheckout,WorkOrderStatus.Settled));
            var second=await svc.RecordPaymentAsync(id,"Card",109800);Assert.Equal(0,second.DueMinor);
            await svc.TransitionWorkOrderAsync(id,WorkOrderStatus.AwaitingCheckout,WorkOrderStatus.Settled);
            var snap=await svc.CreatePrintSnapshotAsync(id,"Receipt");Assert.Equal(6,snap.Model.Lines.Count);Assert.Equal(q,snap.Checkout);
            using var db=new SqliteConnection($"Data Source={path}");db.Open();using var c=db.CreateCommand();c.CommandText="SELECT count(*) FROM estimates WHERE work_order_id="+id;Assert.Equal(3,(long)c.ExecuteScalar()!);
            c.CommandText="SELECT count(*) FROM customer_consents WHERE work_order_id="+id;Assert.Equal(1,(long)c.ExecuteScalar()!);
            c.CommandText="SELECT count(*) FROM print_snapshots WHERE work_order_id="+id;Assert.Equal(1,(long)c.ExecuteScalar()!);
            c.CommandText="SELECT count(*) FROM payments WHERE work_order_id="+id;Assert.Equal(2,(long)c.ExecuteScalar()!);
            c.CommandText="""
                INSERT INTO customers(code,name) VALUES('C2','李○華');
                INSERT INTO vehicle_ownerships(vehicle_id,customer_id,start_date,end_date) VALUES(1,2,'2015-01-01','2020-01-01');
                INSERT INTO vehicle_ownerships(vehicle_id,customer_id,start_date) VALUES(1,1,'2020-01-02');
                INSERT INTO vehicles(current_customer_id,plate,plate_key) VALUES(1,'XYZ-9999','XYZ9999');
                INSERT INTO work_orders(number,customer_id,vehicle_id,status,order_date) VALUES('OTHER-CAR',1,2,'Estimate','2026-11-20');
                """;c.ExecuteNonQuery();
            var history=await svc.GetVehicleHistoryAsync(vehicle.VehicleId);Assert.NotNull(history);
            Assert.Equal(2,history.Ownerships.Count);Assert.DoesNotContain(history.Orders,o=>o.Number=="OTHER-CAR");
        }
        finally{Directory.Delete(dir,true);}
    }

    [Fact]
    public async Task Supercar_profile_imports_dirty_fixture_and_replay_is_idempotent()
    {
        var root=Path.Combine(Path.GetTempPath(),"expertcar-import-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var zip=Path.Combine(AppContext.BaseDirectory,"Fixtures","supercar-v02.zip");ZipFile.ExtractToDirectory(zip,root);
            var target=Path.Combine(root,"target.db");using var svc=new ImportV02Service(target);await svc.InitializeAsync();
            var paths=Directory.GetFiles(root).Where(p=>!p.EndsWith(".db",StringComparison.OrdinalIgnoreCase)).ToArray();
            var preview=await svc.DrillImportAsync(paths);Assert.True(preview.Summary.Success>0);Assert.True(preview.Summary.Pending>=3);
            var first=await svc.CommitDrilledImportAsync();Assert.True(File.Exists(first.Summary.BackupPath));
            var replay=await svc.DrillImportAsync(paths);Assert.Equal(0,replay.Summary.Success);
            var second=await svc.CommitDrilledImportAsync();Assert.Equal(0,second.Summary.Success);
            using var db=new SqliteConnection($"Data Source={target}");db.Open();
            long Count(string sql){using var c=db.CreateCommand();c.CommandText=sql;return (long)c.ExecuteScalar()!;}
            Assert.Equal(14,Count("SELECT count(*) FROM vehicles"));Assert.Equal(30,Count("SELECT count(*) FROM parts"));
            Assert.Equal(36,Count("SELECT count(*) FROM service_parts"));Assert.Equal(12,Count("SELECT count(*) FROM payments WHERE amount_minor=50000"));
            Assert.Equal(0,Count("SELECT count(*) FROM reminders WHERE kind='Insurance' AND state='Active'"));
            Assert.Equal(0,Count("SELECT count(*) FROM insurance_policies WHERE confirmation_state<>'Pending'"));
            Assert.Equal(first.Summary.Success,Count("SELECT count(*) FROM import_source_links"));
        }
        finally{Directory.Delete(root,true);}
    }

    [Fact]
    public async Task Old_import_link_enriches_missing_vehicle_fields_without_new_vehicle()
    {
        var root=Path.Combine(Path.GetTempPath(),"expertcar-enrich-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            ZipFile.ExtractToDirectory(Path.Combine(AppContext.BaseDirectory,"Fixtures","supercar-v02.zip"),root);
            var target=Path.Combine(root,"target.db");using var svc=new ImportV02Service(target);await svc.InitializeAsync();
            using(var db=new SqliteConnection($"Data Source={target}"))
            {
                db.Open();using var c=db.CreateCommand();c.CommandText="""
                    INSERT INTO customers(code,name) VALUES('C9','王○明0009');
                    INSERT INTO vehicles(current_customer_id,plate,plate_key,model_year) VALUES(1,'T00-0009','T000009',199905);
                    INSERT INTO import_runs(source_fingerprint,rule_version,status) VALUES('legacy','old-v1','Completed');
                    INSERT INTO import_source_links(source_file,source_table,source_key,target_entity,target_id,first_run_id) VALUES('Base.mdb','CUSTOMER','9','customers',1,1);
                    """;c.ExecuteNonQuery();
            }
            var paths=new[]{Path.Combine(root,"Base.mdb"),Path.Combine(root,"Item.MDB")};
            var drill=await svc.DrillImportAsync(paths);Assert.True(drill.Summary.Success>0);
            await svc.CommitDrilledImportAsync();
            using(var db=new SqliteConnection($"Data Source={target}"))
            {
                db.Open();using var c=db.CreateCommand();c.CommandText="SELECT model_year,raw_year,make,color FROM vehicles WHERE plate_key='T000009'";
                using var r=c.ExecuteReader();Assert.True(r.Read());Assert.Equal(2019,r.GetInt32(0));Assert.Equal("201905",r.GetString(1));Assert.False(r.IsDBNull(2));Assert.False(r.IsDBNull(3));
                r.Close();c.CommandText="SELECT count(*) FROM vehicles WHERE plate_key='T000009'";Assert.Equal(1,(long)c.ExecuteScalar()!);
            }
            var replay=await svc.DrillImportAsync(paths);Assert.Equal(0,replay.Summary.Success);
        }
        finally{Directory.Delete(root,true);}
    }

    [Fact]
    public async Task Supercar_commit_rolls_back_all_tables_on_failure()
    {
        var root=Path.Combine(Path.GetTempPath(),"expertcar-rollback-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            ZipFile.ExtractToDirectory(Path.Combine(AppContext.BaseDirectory,"Fixtures","supercar-v02.zip"),root);
            var target=Path.Combine(root,"target.db");using var svc=new ImportV02Service(target);await svc.InitializeAsync();
            using(var db=new SqliteConnection($"Data Source={target}")){db.Open();using var c=db.CreateCommand();c.CommandText="CREATE TRIGGER reject_parts BEFORE INSERT ON parts BEGIN SELECT RAISE(ABORT,'fixture failure'); END";c.ExecuteNonQuery();}
            await svc.DrillImportAsync([Path.Combine(root,"Base.mdb"),Path.Combine(root,"Item.MDB")]);
            await Assert.ThrowsAsync<SqliteException>(()=>svc.CommitDrilledImportAsync());
            using(var db=new SqliteConnection($"Data Source={target}"))
            {
                db.Open();foreach(var table in new[]{"customers","vehicles","parts","insurers","staff","vendors","import_runs","import_source_links"})
                {using var c=db.CreateCommand();c.CommandText=$"SELECT count(*) FROM {table}";Assert.Equal(0,(long)c.ExecuteScalar()!);}
            }
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(root,"backups"),"*.db"));
        }
        finally{Directory.Delete(root,true);}
    }

    [Fact]
    public async Task Item_catalog_can_be_selected_after_service_association_import()
    {
        var root=Path.Combine(Path.GetTempPath(),"expertcar-optional-item-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            ZipFile.ExtractToDirectory(Path.Combine(AppContext.BaseDirectory,"Fixtures","supercar-v02.zip"),root);
            var target=Path.Combine(root,"target.db");using var svc=new ImportV02Service(target);await svc.InitializeAsync();
            await svc.DrillImportAsync([Path.Combine(root,"FixItem.MDB")]);await svc.CommitDrilledImportAsync();
            using(var db=new SqliteConnection($"Data Source={target}"))
            {db.Open();using var c=db.CreateCommand();c.CommandText="SELECT count(*) FROM parts WHERE specification='LegacyAssociationStub'";Assert.True((long)c.ExecuteScalar()!>0);}
            await svc.DrillImportAsync([Path.Combine(root,"Item.MDB")]);await svc.CommitDrilledImportAsync();
            using(var db=new SqliteConnection($"Data Source={target}"))
            {db.Open();using var c=db.CreateCommand();c.CommandText="SELECT count(*) FROM parts WHERE specification='LegacyAssociationStub'";Assert.Equal(0,(long)c.ExecuteScalar()!);c.CommandText="SELECT count(*) FROM parts";Assert.Equal(30,(long)c.ExecuteScalar()!);}
        }
        finally{Directory.Delete(root,true);}
    }
}

using System.Globalization;
using System.Text.Json;
using ExpertCar.Core;
using Microsoft.Data.Sqlite;

namespace ExpertCar.Data;

/// <summary>v0.2 持久化工作流程；匯入由 ExpertCar.Import 的衍生類別提供。</summary>
public partial class SqliteV02Service(string databasePath) : SqliteStorageService(databasePath), IExpertCarV02Service
{
    private readonly string _path = Path.GetFullPath(databasePath);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private SqliteConnection OpenV02()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path, ForeignKeys = true, Pooling = false, DefaultTimeout = 30 }.ToString());
        db.Open(); return db;
    }
    private static SqliteCommand Cmd(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] args)
    {
        var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }
    private static long Scalar(SqliteConnection db, SqliteTransaction? tx, string sql, params (string, object?)[] args)
    { using var cmd = Cmd(db, tx, sql, args); return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture); }
    private static long Insert(SqliteConnection db, SqliteTransaction tx, string sql, params (string, object?)[] args) => Scalar(db, tx, sql + " RETURNING id", args);
    private static int Exec(SqliteConnection db, SqliteTransaction? tx, string sql, params (string, object?)[] args)
    { using var cmd = Cmd(db, tx, sql, args); return cmd.ExecuteNonQuery(); }
    private static string? T(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static long? N(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
    private static DateOnly D(string s) => DateOnly.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static DateOnly? DN(string? s) => s is null ? null : D(s);
    private static string? DS(DateOnly? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string Key(string s) => new(s.Normalize(System.Text.NormalizationForm.FormKC).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static long LineTotal(WorkLineDraft l)
    {
        if (l.Category != WorkLineCategory.Labor || l.LaborMode != LaborPriceMode.Hourly)
            return Pricing.LineTotal(l.QuantityScaled, l.UnitPriceMinor, l.DiscountMinor);
        if (l.LaborMinutes is null or < 0 || l.HourlyRateMinor is null or < 0) throw new ArgumentException("工時與費率不可空白或負數");
        var gross = Pricing.RoundMinor((decimal)l.LaborMinutes.Value * l.HourlyRateMinor.Value / 60);
        if (l.DiscountMinor > gross) throw new ArgumentException("行項折扣超過工資");
        return checked(gross - l.DiscountMinor);
    }
    private static bool Approved(WorkOrderDraft draft, WorkLineDraft l) => l.AdditionId is null || draft.Additions.Any(a => a.Id == l.AdditionId && a.Accepted);
    private static CheckoutQuote BuildQuote(WorkOrderDraft draft, CheckoutOptions options) => CheckoutPricing.Calculate(
        draft.Lines.Where(l => l.Category != WorkLineCategory.Discount)
            .Select(l => new ChargeLine(LineTotal(l), l.AdditionId is not null, Approved(draft, l))),
        checked(draft.OrderDiscountMinor + draft.Lines.Where(l => l.Category == WorkLineCategory.Discount && Approved(draft, l))
            .Aggregate(0L, (sum, l) => checked(sum + LineTotal(l)))), options);

    public Task<IReadOnlyList<VehicleQuickResult>> QuickFindVehiclesAsync(string query, int limit = 20, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        query = query.Trim(); using var db = OpenV02();
        using var cmd = Cmd(db, null, """
            SELECT v.id,c.id,v.plate,c.name,v.make,v.model,v.model_year,v.raw_year,v.color,v.vin
            FROM vehicles v JOIN customers c ON c.id=v.current_customer_id
            WHERE $empty=1 OR v.plate_key LIKE $key ESCAPE '\' OR c.name LIKE $text ESCAPE '\'
               OR EXISTS(SELECT 1 FROM customer_contacts cc WHERE cc.customer_id=c.id AND cc.value LIKE $text ESCAPE '\')
            ORDER BY v.id LIMIT $limit
            """, ("$empty", query.Length == 0 ? 1 : 0), ("$key", "%" + Escape(Key(query)) + "%"),
            ("$text", "%" + Escape(query) + "%"), ("$limit", limit));
        var rows = new List<VehicleQuickResult>(); using var r = cmd.ExecuteReader();
        while (r.Read()) rows.Add(new(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), T(r,4), T(r,5), N(r,6) is long y ? checked((int)y) : null, T(r,7), T(r,8), T(r,9)));
        return Task.FromResult<IReadOnlyList<VehicleQuickResult>>(rows);
    }
    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
    public Task<VehicleHistory?> GetVehicleHistoryAsync(long vehicleId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); using var db = OpenV02();
        VehicleQuickResult? v;
        using (var cmd = Cmd(db,null,"SELECT v.id,c.id,v.plate,c.name,v.make,v.model,v.model_year,v.raw_year,v.color,v.vin FROM vehicles v JOIN customers c ON c.id=v.current_customer_id WHERE v.id=$id",("$id",vehicleId)))
        using (var r = cmd.ExecuteReader()) v = r.Read() ? new(r.GetInt64(0),r.GetInt64(1),r.GetString(2),r.GetString(3),T(r,4),T(r,5),N(r,6) is long y ? checked((int)y) : null,T(r,7),T(r,8),T(r,9)) : null;
        if (v is null) return Task.FromResult<VehicleHistory?>(null);
        var owners = new List<OwnershipPeriod>();
        using (var cmd = Cmd(db,null,"SELECT o.customer_id,c.name,o.start_date,o.end_date FROM vehicle_ownerships o JOIN customers c ON c.id=o.customer_id WHERE o.vehicle_id=$id ORDER BY o.start_date",("$id",vehicleId)))
        using (var r = cmd.ExecuteReader()) while(r.Read()) owners.Add(new(r.GetInt64(0),r.GetString(1),D(r.GetString(2)),DN(T(r,3))));
        var orders = new List<WorkOrderSummary>();
        using (var cmd = Cmd(db,null,"SELECT w.id,w.number,w.workflow_status,w.order_date,w.total_minor,coalesce(rec.due_minor,w.total_minor) FROM work_orders w LEFT JOIN receivables rec ON rec.work_order_id=w.id WHERE w.vehicle_id=$id ORDER BY w.order_date DESC,w.id DESC",("$id",vehicleId)))
        using (var r = cmd.ExecuteReader()) while(r.Read()) orders.Add(new(r.GetInt64(0),r.GetString(1),Enum.Parse<WorkOrderStatus>(r.GetString(2)),D(r.GetString(3)),r.GetInt64(4),r.GetInt64(5)));
        var reminders = new List<ReminderRow>();
        using (var cmd = Cmd(db,null,"SELECT r.kind,r.due_date,r.due_km,v.odometer_km,c.address FROM reminders r JOIN vehicles v ON v.id=r.vehicle_id JOIN customers c ON c.id=v.current_customer_id WHERE r.vehicle_id=$id AND r.state='Active' ORDER BY r.due_date,r.id",("$id",vehicleId)))
        using (var r = cmd.ExecuteReader()) while(r.Read()) reminders.Add(new(vehicleId,v.CustomerId,v.CustomerName,T(r,4),v.Plate,r.GetString(0),DN(T(r,1)),N(r,2),N(r,3)));
        return Task.FromResult<VehicleHistory?>(new(v,owners,orders,reminders));
    }
    public Task<WorkOrderDetail?> GetWorkOrderAsync(long id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); using var db = OpenV02();
        WorkOrderDraft? draft; WorkOrderStatus status; int revision; CheckoutQuote? quote = null;
        using (var cmd = Cmd(db,null,"SELECT number,customer_id,vehicle_id,order_date,diagnosis,repair_description,estimate_expires_on,signature_label,coalesce(additional_work_notice,''),discount_minor,workflow_status,revision,promised_delivery_on FROM work_orders WHERE id=$id",("$id",id)))
        using (var r = cmd.ExecuteReader())
        {
            if (!r.Read()) return Task.FromResult<WorkOrderDetail?>(null);
            draft = new(id,r.GetString(0),r.GetInt64(1),r.GetInt64(2),D(r.GetString(3)),T(r,4),T(r,5),DN(T(r,6)),T(r,7),r.GetString(8),[],[],r.GetInt64(9),DN(T(r,12)));
            status = Enum.Parse<WorkOrderStatus>(r.GetString(10)); revision = r.GetInt32(11);
        }
        var adds = new List<AdditionDraft>();
        using (var cmd = Cmd(db,null,"SELECT a.id,a.title,a.reason,a.state,c.occurred_at,c.method,c.customer_name FROM work_order_additions a LEFT JOIN customer_consents c ON c.addition_id=a.id WHERE a.work_order_id=$id ORDER BY a.id",("$id",id)))
        using (var r = cmd.ExecuteReader()) while(r.Read()) adds.Add(new(r.GetInt64(0),r.GetString(1),T(r,2),r.GetString(3)=="Accepted",T(r,4) is string at ? DateTimeOffset.Parse(at) : null,T(r,5),T(r,6)));
        var lines = new List<WorkLineDraft>();
        using (var cmd = Cmd(db,null,"SELECT l.line_category,l.name_snapshot,l.code_snapshot,l.part_number,l.unit,l.qty_scaled,l.unit_price_minor,l.discount_minor,l.labor_mode,l.labor_minutes,l.hourly_rate_minor,l.vendor_id,l.addition_id,v.name FROM work_order_lines l LEFT JOIN vendors v ON v.id=l.vendor_id WHERE l.work_order_id=$id ORDER BY l.line_no",("$id",id)))
        using (var r = cmd.ExecuteReader()) while(r.Read()) lines.Add(new(Enum.Parse<WorkLineCategory>(r.GetString(0)),r.GetString(1),T(r,2),T(r,3),T(r,4),r.GetInt64(5),r.GetInt64(6),r.GetInt64(7),T(r,8) is string m ? Enum.Parse<LaborPriceMode>(m) : null,N(r,9) is long mins ? checked((int)mins) : null,N(r,10),N(r,11),N(r,12),T(r,13)));
        var payments = new List<PaymentEntry>();
        using (var cmd = Cmd(db,null,"SELECT id,method,amount_minor,coalesce(received_at,paid_on,'1970-01-01T00:00:00+00:00'),note FROM payments WHERE work_order_id=$id ORDER BY id",("$id",id)))
        using (var r = cmd.ExecuteReader()) while(r.Read()) payments.Add(new(r.GetInt64(0),id,r.GetString(1),r.GetInt64(2),DateTimeOffset.Parse(r.GetString(3)),T(r,4)));
        using (var cmd = Cmd(db,null,"SELECT value_json FROM app_settings WHERE key=$key",("$key","v02-quote:"+id)))
            if (cmd.ExecuteScalar() is string json) quote = JsonSerializer.Deserialize<CheckoutQuote>(json,Json);
        return Task.FromResult<WorkOrderDetail?>(new(id,draft with { Lines=lines, Additions=adds },status,revision,payments,quote));
    }
    public Task<long> SaveWorkOrderAsync(WorkOrderDraft draft, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(draft.Number) || draft.CustomerId<=0 || draft.VehicleId<=0) throw new ArgumentException("工單必要資料不足");
        foreach(var l in draft.Lines)
        {
            if (string.IsNullOrWhiteSpace(l.Name) || l.QuantityScaled<0 || l.UnitPriceMinor<0 || l.DiscountMinor<0 || l.LaborMinutes<0 || l.HourlyRateMinor<0) throw new ArgumentException("行項資料不正確");
            _ = LineTotal(l);
        }
        var initialQuote=BuildQuote(draft,new(false,false));
        using var db=OpenV02(); using var tx=db.BeginTransaction();
        if (Scalar(db,tx,"SELECT count(*) FROM vehicles WHERE id=$vehicle AND current_customer_id=$customer",("$vehicle",draft.VehicleId),("$customer",draft.CustomerId))!=1 &&
            (draft.Id is null || Scalar(db,tx,"SELECT count(*) FROM work_orders WHERE id=$id AND vehicle_id=$vehicle AND customer_id=$customer",
                ("$id",draft.Id),("$vehicle",draft.VehicleId),("$customer",draft.CustomerId))!=1))
            throw new ArgumentException("車輛與客戶不符");
        long id; int revision;
        if (draft.Id is null)
        {
            id=Insert(db,tx,"INSERT INTO work_orders(number,customer_id,vehicle_id,status,workflow_status,order_date,subtotal_minor,total_minor,discount_minor,diagnosis,repair_description,estimate_expires_on,signature_label,additional_work_notice,promised_delivery_on) VALUES($number,$customer,$vehicle,'Estimate','Estimate',$date,$subtotal,$total,$discount,$diagnosis,$repair,$expires,$signature,$notice,$delivery)",
                ("$number",draft.Number),("$customer",draft.CustomerId),("$vehicle",draft.VehicleId),("$date",DS(draft.OrderDate)),("$subtotal",initialQuote.SubtotalMinor),("$total",initialQuote.TotalMinor),("$discount",draft.OrderDiscountMinor),("$diagnosis",draft.Diagnosis),("$repair",draft.RepairDescription),("$expires",DS(draft.EstimateExpiresOn)),("$signature",draft.SignatureLabel),("$notice",draft.AdditionalWorkNotice),("$delivery",DS(draft.PromisedDeliveryOn))); revision=1;
        }
        else
        {
            id=draft.Id.Value;
            if (Scalar(db,tx,"SELECT count(*) FROM work_orders WHERE id=$id",("$id",id))!=1) throw new InvalidOperationException("找不到工單");
            if (Scalar(db,tx,"SELECT count(*) FROM payments WHERE work_order_id=$id",("$id",id))>0) throw new InvalidOperationException("已收款不可改價");
            revision=checked((int)Scalar(db,tx,"SELECT revision+1 FROM work_orders WHERE id=$id",("$id",id)));
            Exec(db,tx,"UPDATE work_orders SET number=$number,customer_id=$customer,vehicle_id=$vehicle,order_date=$date,subtotal_minor=$subtotal,total_minor=$total,discount_minor=$discount,diagnosis=$diagnosis,repair_description=$repair,estimate_expires_on=$expires,signature_label=$signature,additional_work_notice=$notice,promised_delivery_on=$delivery,revision=$rev WHERE id=$id",
                ("$number",draft.Number),("$customer",draft.CustomerId),("$vehicle",draft.VehicleId),("$date",DS(draft.OrderDate)),("$subtotal",initialQuote.SubtotalMinor),("$total",initialQuote.TotalMinor),("$discount",draft.OrderDiscountMinor),("$diagnosis",draft.Diagnosis),("$repair",draft.RepairDescription),("$expires",DS(draft.EstimateExpiresOn)),("$signature",draft.SignatureLabel),("$notice",draft.AdditionalWorkNotice),("$delivery",DS(draft.PromisedDeliveryOn)),("$rev",revision),("$id",id));
            Exec(db,tx,"DELETE FROM work_order_lines WHERE work_order_id=$id",("$id",id));
            Exec(db,tx,"DELETE FROM customer_consents WHERE work_order_id=$id",("$id",id));
            Exec(db,tx,"DELETE FROM work_order_additions WHERE work_order_id=$id",("$id",id));
            Exec(db,tx,"DELETE FROM receivables WHERE work_order_id=$id",("$id",id));
        }
        var additions=new Dictionary<long,long>();
        foreach(var a in draft.Additions)
        {
            var aid=Insert(db,tx,"INSERT INTO work_order_additions(work_order_id,title,reason,state,proposed_at) VALUES($id,$title,$reason,$state,$at)",("$id",id),("$title",a.Title),("$reason",a.Reason),("$state",a.Accepted?"Accepted":"Pending"),("$at",DateTimeOffset.UtcNow.ToString("O")));
            if(a.Id is long old) additions[old]=aid;
            if(a.Accepted) Exec(db,tx,"INSERT INTO customer_consents(work_order_id,addition_id,decision,method,customer_name,occurred_at) VALUES($id,$addition,'Accepted',$method,$name,$at)",("$id",id),("$addition",aid),("$method",a.ConsentMethod ?? "記錄"),("$name",a.CustomerName ?? "未記錄"),("$at",(a.AcceptedAt ?? DateTimeOffset.UtcNow).ToString("O")));
        }
        for(var i=0;i<draft.Lines.Count;i++)
        {
            var l=draft.Lines[i]; long? addition = l.AdditionId is long old && additions.TryGetValue(old,out var mapped) ? mapped : null;
            if(l.AdditionId is not null && addition is null) throw new ArgumentException("行項加修代碼無效");
            var legacy=l.Category==WorkLineCategory.Part?"Part":"Labor";
            var vendorId=l.VendorId;
            if (l.Category==WorkLineCategory.Outsource && !string.IsNullOrWhiteSpace(l.VendorName))
            {
                using var existing=Cmd(db,tx,"SELECT id FROM vendors WHERE name=$name ORDER BY id LIMIT 1",("$name",l.VendorName.Trim()));
                vendorId=existing.ExecuteScalar() is long found ? found : Insert(db,tx,"INSERT INTO vendors(name) VALUES($name)",("$name",l.VendorName.Trim()));
            }
            Exec(db,tx,"INSERT INTO work_order_lines(work_order_id,line_no,kind,line_category,code_snapshot,name_snapshot,part_number,unit,qty_scaled,unit_price_minor,discount_minor,total_minor,labor_mode,labor_minutes,hourly_rate_minor,vendor_id,addition_id) VALUES($id,$no,$kind,$cat,$code,$name,$part,$unit,$qty,$price,$discount,$total,$mode,$minutes,$rate,$vendor,$addition)",
                ("$id",id),("$no",i+1),("$kind",legacy),("$cat",l.Category.ToString()),("$code",l.Code),("$name",l.Name),("$part",l.PartNumber),("$unit",l.Unit),("$qty",l.QuantityScaled),("$price",l.UnitPriceMinor),("$discount",l.DiscountMinor),("$total",LineTotal(l)),("$mode",l.LaborMode?.ToString()),("$minutes",l.LaborMinutes),("$rate",l.HourlyRateMinor),("$vendor",vendorId),("$addition",addition));
        }
        var stored=draft with { Id=id, Lines=draft.Lines.Select(l=>l with { AdditionId=l.AdditionId is long old && additions.TryGetValue(old,out var mapped)?mapped:null }).ToArray(), Additions=draft.Additions.Select(a=>a with { Id=a.Id is long old && additions.TryGetValue(old,out var mapped)?mapped:null }).ToArray() };
        var quote=BuildQuote(stored,new(false,false));
        Exec(db,tx,"UPDATE work_orders SET subtotal_minor=$subtotal,total_minor=$total WHERE id=$id",("$subtotal",quote.SubtotalMinor),("$total",quote.TotalMinor),("$id",id));
        Exec(db,tx,"INSERT INTO estimates(work_order_id,version,issued_on,expires_on,customer_snapshot_json,model_json) VALUES($id,$version,$date,$expires,$customer,$model)",("$id",id),("$version",revision),("$date",DS(draft.OrderDate)),("$expires",DS(draft.EstimateExpiresOn)),("$customer",JsonSerializer.Serialize(new {draft.CustomerId,draft.VehicleId})),("$model",JsonSerializer.Serialize(stored,Json)));
        Exec(db,tx,"DELETE FROM app_settings WHERE key=$key",("$key","v02-quote:"+id));
        tx.Commit(); return Task.FromResult(id);
    }
    public Task DeleteDraftWorkOrderAsync(long id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); using var db=OpenV02(); using var tx=db.BeginTransaction();
        if(Scalar(db,tx,"SELECT count(*) FROM work_orders WHERE id=$id AND workflow_status='Estimate'",("$id",id))!=1 || Scalar(db,tx,"SELECT count(*) FROM payments WHERE work_order_id=$id",("$id",id))>0) throw new InvalidOperationException("只可刪未同意且未付款草稿");
        foreach(var table in new[]{"customer_consents","work_order_lines","work_order_additions","estimates","receivables","print_snapshots","work_order_events"}) Exec(db,tx,$"DELETE FROM {table} WHERE work_order_id=$id",("$id",id));
        Exec(db,tx,"DELETE FROM work_orders WHERE id=$id",("$id",id)); tx.Commit(); return Task.CompletedTask;
    }
    public Task TransitionWorkOrderAsync(long id, WorkOrderStatus expected, WorkOrderStatus next, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); WorkOrderTransitions.Move(expected,next); using var db=OpenV02(); using var tx=db.BeginTransaction();
        if(next==WorkOrderStatus.Settled && Scalar(db,tx,"SELECT count(*) FROM receivables WHERE work_order_id=$id AND due_minor=0",("$id",id))!=1) throw new InvalidOperationException("應收未清");
        var legacy=next switch{WorkOrderStatus.AwaitingCheckout=>"Completed",WorkOrderStatus.Cancelled=>"Void",WorkOrderStatus.Accepted=>"Estimate",_=>next.ToString()};
        if(Exec(db,tx,"UPDATE work_orders SET workflow_status=$next,status=$legacy WHERE id=$id AND workflow_status=$expected",("$next",next.ToString()),("$legacy",legacy),("$id",id),("$expected",expected.ToString()))!=1) throw new InvalidOperationException("工單狀態已改變");
        Exec(db,tx,"INSERT INTO work_order_events(work_order_id,action,occurred_at,change_json) VALUES($id,$action,$at,$json)",("$id",id),("$action","Transition"),("$at",DateTimeOffset.UtcNow.ToString("O")),("$json",JsonSerializer.Serialize(new{expected,next})));
        tx.Commit(); return Task.CompletedTask;
    }
    public Task<CatalogPage> SearchCatalogAsync(CatalogSearchQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); if(query.Page<1 || query.PageSize is <1 or >100) throw new ArgumentOutOfRangeException(nameof(query));
        using var db=OpenV02(); var where=" WHERE ($kind IS NULL OR $kind='' OR $kind='全部' OR kind=$kind) AND ($empty=1 OR code LIKE $like ESCAPE '\\' OR name LIKE $like ESCAPE '\\')";
        var cte="WITH catalog AS (SELECT id,'項目' kind,code,name,unit,default_labor_minor price FROM service_items UNION ALL SELECT id,'零件',code,name,coalesce(unit,''),default_price_minor FROM parts)";
        var args=new (string,object?)[]{("$kind",query.Kind),("$empty",string.IsNullOrWhiteSpace(query.Text)?1:0),("$like","%"+Escape(query.Text.Trim())+"%")};
        var total=(int)Scalar(db,null,cte+" SELECT count(*) FROM catalog"+where,args);
        using var cmd=Cmd(db,null,cte+" SELECT id,kind,code,name,unit,price FROM catalog"+where+" ORDER BY CASE WHEN kind='項目' THEN 0 ELSE 1 END,code,id LIMIT $limit OFFSET $offset",args.Concat(new (string,object?)[]{("$limit",query.PageSize),("$offset",checked((query.Page-1)*query.PageSize))}).ToArray());
        var rows=new List<CatalogRow>(); using var r=cmd.ExecuteReader(); while(r.Read()) rows.Add(new(r.GetInt64(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetInt64(5)));
        return Task.FromResult(new CatalogPage(rows,total,query.Page,query.PageSize));
    }
    public async Task<CheckoutQuote> QuoteCheckoutAsync(long workOrderId, CheckoutOptions options, CancellationToken cancellationToken = default)
    {
        var order=await GetWorkOrderAsync(workOrderId,cancellationToken) ?? throw new InvalidOperationException("找不到工單");
        var quote=BuildQuote(order.Draft,options); using var db=OpenV02(); using var tx=db.BeginTransaction();
        if(Scalar(db,tx,"SELECT revision FROM work_orders WHERE id=$id",("$id",workOrderId))!=order.Revision) throw new InvalidOperationException("工單版本已變更，請重新試算");
        var paid=Scalar(db,tx,"SELECT coalesce(sum(amount_minor),0) FROM payments WHERE work_order_id=$id",("$id",workOrderId));
        if(paid>quote.TotalMinor) throw new InvalidOperationException("新計價低於已收款");
        Exec(db,tx,"INSERT INTO receivables(work_order_id,total_minor,paid_minor,due_minor,updated_at) VALUES($id,$total,$paid,$due,$at) ON CONFLICT(work_order_id) DO UPDATE SET total_minor=excluded.total_minor,paid_minor=excluded.paid_minor,due_minor=excluded.due_minor,updated_at=excluded.updated_at",("$id",workOrderId),("$total",quote.TotalMinor),("$paid",paid),("$due",quote.TotalMinor-paid),("$at",DateTimeOffset.UtcNow.ToString("O")));
        Exec(db,tx,"UPDATE work_orders SET subtotal_minor=$subtotal,tax_minor=$tax,total_minor=$total WHERE id=$id",("$subtotal",quote.SubtotalMinor),("$tax",quote.TaxMinor),("$total",quote.TotalMinor),("$id",workOrderId));
        Exec(db,tx,"INSERT INTO app_settings(key,value_json) VALUES($key,$json) ON CONFLICT(key) DO UPDATE SET value_json=excluded.value_json",("$key","v02-quote:"+workOrderId),("$json",JsonSerializer.Serialize(quote,Json)));
        tx.Commit(); return quote;
    }
    public Task<ReceivableResult> RecordPaymentAsync(long workOrderId,string method,long amountMinor,CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested(); if(string.IsNullOrWhiteSpace(method)) throw new ArgumentException("付款方式不可空白");
        using var db=OpenV02(); using var tx=db.BeginTransaction();
        long total,paid;
        using(var cmd=Cmd(db,tx,"SELECT total_minor,paid_minor FROM receivables WHERE work_order_id=$id",("$id",workOrderId)))
        using(var r=cmd.ExecuteReader()){if(!r.Read()) throw new InvalidOperationException("請先結帳試算"); total=r.GetInt64(0);paid=r.GetInt64(1);}
        var due=CheckoutPricing.Remaining(total,paid>0 ? new[]{paid} : [],amountMinor);
        Exec(db,tx,"INSERT INTO payments(work_order_id,paid_on,received_at,method,amount_minor) VALUES($id,$at,$at,$method,$amount)",("$id",workOrderId),("$at",DateTimeOffset.UtcNow.ToString("O")),("$method",method.Trim()),("$amount",amountMinor));
        Exec(db,tx,"UPDATE receivables SET paid_minor=$paid,due_minor=$due,updated_at=$at WHERE work_order_id=$id",("$paid",total-due),("$due",due),("$at",DateTimeOffset.UtcNow.ToString("O")),("$id",workOrderId));
        tx.Commit();return Task.FromResult(new ReceivableResult(workOrderId,total,total-due,due));
    }
    public async Task<PrintSnapshot> CreatePrintSnapshotAsync(long workOrderId,string kind,CancellationToken cancellationToken=default)
    {
        if(kind is not("Estimate" or "WorkOrder" or "Receipt")) throw new ArgumentOutOfRangeException(nameof(kind));
        var order=await GetWorkOrderAsync(workOrderId,cancellationToken) ?? throw new InvalidOperationException("找不到工單");
        var quote=order.LastQuote ?? BuildQuote(order.Draft,new(false,false));
        using var db=OpenV02(); string customer,plate; string? address,vin;
        using(var cmd=Cmd(db,null,"SELECT c.name,c.address,v.plate,v.vin FROM customers c JOIN vehicles v ON v.id=$vehicle WHERE c.id=$customer",("$vehicle",order.Draft.VehicleId),("$customer",order.Draft.CustomerId)))
        using(var r=cmd.ExecuteReader()){if(!r.Read()) throw new InvalidOperationException("車主資料不存在");customer=r.GetString(0);address=T(r,1);plate=r.GetString(2);vin=T(r,3);}
        var lines=order.Draft.Lines.Where(l=>Approved(order.Draft,l)).Select((l,i)=>new WorkOrderLineDocument(i+1,l.Category.ToString(),l.Name,l.Unit,l.QuantityScaled,l.UnitPriceMinor,l.DiscountMinor,LineTotal(l))).ToArray();
        var model=new WorkOrderDocument(order.Draft.Number,order.Status,order.Draft.OrderDate,customer,address,plate,lines,new(quote.SubtotalMinor,quote.DiscountMinor,quote.TaxMinor,quote.TotalMinor),order.Revision,vin,order.Draft.Diagnosis,order.Draft.RepairDescription,order.Draft.EstimateExpiresOn,order.Draft.SignatureLabel,order.Draft.AdditionalWorkNotice);
        var snapshot=new PrintSnapshot(workOrderId,kind,order.Revision,model,DateTimeOffset.UtcNow,quote);
        Exec(db,null,"INSERT INTO print_snapshots(work_order_id,document_kind,revision,model_json,created_at,schema_version,tax_rate,card_rate,tax_rounding_minor,card_rounding_minor) VALUES($id,$kind,$rev,$json,$at,4,$taxrate,$cardrate,$tax,$card)",("$id",workOrderId),("$kind",kind),("$rev",order.Revision),("$json",JsonSerializer.Serialize(snapshot,Json)),("$at",snapshot.CreatedAt.ToString("O")),("$taxrate",quote.TaxRate.ToString(CultureInfo.InvariantCulture)),("$cardrate",quote.CardRate.ToString(CultureInfo.InvariantCulture)),("$tax",quote.TaxMinor),("$card",quote.CardSurchargeMinor));
        return snapshot;
    }
    public virtual Task<ImportDrillResult> DrillImportAsync(IReadOnlyList<string> paths,CancellationToken cancellationToken=default) => throw new NotSupportedException("請使用 ExpertCar.Import 的正式服務");
    public virtual Task<ImportCommitResult> CommitDrilledImportAsync(CancellationToken cancellationToken=default) => throw new NotSupportedException("請使用 ExpertCar.Import 的正式服務");
}

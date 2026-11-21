using ExpertCar.Core;

namespace ExpertCar.Data;

public partial class SqliteV02Service
{
    public Task UpdateVehicleDetailsAsync(VehicleDetailsEditor editor, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var vin = string.IsNullOrWhiteSpace(editor.Vin) ? null : editor.Vin.Trim().ToUpperInvariant();
        if (vin is not null && (vin.Length != 17 || vin.Any(c => !char.IsAsciiLetterOrDigit(c) || c is 'I' or 'O' or 'Q')))
            throw new ArgumentException("VIN 必須為 17 碼英數字，且不含 I、O、Q");
        if (editor.ModelYear is < 1886 or > 2200) throw new ArgumentOutOfRangeException(nameof(editor));
        using var db = OpenV02();
        if (Exec(db, null, "UPDATE vehicles SET vin=$vin,make=$make,model=$model,model_year=$year,color=$color WHERE id=$id",
            ("$vin", vin), ("$make", editor.Make?.Trim()), ("$model", editor.Model?.Trim()),
            ("$year", editor.ModelYear), ("$color", editor.Color?.Trim()), ("$id", editor.VehicleId)) != 1)
            throw new InvalidOperationException("找不到車輛");
        return Task.CompletedTask;
    }

    public Task TransferVehicleAsync(long vehicleId, long newCustomerId, DateOnly effectiveOn, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); using var db = OpenV02(); using var tx = db.BeginTransaction();
        if (Scalar(db, tx, "SELECT count(*) FROM customers WHERE id=$id", ("$id", newCustomerId)) != 1)
            throw new ArgumentException("找不到新車主");
        long oldCustomer;
        using (var cmd = Cmd(db, tx, "SELECT current_customer_id FROM vehicles WHERE id=$id", ("$id", vehicleId)))
            oldCustomer = cmd.ExecuteScalar() is long id ? id : throw new ArgumentException("找不到車輛");
        if (oldCustomer == newCustomerId) throw new ArgumentException("新舊車主相同");
        if (effectiveOn <= new DateOnly(1900, 1, 1)) throw new ArgumentException("過戶日期須晚於 1900-01-01");
        var date = DS(effectiveOn)!;
        if (Scalar(db, tx, "SELECT count(*) FROM vehicle_ownerships WHERE vehicle_id=$id AND end_date IS NULL", ("$id", vehicleId)) == 0)
            Exec(db, tx, "INSERT INTO vehicle_ownerships(vehicle_id,customer_id,start_date,source_note) VALUES($vehicle,$customer,'1900-01-01','原車主起日待確認')",
                ("$vehicle", vehicleId), ("$customer", oldCustomer));
        using (var cmd = Cmd(db, tx, "SELECT start_date FROM vehicle_ownerships WHERE vehicle_id=$id AND end_date IS NULL", ("$id", vehicleId)))
        {
            var start = cmd.ExecuteScalar() as string;
            if (start is not null && D(start) >= effectiveOn) throw new ArgumentException("過戶日期須晚於原車主起日");
        }
        Exec(db, tx, "UPDATE vehicle_ownerships SET end_date=$end WHERE vehicle_id=$id AND end_date IS NULL", ("$end", DS(effectiveOn.AddDays(-1))), ("$id", vehicleId));
        Exec(db, tx, "INSERT INTO vehicle_ownerships(vehicle_id,customer_id,start_date,source_note) VALUES($vehicle,$customer,$date,'店家手動過戶')",
            ("$vehicle", vehicleId), ("$customer", newCustomerId), ("$date", date));
        Exec(db, tx, "UPDATE vehicles SET current_customer_id=$customer WHERE id=$id", ("$customer", newCustomerId), ("$id", vehicleId));
        tx.Commit(); return Task.CompletedTask;
    }

    public Task<WorkOrderPage> SearchWorkOrdersAsync(int page = 1, int pageSize = 30, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (page < 1 || pageSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(page));
        using var db = OpenV02();
        var total = checked((int)Scalar(db, null, "SELECT count(*) FROM work_orders"));
        using var cmd = Cmd(db, null, "SELECT w.id,w.number,w.workflow_status,w.order_date,w.total_minor,coalesce(r.due_minor,w.total_minor),v.plate,w.promised_delivery_on FROM work_orders w JOIN vehicles v ON v.id=w.vehicle_id LEFT JOIN receivables r ON r.work_order_id=w.id ORDER BY w.order_date DESC,w.id DESC LIMIT $limit OFFSET $offset",
            ("$limit", pageSize), ("$offset", checked((page - 1) * pageSize)));
        var rows = new List<WorkOrderListRow>(); using var reader = cmd.ExecuteReader();
        while (reader.Read()) rows.Add(new(new(reader.GetInt64(0), reader.GetString(1), Enum.Parse<WorkOrderStatus>(reader.GetString(2)),
            D(reader.GetString(3)), reader.GetInt64(4), reader.GetInt64(5)), reader.GetString(6), DN(T(reader, 7))));
        return Task.FromResult(new WorkOrderPage(rows, total, page, pageSize));
    }

    public Task<ReceivableResult?> GetReceivableAsync(long workOrderId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); using var db = OpenV02();
        using var cmd = Cmd(db, null, "SELECT total_minor,paid_minor,due_minor FROM receivables WHERE work_order_id=$id", ("$id", workOrderId));
        using var reader = cmd.ExecuteReader();
        return Task.FromResult<ReceivableResult?>(reader.Read() ? new(workOrderId, reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2)) : null);
    }

    public Task<IReadOnlyList<WorkOrderListRow>> GetTodayDeliveryOrdersAsync(DateOnly today, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); using var db = OpenV02();
        using var cmd = Cmd(db, null, "SELECT w.id,w.number,w.workflow_status,w.order_date,w.total_minor,coalesce(r.due_minor,w.total_minor),v.plate FROM work_orders w JOIN vehicles v ON v.id=w.vehicle_id LEFT JOIN receivables r ON r.work_order_id=w.id WHERE w.workflow_status='AwaitingCheckout' AND w.promised_delivery_on=$today ORDER BY w.id",
            ("$today", DS(today)));
        var rows = new List<WorkOrderListRow>(); using var reader = cmd.ExecuteReader();
        while (reader.Read()) rows.Add(new(new(reader.GetInt64(0), reader.GetString(1), Enum.Parse<WorkOrderStatus>(reader.GetString(2)),
            D(reader.GetString(3)), reader.GetInt64(4), reader.GetInt64(5)), reader.GetString(6), today));
        return Task.FromResult<IReadOnlyList<WorkOrderListRow>>(rows);
    }

    public Task<IReadOnlyList<long>> GetSuggestedPartIdsAsync(long serviceItemId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); using var db = OpenV02();
        using var cmd = Cmd(db, null, "SELECT part_id FROM service_parts WHERE service_id=$id AND default_use=1 ORDER BY line_no", ("$id", serviceItemId));
        var ids = new List<long>(); using var reader = cmd.ExecuteReader(); while (reader.Read()) ids.Add(reader.GetInt64(0));
        return Task.FromResult<IReadOnlyList<long>>(ids);
    }

    public Task SaveSuggestedPartIdsAsync(long serviceItemId, IReadOnlyList<long> partIds, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); using var db = OpenV02(); using var tx = db.BeginTransaction();
        if (Scalar(db, tx, "SELECT count(*) FROM service_items WHERE id=$id", ("$id", serviceItemId)) != 1)
            throw new ArgumentException("找不到目錄項目");
        Exec(db, tx, "DELETE FROM service_parts WHERE service_id=$id", ("$id", serviceItemId));
        var line = 0;
        foreach (var id in partIds.Distinct())
        {
            using var price = Cmd(db, tx, "SELECT default_price_minor FROM parts WHERE id=$id", ("$id", id));
            if (price.ExecuteScalar() is not long amount) throw new ArgumentException("找不到建議零件");
            Exec(db, tx, "INSERT INTO service_parts(service_id,part_id,line_no,qty_scaled,unit_price_minor,default_use) VALUES($service,$part,$line,1000,$price,1)",
                ("$service", serviceItemId), ("$part", id), ("$line", ++line), ("$price", amount));
        }
        tx.Commit(); return Task.CompletedTask;
    }
}

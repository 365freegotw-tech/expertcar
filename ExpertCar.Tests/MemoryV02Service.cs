using ExpertCar.Core;

namespace ExpertCar.Data;

/// <summary>供 WPF 先接畫面的記憶體假實作；資料關閉程式即消失。</summary>
public sealed class MemoryV02Service : IExpertCarV02Service
{
    private readonly Dictionary<long, WorkOrderDetail> _orders = [];
    private readonly Dictionary<long, VehicleHistory> _vehicles = [];
    private readonly List<CatalogRow> _catalog = [];
    private readonly Dictionary<long, long[]> _suggestedParts = [];
    private long _nextOrderId = 1;
    private long _nextPaymentId = 1;

    public MemoryV02Service(IEnumerable<VehicleHistory>? vehicles = null, IEnumerable<CatalogRow>? catalog = null)
    {
        foreach (var vehicle in vehicles ?? []) _vehicles[vehicle.Vehicle.VehicleId] = vehicle;
        _catalog.AddRange(catalog ?? []);
    }

    public Task<IReadOnlyList<VehicleQuickResult>> QuickFindVehiclesAsync(string query, int limit = 20, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        query = query.Trim();
        var key = new string(query.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        return Task.FromResult<IReadOnlyList<VehicleQuickResult>>(_vehicles.Values.Select(v => v.Vehicle)
            .Where(v => query.Length == 0 || v.CustomerName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                new string(v.Plate.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray()).Contains(key, StringComparison.Ordinal))
            .Take(limit).ToArray());
    }

    public Task<VehicleHistory?> GetVehicleHistoryAsync(long vehicleId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_vehicles.GetValueOrDefault(vehicleId));
    }

    public Task<WorkOrderDetail?> GetWorkOrderAsync(long id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_orders.GetValueOrDefault(id));
    }
    public Task<WorkOrderPage> SearchWorkOrdersAsync(int page = 1, int pageSize = 30, CancellationToken cancellationToken = default)
    {
        var rows = _orders.Values.OrderByDescending(o => o.Draft.OrderDate).ThenByDescending(o => o.Id)
            .Select(o => new WorkOrderListRow(new(o.Id, o.Draft.Number, o.Status, o.Draft.OrderDate,
                o.LastQuote?.TotalMinor ?? 0, (o.LastQuote?.TotalMinor ?? 0) - o.Payments.Sum(p => p.AmountMinor)),
                _vehicles.GetValueOrDefault(o.Draft.VehicleId)?.Vehicle.Plate ?? "", o.Draft.PromisedDeliveryOn)).ToArray();
        return Task.FromResult(new WorkOrderPage(rows.Skip((page - 1) * pageSize).Take(pageSize).ToArray(), rows.Length, page, pageSize));
    }
    public Task<ReceivableResult?> GetReceivableAsync(long id, CancellationToken cancellationToken = default)
    {
        var o = RequireOrder(id); var total = o.LastQuote?.TotalMinor;
        var paid = o.Payments.Sum(p => p.AmountMinor);
        return Task.FromResult<ReceivableResult?>(total is null ? null : new(id, total.Value, paid, total.Value - paid));
    }
    public Task<IReadOnlyList<WorkOrderListRow>> GetTodayDeliveryOrdersAsync(DateOnly today, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<WorkOrderListRow>>(_orders.Values
            .Where(o => o.Status == WorkOrderStatus.AwaitingCheckout && o.Draft.PromisedDeliveryOn == today)
            .Select(o => new WorkOrderListRow(new(o.Id, o.Draft.Number, o.Status, o.Draft.OrderDate,
                o.LastQuote?.TotalMinor ?? 0, (o.LastQuote?.TotalMinor ?? 0) - o.Payments.Sum(p => p.AmountMinor)),
                _vehicles.GetValueOrDefault(o.Draft.VehicleId)?.Vehicle.Plate ?? "", today)).ToArray());
    public Task<IReadOnlyList<long>> GetSuggestedPartIdsAsync(long id, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<long>>(_suggestedParts.GetValueOrDefault(id) ?? []);
    public Task SaveSuggestedPartIdsAsync(long id, IReadOnlyList<long> partIds, CancellationToken cancellationToken = default)
    { _suggestedParts[id] = partIds.Distinct().ToArray(); return Task.CompletedTask; }

    public Task<long> SaveWorkOrderAsync(WorkOrderDraft draft, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(draft.Number) || draft.CustomerId <= 0 || draft.VehicleId <= 0)
            throw new ArgumentException("單號、客戶與車輛不可空白");
        if (draft.Lines.Any(line => line.QuantityScaled < 0 || line.UnitPriceMinor < 0 || line.DiscountMinor < 0))
            throw new ArgumentException("行項金額或數量不可為負");
        var id = draft.Id ?? _nextOrderId++;
        if (draft.Id is not null && !_orders.ContainsKey(id)) throw new InvalidOperationException("找不到工單");
        if (_orders.Values.Any(order => order.Id != id && order.Draft.Number == draft.Number))
            throw new InvalidOperationException("單號已存在");
        var old = _orders.GetValueOrDefault(id);
        if (old is not null && old.Payments.Count > 0) throw new InvalidOperationException("已有收款的工單不可直接改價");
        _orders[id] = new(id, draft with { Id = id }, old?.Status ?? WorkOrderStatus.Estimate,
            (old?.Revision ?? 0) + 1, old?.Payments ?? [], null);
        return Task.FromResult(id);
    }

    public Task TransitionWorkOrderAsync(long id, WorkOrderStatus expected, WorkOrderStatus next, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var order = RequireOrder(id);
        if (order.Status != expected) throw new InvalidOperationException("工單狀態已改變");
        WorkOrderTransitions.Move(expected, next);
        if (next == WorkOrderStatus.Settled && (order.LastQuote is null ||
            order.Payments.Sum(payment => payment.AmountMinor) != order.LastQuote.TotalMinor))
            throw new InvalidOperationException("應收尚未付清");
        _orders[id] = order with { Status = next };
        return Task.CompletedTask;
    }

    public Task DeleteDraftWorkOrderAsync(long id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var order = RequireOrder(id);
        if (order.Status != WorkOrderStatus.Estimate || order.Payments.Count > 0)
            throw new InvalidOperationException("只可刪除未同意且未收款的草稿");
        _orders.Remove(id);
        return Task.CompletedTask;
    }

    public Task<CatalogPage> SearchCatalogAsync(CatalogSearchQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (query.Page < 1 || query.PageSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(query));
        var rows = _catalog.Where(item => (string.IsNullOrWhiteSpace(query.Kind) || item.Kind == query.Kind) &&
            (string.IsNullOrWhiteSpace(query.Text) || item.Code.Contains(query.Text, StringComparison.OrdinalIgnoreCase) ||
             item.Name.Contains(query.Text, StringComparison.OrdinalIgnoreCase))).ToArray();
        return Task.FromResult(new CatalogPage(rows.Skip(checked((query.Page - 1) * query.PageSize)).Take(query.PageSize).ToArray(),
            rows.Length, query.Page, query.PageSize));
    }

    public Task<CheckoutQuote> QuoteCheckoutAsync(long workOrderId, CheckoutOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var order = RequireOrder(workOrderId);
        var quote = BuildQuote(order.Draft, options);
        if (order.Payments.Sum(payment => payment.AmountMinor) > quote.TotalMinor)
            throw new InvalidOperationException("新計價低於已收款");
        _orders[workOrderId] = order with { LastQuote = quote };
        return Task.FromResult(quote);
    }

    public Task<ReceivableResult> RecordPaymentAsync(long workOrderId, string method, long amountMinor, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(method)) throw new ArgumentException("請選擇付款方式", nameof(method));
        var order = RequireOrder(workOrderId);
        var quote = order.LastQuote ?? throw new InvalidOperationException("請先結帳試算");
        var due = CheckoutPricing.Remaining(quote.TotalMinor, order.Payments.Select(payment => payment.AmountMinor), amountMinor);
        var entry = new PaymentEntry(_nextPaymentId++, workOrderId, method, amountMinor, DateTimeOffset.Now, null);
        _orders[workOrderId] = order with { Payments = [.. order.Payments, entry] };
        return Task.FromResult(new ReceivableResult(workOrderId, quote.TotalMinor, quote.TotalMinor - due, due));
    }

    public Task<PrintSnapshot> CreatePrintSnapshotAsync(long workOrderId, string kind, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (kind is not ("Estimate" or "WorkOrder" or "Receipt")) throw new ArgumentOutOfRangeException(nameof(kind));
        var order = RequireOrder(workOrderId);
        var quote = order.LastQuote ?? BuildQuote(order.Draft, new CheckoutOptions(false, false));
        var lines = order.Draft.Lines.Where(line => Approved(order.Draft, line)).Select((line, index) =>
            new WorkOrderLineDocument(index + 1, line.Category.ToString(), line.Name, line.Unit,
                line.QuantityScaled, line.UnitPriceMinor, line.DiscountMinor, LineAmount(line))).ToArray();
        var vehicle = _vehicles.GetValueOrDefault(order.Draft.VehicleId)?.Vehicle;
        var model = new WorkOrderDocument(order.Draft.Number, order.Status, order.Draft.OrderDate,
            vehicle?.CustomerName ?? "", null, vehicle?.Plate ?? "", lines,
            new OrderTotals(quote.SubtotalMinor, quote.DiscountMinor, quote.TaxMinor, quote.TotalMinor), order.Revision,
            vehicle?.Vin, order.Draft.Diagnosis, order.Draft.RepairDescription, order.Draft.EstimateExpiresOn,
            order.Draft.SignatureLabel, order.Draft.AdditionalWorkNotice);
        return Task.FromResult(new PrintSnapshot(workOrderId, kind, order.Revision, model, DateTimeOffset.Now, quote));
    }

    public Task<ImportDrillResult> DrillImportAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ImportDrillResult(new ImportPreview(0, 0, 0, 0, [], false),
            paths.Select(path => new ImportSourceCount(path, "", 0, 0, 0, 0, 0)).ToArray()));
    }

    public Task<ImportCommitResult> CommitDrilledImportAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("記憶體假實作不提交匯入；正式流程使用 ImportStorageService");

    private WorkOrderDetail RequireOrder(long id) => _orders.GetValueOrDefault(id) ?? throw new InvalidOperationException("找不到工單");
    private static bool Approved(WorkOrderDraft draft, WorkLineDraft line) => line.AdditionId is null ||
        draft.Additions.Any(addition => addition.Id == line.AdditionId && addition.Accepted);
    private static long LineAmount(WorkLineDraft line)
    {
        if (line.LaborMode != LaborPriceMode.Hourly)
            return Pricing.LineTotal(line.QuantityScaled, line.UnitPriceMinor, line.DiscountMinor);
        if (line.LaborMinutes is null or < 0 || line.HourlyRateMinor is null or < 0)
            throw new ArgumentException("請輸入有效的工時分鐘與費率");
        var gross = Pricing.RoundMinor((decimal)line.LaborMinutes.Value * line.HourlyRateMinor.Value / 60);
        if (line.DiscountMinor > gross) throw new ArgumentOutOfRangeException(nameof(line));
        return gross - line.DiscountMinor;
    }
    private static CheckoutQuote BuildQuote(WorkOrderDraft draft, CheckoutOptions options)
    {
        var lines = draft.Lines.Where(line => line.Category != WorkLineCategory.Discount)
            .Select(line => new ChargeLine(LineAmount(line), line.AdditionId is not null, Approved(draft, line)));
        var discount = checked(draft.OrderDiscountMinor + draft.Lines
            .Where(line => line.Category == WorkLineCategory.Discount && Approved(draft, line)).Sum(LineAmount));
        return CheckoutPricing.Calculate(lines, discount, options);
    }
}

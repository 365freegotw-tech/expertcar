using System.Text.Json.Serialization;
using ExpertCar.Core;

namespace ExpertCar.App.Presentation;

[JsonConverter(typeof(JsonStringEnumConverter<PrintOrientation>))]
public enum PrintOrientation { Portrait, Landscape }
public enum PrintRowKind { Text, TableHeader, Detail, Signature }
public sealed record PrintRow(string Text, string Quantity = "", string UnitPrice = "", string Amount = "",
    bool IsHeading = false, string Category = "", PrintRowKind Kind = PrintRowKind.Text);
public sealed record PrintVehicleContext(string Description, string Odometer);
public sealed record PrintPage(int Number, int TotalPages, IReadOnlyList<PrintRow> Rows)
{
    public string PageLabel => $"第 {Number}／{TotalPages} 頁";
}

/// <summary>A4 的 DIP 尺寸與固定行高由模型和 WPF 共用，避免預覽與分頁各自計算。</summary>
public sealed record PrintLayout(PrintOrientation Orientation)
{
    public const double Margin = 36, HeaderTop = 60, FooterHeight = 24;
    public double PageWidth => Orientation == PrintOrientation.Landscape ? 1122.52 : 793.70;
    public double PageHeight => Orientation == PrintOrientation.Landscape ? 793.70 : 1122.52;
    public double ContentWidth => PageWidth - Margin * 2;
    public double RowHeight => Orientation == PrintOrientation.Landscape ? 24 : 22;
    public double FontSize => Orientation == PrintOrientation.Landscape ? 14 : 12;
    public IReadOnlyList<double> ColumnWidths => [ContentWidth * .10, ContentWidth * .43,
        ContentWidth * .11, ContentWidth * .17, ContentWidth * .19];
    public int TextWidth => (int)((ContentWidth - 8) / FontSize);
    public double BodyTop(int headerRows) => HeaderTop + headerRows * RowHeight + 8;
    public int BodyRowCapacity(int headerRows) => (int)((PageHeight - Margin - FooterHeight - BodyTop(headerRows)) / RowHeight);
}

/// <summary>顯示服務快照中的行項與結帳金額；僅彙整分類小計，不重新計價。</summary>
public sealed record PrintDocumentModel(string Title, string ShopHeader, string Contact, string Number,
    IReadOnlyList<PrintPage> Pages, CheckoutQuote Checkout, string SignatureLabel, PrintLayout Layout,
    IReadOnlyList<PrintRow> HeaderRows)
{
    public int RowsPerPage => Layout.BodyRowCapacity(HeaderRows.Count);
    public static PrintDocumentModel Build(PrintSnapshot snapshot, string header, string contact, WorkOrderDraft draft,
        IReadOnlyList<PaymentEntry> payments, string paidText, string dueText, PrintVehicleContext? vehicleContext = null,
        PrintOrientation orientation = PrintOrientation.Portrait, string shopAddress = "", string shopTaxId = "")
    {
        if (!Enum.IsDefined(orientation)) throw new ArgumentException("請選擇 A4 直式或 A4 橫式");
        var quote = snapshot.Checkout ?? throw new InvalidOperationException("缺少結帳快照，無法列印；請由服務重新產生");
        if (draft.Id != snapshot.WorkOrderId || draft.Number != snapshot.Model.Number)
            throw new InvalidOperationException("列印快照與工單不一致，請重新預覽");
        var title = snapshot.DocumentKind switch { "Estimate" => "估價單", "WorkOrder" => "維修單", "Receipt" => "收據", _ => throw new ArgumentException("未知單據類型") };
        var model = snapshot.Model;
        var layout = new PrintLayout(orientation);
        var headerRows = new List<PrintRow>();
        void Header(string text) => headerRows.AddRange(Wrap(text, layout.TextWidth).Select(part => new PrintRow(part)));
        Header($"店名：{header}"); Header($"地址：{shopAddress}");
        Header($"電話：{contact}　統編：{shopTaxId}");
        Header($"單號：{model.Number}　日期：{model.OrderDate:yyyy/MM/dd}");
        var capacity = layout.BodyRowCapacity(headerRows.Count);
        if (capacity < 6) throw new InvalidOperationException("店家抬頭過長，請縮短店名、地址或聯絡方式後重新預覽");
        List<List<PrintRow>> pages = [[]];
        var tableHeader = new PrintRow("品名／料號", "數量", "單價（元）", "小計（元）", true, "類別", PrintRowKind.TableHeader);
        var tableStarted = false;
        void NextPage() => pages.Add(tableStarted ? [tableHeader] : []);
        void Append(PrintRow row)
        {
            if (pages[^1].Count == capacity) NextPage();
            pages[^1].Add(row);
        }
        void Text(string text, bool heading = false)
        { foreach (var part in Wrap(text, layout.TextWidth)) Append(new(part, IsHeading: heading)); }
        Text($"客戶：{model.CustomerName}　車牌：{model.Plate}");
        if (!string.IsNullOrWhiteSpace(model.CustomerAddress)) Text($"客戶地址：{model.CustomerAddress}");
        Text($"廠牌／車型／年份：{vehicleContext?.Description ?? "待確認"}");
        Text($"里程（目前資料）：{vehicleContext?.Odometer ?? "待確認"}");
        Text($"VIN：{model.Vin ?? "待確認（17 碼）"}　狀態：{UiText.Status(model.Status)}　修訂：{snapshot.Revision}");
        Text($"診斷結果：{model.Diagnosis ?? "未填寫"}");
        Text($"維修項目說明：{model.RepairDescription ?? "未填寫"}");
        // 首頁表頭放在車籍／說明之後；所有續頁在頁首重印。
        tableStarted = true;
        if (capacity - pages[^1].Count < 2) NextPage(); else Append(tableHeader);
        var approved = draft.Lines.Where(l => l.AdditionId is null || draft.Additions.Any(a => a.Id == l.AdditionId && a.Accepted)).ToArray();
        if (approved.Length != model.Lines.Count) throw new InvalidOperationException("施工明細與列印快照不一致，請重新產生");
        for (var i = 0; i < model.Lines.Count; i++)
        {
            var line = model.Lines[i]; var detail = approved[i];
            var description = line.Name;
            if (!string.IsNullOrWhiteSpace(detail.VendorName)) description += $"　廠商：{detail.VendorName}";
            if (!string.IsNullOrWhiteSpace(detail.PartNumber)) description += $"　料號：{detail.PartNumber}";
            var quantity = $"{line.QuantityScaled / 1000m:0.###} {line.Unit}";
            var price = line.UnitPriceMinor;
            if (detail.LaborMode == LaborPriceMode.Hourly)
            {
                var hours = (detail.LaborMinutes ?? 0) / 60m;
                price = detail.HourlyRateMinor ?? 0;
                description += $"　工時：{hours:0.####} 小時 × {price / 100m:0.##}";
                quantity = $"{hours:0.####} 小時";
            }
            var values = new[] { UiText.Category(detail.Category), description, quantity, UiText.Amount(price), UiText.Amount(line.TotalMinor) };
            var fields = values.Select((value, column) => Wrap(value,
                Math.Max(1, (int)((layout.ColumnWidths[column] - 8) / layout.FontSize))).ToArray()).ToArray();
            var count = fields.Max(f => f.Length);
            // 一般明細整組移至下一頁；單項超長時仍可跨頁，所有文字均保留。
            if (count <= capacity - 1 && pages[^1].Count + count > capacity) NextPage();
            for (var row = 0; row < count; row++)
            {
                string Cell(int column) => row < fields[column].Length ? fields[column][row] : "";
                Append(new(Cell(1), Cell(2), Cell(3), Cell(4), Category: Cell(0), Kind: PrintRowKind.Detail));
            }
        }
        foreach (var addition in draft.Additions)
            Text($"加修 #{addition.Id}：{addition.Title}；原因：{addition.Reason}；{(addition.Accepted ? $"已同意／{addition.CustomerName}／{addition.ConsentMethod}／{addition.AcceptedAt?.ToOffset(TimeSpan.FromHours(8)):yyyy/MM/dd HH:mm zzz}" : "未同意，不併入結帳")}");
        // 分類金額直接加總服務列印快照的 TotalMinor，不使用草稿重新算價。
        foreach (var category in new[] { WorkLineCategory.Labor, WorkLineCategory.Part, WorkLineCategory.Consumable, WorkLineCategory.Disposal, WorkLineCategory.Outsource })
        {
            var subtotal = model.Lines.Where((_, index) => approved[index].Category == category).Sum(l => l.TotalMinor);
            Text($"{UiText.Category(category)}小計：{UiText.Money(subtotal)}");
        }
        Text($"費用小計：{UiText.Money(quote.SubtotalMinor)}　折扣：{UiText.Money(quote.DiscountMinor)}");
        Text($"折後現金基價：{UiText.Money(quote.BaseMinor)}　稅額（{quote.TaxRate:P0}）：{UiText.Money(quote.TaxMinor)}");
        if (quote.CardSurchargeApplied) Text($"刷卡加成（{quote.CardRate:P2}）：{UiText.Money(quote.CardSurchargeMinor)}");
        Text($"應收總額：{UiText.Money(quote.TotalMinor)}", true);
        if (snapshot.DocumentKind == "Receipt")
        {
            foreach (var payment in payments)
                Text($"收款：{payment.ReceivedAt.ToOffset(TimeSpan.FromHours(8)):yyyy/MM/dd HH:mm}　{payment.Method}　{UiText.Money(payment.AmountMinor)}");
            Text($"已收：{paidText}　未收：{dueText}");
        }
        Text($"注意事項：{model.AdditionalWorkNotice ?? "施工中可能發現額外項目，未經同意不施工"}");
        if (snapshot.DocumentKind == "Estimate") Text($"估價有效期限：{model.EstimateExpiresOn?.ToString("yyyy/MM/dd") ?? "待確認"}");
        Text("金額依本次結帳快照；發票由店家在外部系統開立。此單非統一發票。");
        var signatureLabel = model.SignatureLabel ?? "客人授權簽名";
        var signatures = Wrap($"{signatureLabel}：________________　日期：____________", layout.TextWidth)
            .Concat(Wrap("技師：________________　接待：________________", layout.TextWidth))
            .Select(part => new PrintRow(part, Kind: PrintRowKind.Signature)).ToArray();
        if (signatures.Length > capacity - 1) throw new InvalidOperationException("簽名欄標題過長，請縮短後重新預覽");
        if (pages[^1].Count + signatures.Length > capacity) NextPage();
        pages[^1].AddRange(signatures);
        return new(title, header, contact, model.Number,
            pages.Select((rows, index) => new PrintPage(index + 1, pages.Count, rows)).ToArray(), quote,
            signatureLabel, layout, headerRows);
    }
    public static IEnumerable<string> Wrap(string text, int width)
    {
        // 保留明確換行；Rune 避免切斷代理字元。以中文字寬保守估計。
        foreach (var paragraph in text.Replace("\r", "").Split('\n'))
        {
            var runes = paragraph.EnumerateRunes().ToArray();
            if (runes.Length == 0) { yield return ""; continue; }
            foreach (var chunk in runes.Chunk(width)) yield return string.Concat(chunk.Select(r => r.ToString()));
        }
    }
}

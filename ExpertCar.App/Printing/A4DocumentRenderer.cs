using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using ExpertCar.App.Presentation;

namespace ExpertCar.App.Printing;

public static class A4DocumentRenderer
{
    public static FixedDocument Render(PrintDocumentModel model)
    {
        var layout = model.Layout;
        var document = new FixedDocument();
        document.DocumentPaginator.PageSize = new(layout.PageWidth, layout.PageHeight);
        foreach (var page in model.Pages)
        {
            var sheet = new FixedPage { Width = layout.PageWidth, Height = layout.PageHeight, Background = Brushes.White };
            Add(sheet, model.Title, PrintLayout.Margin, 24, layout.ContentWidth, 30, 22, true);
            var y = PrintLayout.HeaderTop;
            foreach (var header in model.HeaderRows)
            {
                Add(sheet, header.Text, PrintLayout.Margin + 4, y, layout.ContentWidth - 8, layout.RowHeight, layout.FontSize);
                y += layout.RowHeight;
            }
            y = layout.BodyTop(model.HeaderRows.Count);
            foreach (var row in page.Rows)
            {
                if (row.IsHeading)
                {
                    var background = new Border { Background = new SolidColorBrush(Color.FromRgb(235, 241, 247)),
                        Width = layout.ContentWidth, Height = layout.RowHeight };
                    FixedPage.SetLeft(background, PrintLayout.Margin); FixedPage.SetTop(background, y); sheet.Children.Add(background);
                }
                if (row.Kind is PrintRowKind.TableHeader or PrintRowKind.Detail)
                {
                    var cells = new[] { row.Category, row.Text, row.Quantity, row.UnitPrice, row.Amount };
                    var x = PrintLayout.Margin;
                    for (var column = 0; column < cells.Length; column++)
                    {
                        var width = layout.ColumnWidths[column];
                        Add(sheet, cells[column], x + 4, y, width - 8, layout.RowHeight, layout.FontSize,
                            row.IsHeading || column == 4);
                        x += width;
                    }
                }
                else
                    Add(sheet, row.Text, PrintLayout.Margin + 4, y, layout.ContentWidth - 8, layout.RowHeight, layout.FontSize, row.IsHeading);
                y += layout.RowHeight;
            }
            // 簽名已作為末頁內容由模型排版；頁尾只負責頁碼。
            Add(sheet, page.PageLabel, PrintLayout.Margin, layout.PageHeight - PrintLayout.Margin - PrintLayout.FooterHeight,
                layout.ContentWidth, PrintLayout.FooterHeight, 12);
            var content = new PageContent(); ((System.Windows.Markup.IAddChild)content).AddChild(sheet); document.Pages.Add(content);
        }
        return document;
    }
    private static void Add(FixedPage page, string text, double left, double top, double width, double height, double size, bool bold = false)
    {
        var block = new TextBlock { Text = text, Width = width, Height = height, FontSize = size,
            FontFamily = new("Microsoft JhengHei"), FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
            Foreground = Brushes.Black };
        FixedPage.SetLeft(block, left); FixedPage.SetTop(block, top); page.Children.Add(block);
    }
}

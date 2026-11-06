using System.Globalization;
using ClosedXML.Excel;
using EnterprisePulse.Core.Metadata;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace EnterprisePulse.Core.Resources;

public sealed record ExportFile(byte[] Content, string ContentType, string FileName);

public sealed class ExportService(MetadataStore meta)
{
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    static ExportService() => QuestPDF.Settings.License = LicenseType.Community;

    public string Display(FieldMeta f, object? value, Dictionary<string, Dictionary<string, string>> labels, string locale)
    {
        if (value == null) return "";
        if (f.Ref != null && labels.TryGetValue(f.Name, out var map) && map.TryGetValue(value.ToString()!, out var label)) return label;
        return value switch
        {
            DateOnly d => d.ToString("dd.MM.yyyy"),
            DateTime dt => dt.ToLocalTime().ToString("dd.MM.yyyy HH:mm"),
            decimal m => m.ToString(f.Scale is > 2 ? "N" + f.Scale : "N2", Tr),
            bool b => b ? (locale == "en" ? "Yes" : "Evet") : (locale == "en" ? "No" : "Hayır"),
            Enum e when f.Enum != null && meta.Document.Enums.TryGetValue(f.Enum, out var labelsByLocale)
                => labelsByLocale.GetValueOrDefault(locale == "en" ? "en" : "tr")?.GetValueOrDefault(e.ToString()) ?? e.ToString(),
            _ => value.ToString() ?? "",
        };
    }

    public ExportFile Export(ResourceMeta r, List<object> rows, Dictionary<string, Dictionary<string, string>> labels, string format, string locale)
    {
        var fields = r.Fields.Where(f => !f.Hidden && f.Kind != "json" && f.Name is not ("id" or "companyId" or "deletedAt" or "updatedAt")).ToList();
        var headers = fields.Select(f => f.Label.For(locale)).ToList();
        var data = rows.Select(row => fields.Select(f => Display(f, f.Property.GetValue(row), labels, locale)).ToList()).ToList();
        return Table(r.Label.For(locale), r.Resource, headers, data, format);
    }

    public ExportFile Table(string title, string fileBase, List<string> headers, List<List<string>> rows, string format)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmm");
        if (format == "pdf") return new ExportFile(Pdf(title, headers, rows), "application/pdf", $"{fileBase}-{stamp}.pdf");

        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(title.Length > 31 ? title[..31] : title);
        for (var c = 0; c < headers.Count; c++)
        {
            sheet.Cell(1, c + 1).Value = headers[c];
            sheet.Cell(1, c + 1).Style.Font.Bold = true;
            sheet.Cell(1, c + 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#EEF2FF");
        }
        for (var r = 0; r < rows.Count; r++)
        {
            for (var c = 0; c < rows[r].Count; c++) sheet.Cell(r + 2, c + 1).Value = rows[r][c];
        }
        sheet.SheetView.FreezeRows(1);
        sheet.Columns().AdjustToContents(1, Math.Min(rows.Count + 1, 200));
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return new ExportFile(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"{fileBase}-{stamp}.xlsx");
    }

    private static byte[] Pdf(string title, List<string> headers, List<List<string>> rows) =>
        Document.Create(doc => doc.Page(page =>
        {
            page.Size(headers.Count > 6 ? PageSizes.A4.Landscape() : PageSizes.A4);
            page.Margin(24);
            page.DefaultTextStyle(t => t.FontSize(headers.Count > 10 ? 6.5f : 8));
            page.Header().Row(row =>
            {
                row.RelativeItem().Text(title).SemiBold().FontSize(13).FontColor(Colors.Indigo.Darken2);
                row.AutoItem().AlignRight().Text(DateTime.Now.ToString("dd.MM.yyyy HH:mm")).FontColor(Colors.Grey.Darken1);
            });
            page.Content().PaddingTop(8).Table(table =>
            {
                table.ColumnsDefinition(c =>
                {
                    foreach (var _ in headers) c.RelativeColumn();
                });
                table.Header(h =>
                {
                    foreach (var header in headers)
                        h.Cell().Background(Colors.Indigo.Lighten5).Padding(3).Text(header).SemiBold();
                });
                foreach (var row in rows)
                    foreach (var cell in row)
                        table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(cell);
            });
            page.Footer().AlignRight().Text(t =>
            {
                t.Span("Sayfa ");
                t.CurrentPageNumber();
                t.Span(" / ");
                t.TotalPages();
            });
        })).GeneratePdf();
}

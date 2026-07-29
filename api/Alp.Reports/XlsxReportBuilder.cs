using System.Globalization;
using ClosedXML.Excel;

namespace Alp.Reports;

// docs/uyelik-ve-rapor-plani.md §5.4. PDF'te yan yana duran şematik/grafik
// burada yok — SVG basılmaz; grafiğin ham verisi (§5.1'deki `chart.table`)
// sütun olarak girer, kullanıcı kendi grafiğini çizebilsin diye.
public class XlsxReportBuilder
{
    public byte[] Build(ReportPayload payload)
    {
        using var wb = new XLWorkbook();

        var summary = wb.Worksheets.Add("Özet");
        summary.Cell(1, 1).Value = payload.Title;
        summary.Cell(1, 1).Style.Font.Bold = true;
        summary.Cell(1, 1).Style.Font.FontSize = 14;
        summary.Cell(2, 1).Value = "Hazırlayan";
        summary.Cell(2, 2).Value = payload.PreparedBy;
        if (!string.IsNullOrWhiteSpace(payload.Company))
        {
            summary.Cell(3, 1).Value = "Firma";
            summary.Cell(3, 2).Value = payload.Company;
        }
        summary.Cell(4, 1).Value = "Tarih";
        summary.Cell(4, 2).Value = payload.Date;

        var row = 6;
        summary.Cell(row, 1).Value = "#";
        summary.Cell(row, 2).Value = "Hesap";
        summary.Range(row, 1, row, 2).Style.Font.Bold = true;
        row++;
        for (var i = 0; i < payload.Sections.Count; i++)
        {
            var s = payload.Sections[i];
            summary.Cell(row, 1).Value = i + 1;
            summary.Cell(row, 2).Value = s.Mode is null ? s.ToolName : $"{s.ToolName} — {s.Mode}";
            row++;
        }
        summary.Columns().AdjustToContents();

        for (var i = 0; i < payload.Sections.Count; i++)
        {
            BuildSectionSheet(wb, i + 1, payload.Sections[i]);
        }

        using var stream = new MemoryStream();
        wb.SaveAs(stream);
        return stream.ToArray();
    }

    private static void BuildSectionSheet(XLWorkbook wb, int no, ReportSection section)
    {
        // Sayfa adı 31 karakteri ve Excel'in yasakladığı karakterleri
        // (: \ / ? * [ ]) aşamaz.
        var name = SanitizeSheetName($"{no} {section.ToolName}");
        var ws = wb.Worksheets.Add(name);

        var r = 1;
        ws.Cell(r, 1).Value = section.Mode is null ? section.ToolName : $"{section.ToolName} — {section.Mode}";
        ws.Cell(r, 1).Style.Font.Bold = true;
        r += 2;

        if (section.Inputs.Count > 0)
        {
            r = WriteFieldBlock(ws, r, "Girdiler", section.Inputs);
            r++;
        }

        if (section.Results.Count > 0)
        {
            r = WriteFieldBlock(ws, r, "Sonuçlar", section.Results);
            r++;
        }

        if (section.Notes.Count > 0)
        {
            ws.Cell(r, 1).Value = "Notlar";
            ws.Cell(r, 1).Style.Font.Bold = true;
            r++;
            foreach (var note in section.Notes)
            {
                ws.Cell(r, 1).Value = note.Text;
                r++;
            }
            r++;
        }

        if (section.Chart?.Table is { } table && table.Columns.Count > 0)
        {
            ws.Cell(r, 1).Value = section.Chart.Title ?? "Grafik verisi";
            ws.Cell(r, 1).Style.Font.Bold = true;
            r++;
            for (var c = 0; c < table.Columns.Count; c++)
            {
                ws.Cell(r, c + 1).Value = table.Columns[c];
                ws.Cell(r, c + 1).Style.Font.Bold = true;
            }
            r++;
            foreach (var dataRow in table.Rows)
            {
                for (var c = 0; c < dataRow.Count; c++)
                {
                    WriteValue(ws.Cell(r, c + 1), dataRow[c]);
                }
                r++;
            }
        }

        ws.Columns().AdjustToContents();
    }

    private static int WriteFieldBlock(IXLWorksheet ws, int startRow, string heading, IReadOnlyList<ReportField> fields)
    {
        var r = startRow;
        ws.Cell(r, 1).Value = heading;
        ws.Cell(r, 1).Style.Font.Bold = true;
        r++;
        foreach (var field in fields)
        {
            ws.Cell(r, 1).Value = field.Label;
            WriteValue(ws.Cell(r, 2), field.Value);
            if (!string.IsNullOrWhiteSpace(field.Unit)) ws.Cell(r, 3).Value = field.Unit;
            if (field.Emphasis)
            {
                ws.Range(r, 1, r, 3).Style.Font.Bold = true;
            }
            r++;
        }
        return r;
    }

    // Sayı olarak ayrıştırılabiliyorsa gerçek sayı hücresine yazılır —
    // kullanıcı formülde kullanabilsin diye (§5.4). Ondalık ayırıcı her
    // zaman nokta: num.js'in ürettiği dize dile göre değişmez.
    private static void WriteValue(IXLCell cell, string raw)
    {
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
        {
            cell.Value = n;
        }
        else
        {
            cell.Value = raw;
        }
    }

    private static string SanitizeSheetName(string name)
    {
        var cleaned = new string(name.Select(c => ":\\/?*[]".Contains(c) ? '-' : c).ToArray());
        return cleaned.Length > 31 ? cleaned[..31] : cleaned;
    }
}

using System.Globalization;
using ClosedXML.Excel;

namespace Alp.Reports;

// docs/uyelik-ve-rapor-plani.md §5.4. PDF'te yan yana duran şematik/grafik
// burada yok — SVG basılmaz; grafiğin ham verisi (§5.1'deki `chart.table`)
// sütun olarak girer, kullanıcı kendi grafiğini çizebilsin diye.
//
// GERÇEK EXCEL GRAFİĞİ YOK ve eklenemiyor: ClosedXML grafik nesnesi
// oluşturamaz (kütüphane sınırı). Ham veri tam bu yüzden tabloya yazılıyor —
// kullanıcı aralığı seçip kendi grafiğini üç tıkta ekliyor.
public class XlsxReportBuilder
{
    // PDF ile AYNI palet (solder-light.css). Rapor sitenin kendi renklerini
    // kullanır, ayrı bir "kâğıt paleti" yok (§5.1.1 kararı) — iki üretici
    // ayrışmasın diye değerler burada da birebir tekrarlanıyor.
    private static readonly XLColor Green = XLColor.FromHtml("#007937");
    private static readonly XLColor Muted = XLColor.FromHtml("#5d6e60");
    private static readonly XLColor Rule = XLColor.FromHtml("#d2e1d5");
    private static readonly XLColor Raised = XLColor.FromHtml("#f6fcf7");

    // `AdjustToContents()` en uzun HÜCREYE göre genişletiyor; notlar tam cümle
    // olduğu için A sütunu 220 karakteri buluyordu ve değerler ekranın dışında
    // kalıyordu. Tavan + uzun metinlerde satır kaydırma bunu düzeltir.
    private const double MaxColumnWidth = 46;
    private const double MinColumnWidth = 9;

    public byte[] Build(ReportPayload payload)
    {
        using var wb = new XLWorkbook();
        var labels = payload.Labels;

        var summary = wb.Worksheets.Add(labels.SummarySheet);
        summary.Cell(1, 1).Value = payload.Title;
        summary.Cell(1, 1).Style.Font.Bold = true;
        summary.Cell(1, 1).Style.Font.FontSize = 14;
        summary.Cell(1, 1).Style.Font.FontColor = Green;

        // Künye satırları ARDIŞIK yazılır. Sabit satır numarası kullanıldığında
        // firma boşken araya boş bir satır kalıyor ve künye ikiye bölünmüş
        // görünüyordu.
        var head = 2;
        summary.Cell(head, 1).Value = labels.PreparedBy;
        summary.Cell(head, 2).Value = payload.PreparedBy;
        head++;
        if (!string.IsNullOrWhiteSpace(payload.Company))
        {
            summary.Cell(head, 1).Value = labels.Company;
            summary.Cell(head, 2).Value = payload.Company;
            head++;
        }
        summary.Cell(head, 1).Value = labels.Date;
        WriteDate(summary.Cell(head, 2), payload.Date);
        // Tarih gerçek bir sayı hücresi olduğu için Excel onu SAĞA yaslar ve
        // künyedeki öteki değerlerden kopuk düşer. Hizalama elle sola alınır;
        // hücrenin tipi (ve dolayısıyla sıralanabilirliği) değişmez.
        summary.Cell(head, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
        summary.Range(2, 1, head, 1).Style.Font.FontColor = Muted;

        // Hesap sayfaları ÖNCE kurulur: özet listesindeki satırlar onlara
        // köprü verecek, köprünün hedefi de var olmalı.
        var sheets = new List<IXLWorksheet>();
        for (var i = 0; i < payload.Sections.Count; i++)
        {
            sheets.Add(BuildSectionSheet(wb, i + 1, payload.Sections[i], labels));
        }

        var row = head + 2;
        summary.Cell(row, 1).Value = "#";
        summary.Cell(row, 2).Value = labels.Calculation;
        StyleTableHeader(summary.Range(row, 1, row, 2));
        row++;
        var firstDataRow = row;
        for (var i = 0; i < payload.Sections.Count; i++)
        {
            var s = payload.Sections[i];
            summary.Cell(row, 1).Value = i + 1;
            // Sıra numarası sola yaslanır: geniş bir sütunda sağa yaslı sayı,
            // yanındaki addan kopup ortada asılı kalıyordu.
            summary.Cell(row, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;

            var nameCell = summary.Cell(row, 2);
            nameCell.Value = s.Mode is null ? s.ToolName : $"{s.ToolName} — {s.Mode}";
            // Ada tıklayınca ilgili sayfaya gidilir. Özet tek başına raporun
            // TAMAMI sanılıyordu; alttaki sayfa sekmeleri gözden kaçıyor.
            nameCell.SetHyperlink(new XLHyperlink(sheets[i].Cell(1, 1)));
            row++;
        }
        if (row > firstDataRow)
        {
            summary.Range(firstDataRow - 1, 1, row - 1, 2).Style
                .Border.OutsideBorder = XLBorderStyleValues.Thin;
            summary.Range(firstDataRow - 1, 1, row - 1, 2).Style
                .Border.OutsideBorderColor = Rule;
        }

        row++;
        summary.Cell(row, 1).Value = payload.Sections.Count == 1
            ? labels.SummaryHintSingle
            : labels.SummaryHintMany;
        summary.Cell(row, 1).Style.Font.FontColor = Muted;
        summary.Cell(row, 1).Style.Font.Italic = true;
        summary.Cell(row, 1).Style.Font.FontSize = 9;

        FitColumns(summary);

        // Dosya İÇERİKLE açılır. Tek hesaplı raporda "Özet" neredeyse boştur ve
        // raporun tamamı sanılıyor; ilk hesap sayfası etkin bırakılır. Birden
        // çok hesapta özet gerçekten giriş sayfasıdır, orada kalınır.
        if (sheets.Count == 1) sheets[0].SetTabActive();
        else summary.SetTabActive();

        using var stream = new MemoryStream();
        wb.SaveAs(stream);
        return stream.ToArray();
    }

    private static IXLWorksheet BuildSectionSheet(XLWorkbook wb, int no, ReportSection section, ReportLabels labels)
    {
        var name = SanitizeSheetName($"{no} {section.ToolName}", labels.Calculation);
        var ws = wb.Worksheets.Add(name);

        var r = 1;
        ws.Cell(r, 1).Value = section.Mode is null ? section.ToolName : $"{section.ToolName} — {section.Mode}";
        ws.Cell(r, 1).Style.Font.Bold = true;
        ws.Cell(r, 1).Style.Font.FontSize = 12;
        ws.Cell(r, 1).Style.Font.FontColor = Green;
        r += 2;

        if (section.Inputs.Count > 0)
        {
            r = WriteFieldBlock(ws, r, labels.Inputs, section.Inputs);
            r++;
        }

        if (section.Results.Count > 0)
        {
            r = WriteFieldBlock(ws, r, labels.Results, section.Results);
            r++;
        }

        // Denklemler PDF'te var, Excel'de hiç yoktu: hangi bağıntının
        // kullanıldığı bilgisi tabloya geçen kullanıcı için kayboluyordu.
        if (section.Formula.Count > 0)
        {
            WriteBlockHeading(ws, r, labels.Equations);
            r++;
            foreach (var line in section.Formula)
            {
                ws.Cell(r, 1).Value = line;
                ws.Cell(r, 1).Style.Font.FontName = "Consolas";
                // Formül SARDIRILMAZ. Sardırınca ifade ortadan bölünüyordu
                // ("R(T) = ρ₂₀·[1 + α(T − 20)] ·" / "L / (W·t)") ve denklem
                // yanlış okunuyordu. Satırın sağındaki hücreler boş olduğu için
                // metin taşar ve tam görünür. Notlar bunun tersi: onlar düz
                // cümle, sarmak doğru.
                ws.Cell(r, 1).Style.Alignment.WrapText = false;
                r++;
            }
            r++;
        }

        if (section.Notes.Count > 0)
        {
            WriteBlockHeading(ws, r, labels.Notes);
            r++;
            foreach (var note in section.Notes)
            {
                ws.Cell(r, 1).Value = note.Text;
                // Sütun genişliğini şişiren asıl metin bu: sardırılır.
                ws.Cell(r, 1).Style.Alignment.WrapText = true;
                ws.Cell(r, 1).Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
                r++;
            }
            r++;
        }

        if (section.Chart?.Table is { } table && table.Columns.Count > 0)
        {
            // Blok başlığı SABİT ve kısadır. `Chart.Title` bir başlık değil,
            // grafiği anlatan tam cümledir ("Kesit alanı arttıkça akım
            // kapasitesi artar; …"); kalın başlık olarak basıldığında tablonun
            // çok ötesine taşıyor ve blok başlangıcı seçilemiyordu. Cümle
            // altta, sönük ve sardırılmış satırda durur.
            WriteBlockHeading(ws, r, labels.ChartData);
            r++;
            if (!string.IsNullOrWhiteSpace(section.Chart.Title))
            {
                ws.Cell(r, 1).Value = section.Chart.Title;
                ws.Cell(r, 1).Style.Font.FontColor = Muted;
                ws.Cell(r, 1).Style.Font.FontSize = 9;
                ws.Cell(r, 1).Style.Alignment.WrapText = true;
                ws.Cell(r, 1).Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
                r++;
            }
            ws.Cell(r, 1).Value =
                labels.ChartHint;
            ws.Cell(r, 1).Style.Font.Italic = true;
            ws.Cell(r, 1).Style.Font.FontColor = Muted;
            ws.Cell(r, 1).Style.Font.FontSize = 9;
            r++;

            var headerRow = r;
            for (var c = 0; c < table.Columns.Count; c++)
            {
                ws.Cell(r, c + 1).Value = table.Columns[c];
            }
            StyleTableHeader(ws.Range(r, 1, r, table.Columns.Count));
            r++;
            foreach (var dataRow in table.Rows)
            {
                for (var c = 0; c < dataRow.Count; c++)
                {
                    WriteValue(ws.Cell(r, c + 1), dataRow[c]);
                }
                r++;
            }

            if (r > headerRow + 1)
            {
                var range = ws.Range(headerRow, 1, r - 1, table.Columns.Count);
                range.Style.Border.InsideBorder = XLBorderStyleValues.Hair;
                range.Style.Border.InsideBorderColor = Rule;
                range.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                range.Style.Border.OutsideBorderColor = Rule;
                range.SetAutoFilter();
            }
        }

        FitColumns(ws);
        // Başlık satırı sabit kalsın: uzun sayfada hangi hesaba baktığın
        // aşağı inince kaybolmasın.
        ws.SheetView.FreezeRows(1);
        return ws;
    }

    private static int WriteFieldBlock(IXLWorksheet ws, int startRow, string heading, IReadOnlyList<ReportField> fields)
    {
        var r = startRow;
        WriteBlockHeading(ws, r, heading);
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

    private static void WriteBlockHeading(IXLWorksheet ws, int row, string text)
    {
        var cell = ws.Cell(row, 1);
        cell.Value = text;
        cell.Style.Font.Bold = true;
        cell.Style.Font.FontColor = Green;
        ws.Range(row, 1, row, 3).Style.Fill.BackgroundColor = Raised;
        ws.Range(row, 1, row, 3).Style.Border.BottomBorder = XLBorderStyleValues.Thin;
        ws.Range(row, 1, row, 3).Style.Border.BottomBorderColor = Rule;
    }

    private static void StyleTableHeader(IXLRange range)
    {
        range.Style.Font.Bold = true;
        range.Style.Font.FontColor = Green;
        range.Style.Fill.BackgroundColor = Raised;
        range.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
        range.Style.Border.BottomBorderColor = Rule;
    }

    // İçeriğe göre genişlet, sonra TAVAN uygula. Sıra önemli: tavan önce
    // konursa `AdjustToContents` onu ezer.
    private static void FitColumns(IXLWorksheet ws)
    {
        ws.Columns().AdjustToContents();
        foreach (var column in ws.ColumnsUsed())
        {
            if (column.Width > MaxColumnWidth) column.Width = MaxColumnWidth;
            else if (column.Width < MinColumnWidth) column.Width = MinColumnWidth;
        }
    }

    // Sayı olarak ayrıştırılabiliyorsa gerçek sayı hücresine yazılır —
    // kullanıcı formülde kullanabilsin diye (§5.4). Ondalık ayırıcı her
    // zaman nokta: num.js'in ürettiği dize dile göre değişmez.
    //
    // Sayı BİÇİMİ verilmez. Değerler zaten ekrandaki anlamlı basamağıyla
    // geliyor (0.01051 gibi); sabit bir biçim uygulamak onları yuvarlayıp
    // gösterir ve hücrede görünen değer ile gerçek değer ayrışırdı.
    // Formül injection sigortası: ClosedXML `cell.Value = string` atamasını
    // metin hücresi yapar, formül DEĞİL — bugün canlı bir açık yok. Önek yine
    // de konur: kullanıcı bu dosyayı Excel'den CSV'ye aktarıp yeniden açarsa
    // `=`, `+`, `-`, `@` ile başlayan metin O AŞAMADA formüle dönüşür.
    // Kullanıcı verisi (etiketler, proje adı) hücreye buradan giriyor.
    private static readonly char[] FormulaLeadIns = ['=', '+', '@'];

    internal static string GuardFormulaLeadIn(string raw) =>
        raw.Length > 0 && (FormulaLeadIns.Contains(raw[0]) || (raw[0] == '-' && raw.Length > 1 && !char.IsDigit(raw[1])))
            ? "'" + raw
            : raw;

    private static void WriteValue(IXLCell cell, string raw)
    {
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
        {
            cell.Value = n;
        }
        else
        {
            cell.Value = GuardFormulaLeadIn(raw);
        }
    }

    // Tarih gerçek tarih hücresi olur: metin kalırsa sıralanamaz, biçimi
    // değiştirilemez ve tarih fonksiyonlarına girmez. Yük `dd.MM.yyyy`
    // gönderiyor (reportText.js → reportDateStamp); ayrıştırılamayan bir
    // değer geldiğinde metin olarak bırakılır, veri kaybolmaz.
    private static void WriteDate(IXLCell cell, string raw)
    {
        if (DateTime.TryParseExact(raw, "dd.MM.yyyy", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date))
        {
            cell.Value = date;
            cell.Style.DateFormat.Format = "dd.MM.yyyy";
        }
        else
        {
            cell.Value = raw;
        }
    }

    // Excel sayfa adı 31 karakteri ve şu karakterleri kabul etmez: : \ / ? * [ ]
    //
    // Kesme KELİME SINIRINDA yapılır: ham kesme "1 Yol Genişliği ve Akım Kapasit"
    // gibi yarım kelimeyle bitiyordu. Sınırdan önce boşluk yoksa ham kesmeye
    // düşülür — ad üretilemeden kalmaz.
    internal static string SanitizeSheetName(string name, string fallback)
    {
        var cleaned = new string(name.Select(c => ":\\/?*[]".Contains(c) ? '-' : c).ToArray()).Trim();
        if (cleaned.Length <= 31) return cleaned.Length > 0 ? cleaned : fallback;

        var cut = cleaned[..31];
        var lastSpace = cut.LastIndexOf(' ');
        // Çok erken bir boşlukta kesip adı anlamsız kısaltmamak için alt sınır.
        if (lastSpace >= 12) cut = cut[..lastSpace];
        return cut.TrimEnd(' ', '-', '—');
    }
}

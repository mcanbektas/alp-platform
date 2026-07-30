using QuestPDF.Drawing;
using QuestPDF.Drawing.Exceptions;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Alp.Reports;

// docs/uyelik-ve-rapor-plani.md §5.3 + §12 (Faz 1 risk denemesi). Düzen ve
// renk kararları orada uçtan uca doğrulandı; bu, aynı kalıbın gerçek
// ReportPayload üzerinde çalışan hâli.
/// <param name="logoPng">Rapor başlığındaki logo.</param>
/// <param name="onSvgError">
/// Bir SVG çözülemediğinde çağrılır (bozuk parçanın başı verilir). Boş
/// bırakılabilir. Amaç: atlanan çizim SESSİZ kalmasın — belgede yerine not
/// basılıyor ama sunucu günlüğünde de iz kalmalı, yoksa raporlar zamanla
/// çizimsiz çıkar ve kimse fark etmez.
/// </param>
public class PdfReportBuilder(byte[] logoPng, Action<string>? onSvgError = null)
{
    // solder-light.css paleti — rapor sitenin kendi renklerini kullanır,
    // ayrı bir "kâğıt paleti" yok (§5.1.1 kararı).
    private static readonly string Green = "#007937";
    private static readonly string Ink = "#1c261e";
    private static readonly string Muted = "#5d6e60";
    private static readonly string Rule = "#d2e1d5";
    private static readonly string Raised = "#f6fcf7";
    private static readonly string Warn = "#8c5f00";
    private static readonly string Danger = "#b02a2c";

    // Faz 3b tamamlanana kadar bu adlar kayıtlı olmayabilir; SkiaSharp
    // bilinmeyen aile adında platform varsayılanına düşer, patlamaz.
    private const string FontDisplay = "Chakra Petch";
    private const string FontBody = "IBM Plex Sans";
    private const string FontMono = "IBM Plex Mono";

    // QuestPDF, içeriği sayfa kısıtlarına sığdıramadığında `GeneratePdf()`
    // sırasında `DocumentLayoutException` fırlatır — yükün kendisi geçerli
    // olsa bile (5000 satırlık bir proje raporu canlı sunucuda bunu tetikledi).
    // Sarmalanmadığında bu işlenmemiş bir 500 oluyordu; `ReportLayoutException`
    // olarak dışarı verilir ve uç nokta onu temiz bir hata koduna çevirir.
    /// <param name="logoOverride">
    /// Kullanıcının kendi firma logosu. Verilmezse başlıkta uygulamanın
    /// varsayılan logosu kalır. Baytların geçerli bir PNG/JPEG olduğu YÜKLEME
    /// sırasında doğrulanır (bkz. AuthEndpoints.UploadLogo); burada ikinci bir
    /// çözümleme yapılmaz.
    /// </param>
    public byte[] Build(ReportPayload payload, byte[]? logoOverride = null)
    {
        try
        {
            return Compose(payload, logoOverride).GeneratePdf();
        }
        catch (DocumentLayoutException ex)
        {
            throw new ReportLayoutException(ex);
        }
    }

    private Document Compose(ReportPayload payload, byte[]? logoOverride)
    {
        var logo = logoOverride is { Length: > 0 } ? logoOverride : logoPng;

        var green = Color.FromHex(Green);
        var ink = Color.FromHex(Ink);
        var muted = Color.FromHex(Muted);
        var rule = Color.FromHex(Rule);
        var raised = Color.FromHex(Raised);

        return Document.Create(doc =>
        {
            doc.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(36);
                page.DefaultTextStyle(t => t.FontSize(9).FontColor(ink).FontFamily(FontMono));

                page.Header().Column(col =>
                {
                    col.Item().Row(row =>
                    {
                        row.ConstantItem(110).Height(22).Image(logo).FitHeight();
                        row.RelativeItem();
                        row.AutoItem().AlignMiddle().Text(payload.Date).FontSize(8).FontColor(muted);
                    });
                    col.Item().PaddingTop(6).LineHorizontal(0.8f).LineColor(rule);
                });

                page.Footer().AlignRight().Text(t =>
                {
                    t.DefaultTextStyle(s => s.FontSize(8).FontColor(muted));
                    t.CurrentPageNumber();
                    t.Span(" / ");
                    t.TotalPages();
                });

                page.Content().PaddingTop(14).Column(col =>
                {
                    col.Spacing(0);

                    col.Item().AlignCenter().Text(payload.Title)
                        .FontSize(22).Bold().FontColor(green).FontFamily(FontDisplay).LetterSpacing(0.06f);

                    col.Item().PaddingTop(14).Text(t =>
                    {
                        t.DefaultTextStyle(s => s.FontFamily(FontBody));
                        t.Span("Hazırlayan: ").FontColor(muted);
                        t.Span(payload.PreparedBy).SemiBold();
                    });
                    if (!string.IsNullOrWhiteSpace(payload.Company))
                    {
                        col.Item().PaddingTop(2).Text(t =>
                        {
                            t.DefaultTextStyle(s => s.FontFamily(FontBody));
                            t.Span("Firma: ").FontColor(muted);
                            t.Span(payload.Company);
                        });
                    }

                    for (var i = 0; i < payload.Sections.Count; i++)
                    {
                        var no = i + 1;
                        var section = payload.Sections[i];
                        col.Item().PaddingTop(i == 0 ? 16 : 20).Element(c => Section(c, no, section, green, ink, muted, rule, raised));
                    }
                });
            });
        });
    }

    // Statik DEĞİL: SVG çözülemediğinde örnek üzerindeki `onSvgError` geri
    // çağrısına ulaşması gerekiyor.
    private void Section(
        IContainer container, int no, ReportSection section,
        Color green, Color ink, Color muted, Color rule, Color raised)
    {
        // Sayfa kırılması blok SINIRINDA olur, blok ORTASINDA değil — her
        // mantıksal blok kendi ShowEntire()'ına sarılır. Faz 1 denemesinde
        // öğrenilen kural: bunu bölümün TAMAMINA uygulamak yanlış olurdu,
        // bir bölüm sayfadan uzun olabilir.
        container.Column(col =>
        {
            var heading = section.Mode is null ? section.ToolName : $"{section.ToolName} — {section.Mode}";
            col.Item().ShowEntire().Column(head =>
            {
                head.Item().BorderBottom(1.4f).BorderColor(green).PaddingBottom(3)
                    .Text($"{no}. {heading}").FontSize(12).SemiBold().FontColor(green).FontFamily(FontBody);

                if (section.Inputs.Count > 0)
                {
                    head.Item().PaddingTop(10).Text("Girdiler").FontSize(9).SemiBold().FontColor(muted).FontFamily(FontBody);
                    head.Item().PaddingTop(4).Element(c => FieldTable(c, section.Inputs, muted, rule, raised));
                }
            });

            if (!string.IsNullOrWhiteSpace(section.SchematicSvg))
            {
                col.Item().PaddingTop(12).ShowEntire().Column(fig =>
                {
                    // Çözülemeyen bir SVG BÜTÜN raporu düşürmemeli. QuestPDF
                    // burada istisna fırlatıyor ve daha önce tek bir bozuk
                    // öznitelik yüzünden PDF hiç üretilemiyordu — kullanıcı
                    // "Sunucudan beklenmeyen bir yanıt geldi" görüyordu.
                    // Şema atlanır, belge kalan bölümlerle üretilir; eksik
                    // olduğu da SESSİZ geçilmez, yerine not yazılır.
                    if (TryRenderSvg(fig, section.SchematicSvg, "şema", full: false))
                    {
                        if (!string.IsNullOrWhiteSpace(section.SchematicCaption))
                        {
                            fig.Item().PaddingTop(3).AlignCenter()
                                .Text(section.SchematicCaption).FontSize(7.5f).FontColor(muted).FontFamily(FontBody);
                        }
                    }
                    else
                    {
                        fig.Item().AlignCenter().Text("Şema bu raporda gösterilemedi.")
                            .FontSize(7.5f).FontColor(muted).FontFamily(FontBody);
                    }
                });
            }

            if (section.Formula.Count > 0)
            {
                col.Item().PaddingTop(12).ShowEntire()
                    .Background(raised).Border(0.8f).BorderColor(rule).Padding(7)
                    .Column(f =>
                    {
                        foreach (var line in section.Formula)
                        {
                            f.Item().Text(line).FontSize(9.5f);
                        }
                    });
            }

            if (section.Results.Count > 0)
            {
                col.Item().PaddingTop(12).ShowEntire().Column(res =>
                {
                    res.Item().Text("Sonuçlar").FontSize(9).SemiBold().FontColor(muted).FontFamily(FontBody);
                    res.Item().PaddingTop(4).Element(c => FieldTable(c, section.Results, muted, rule, raised, green));
                });
            }

            if (section.Chart?.Svg is { Length: > 0 })
            {
                col.Item().PaddingTop(12).ShowEntire().Column(fig =>
                {
                    // Şemayla aynı gerekçe: çözülemeyen bir grafik BÜTÜN raporu
                    // düşürmemeli (bkz. TryRenderSvg).
                    if (TryRenderSvg(fig, section.Chart.Svg, "grafik", full: true))
                    {
                        if (!string.IsNullOrWhiteSpace(section.Chart.Title))
                        {
                            fig.Item().PaddingTop(3).AlignCenter()
                                .Text(section.Chart.Title).FontSize(7.5f).FontColor(muted).FontFamily(FontBody);
                        }
                    }
                    else
                    {
                        fig.Item().AlignCenter().Text("Grafik bu raporda gösterilemedi.")
                            .FontSize(7.5f).FontColor(muted).FontFamily(FontBody);
                    }
                });
            }

            // Ekrandaki mühendislik yorumuyla aynı üç seviye, aynı işaret ve
            // renk (CLAUDE.md "Durum çipi tek kuralı" + .commentary li.* CSS).
            foreach (var note in section.Notes)
            {
                var (mark, color) = note.Level switch
                {
                    "danger" => ("×", Color.FromHex(Danger)),
                    "warn" => ("!", Color.FromHex(Warn)),
                    _ => ("✓", green),
                };
                col.Item().PaddingTop(10).ShowEntire().Row(row =>
                {
                    row.ConstantItem(14).Text(mark).FontColor(color).SemiBold();
                    row.RelativeItem().Text(note.Text).FontSize(8.5f).FontColor(color).FontFamily(FontBody);
                });
            }
        });
    }

    private static void FieldTable(
        IContainer container, IReadOnlyList<ReportField> fields,
        Color muted, Color rule, Color raised, Color? emphasisColor = null)
    {
        container.Table(table =>
        {
            table.ColumnsDefinition(c => { c.RelativeColumn(3); c.RelativeColumn(2); });
            foreach (var field in fields)
            {
                var display = string.IsNullOrWhiteSpace(field.Unit) ? field.Value : $"{field.Value} {field.Unit}";
                if (field.Emphasis)
                {
                    var color = emphasisColor ?? muted;
                    table.Cell().Background(raised).Element(c => Cell(c, rule))
                        .Text(field.Label).SemiBold().FontColor(color).FontFamily(FontBody);
                    table.Cell().Background(raised).Element(c => Cell(c, rule)).AlignRight()
                        .Text(display).SemiBold().FontColor(color);
                }
                else
                {
                    table.Cell().Element(c => Cell(c, rule)).Text(field.Label).FontColor(muted).FontFamily(FontBody);
                    table.Cell().Element(c => Cell(c, rule)).AlignRight().Text(display);
                }
            }
        });
    }

    private static IContainer Cell(IContainer c, Color rule) =>
        c.BorderBottom(0.6f).BorderColor(rule).PaddingVertical(3.5f).PaddingHorizontal(6);

    /// <summary>
    /// SVG'yi yerleştirmeyi dener; çözülemezse <c>false</c> döner ve belgeye
    /// hiçbir şey eklemez.
    /// </summary>
    /// <remarks>
    /// Geniş <c>catch</c> bilinçli: QuestPDF çözümleme hatasını düz
    /// <c>System.Exception</c> olarak fırlatıyor, yakalanacak dar bir tür yok.
    /// Buradaki tek amaç bir bölümün çizimi yüzünden BÜTÜN raporun
    /// üretilememesini engellemek — hata yutulmuyor, çağıran yerine not basıyor.
    /// </remarks>
    private bool TryRenderSvg(ColumnDescriptor fig, string svg, string kind, bool full)
    {
        // Boyutsuz SVG çizime HİÇ verilmez. Bu bir biçim tercihi değil,
        // dayanıklılık sınırı: `viewBox` ve `width`/`height` yokken çizim
        // katmanı çözümü bulamıyor ve hata fırlatmak yerine dönüyor —
        // istek yanıtsız kalırken işlem bir çekirdeği doldurup belleği
        // gigabaytlarca şişiriyor (yerel ölçüm: %248 CPU, 7 GB, yanıt yok).
        //
        // Kimlik doğrulamalı bir kullanıcı kendi rapor bölümünü kaydedebildiği
        // için bu tek istekle sunucuyu düşürmeye yeterdi. Uygulamanın kendi
        // ürettiği çizimlerde `viewBox` her zaman var; kaybedilen bir şey yok.
        if (!HasIntrinsicSize(svg))
        {
            onSvgError?.Invoke(
                $"{kind} SVG'si boyut bilgisi taşımıyor (viewBox/width+height yok), çizilmedi: "
                + svg[..Math.Min(300, svg.Length)]);
            return false;
        }

        try
        {
            if (full) fig.Item().Svg(svg);
            else fig.Item().AlignCenter().Width(210).Svg(svg);
            return true;
        }
        catch (Exception ex)
        {
            // Teşhis için bozuk parçanın BAŞI da verilir: hata mesajı
            // ("Cannot decode the provided SVG image.") hangi özniteliğin
            // bozulduğunu söylemiyor, dizeyi görmeden neden bulunamıyor.
            onSvgError?.Invoke(
                $"{kind} SVG'si çözülemedi: {ex.Message} | ilk 600 karakter: "
                + svg[..Math.Min(600, svg.Length)]);
            return false;
        }
    }

    // `<svg>` açılış etiketinde viewBox ya da width+height var mı? Ayrıştırıcı
    // değil, kapı: geçerli bir SVG'yi elemek değil, boyutsuz olanı çizime
    // sokmamak amaç. Bu yüzden yalnız ilk etikete bakılır ve şüpheli her şey
    // reddedilir.
    //
    // Öznitelik adları TAM harf büyüklüğüyle aranır (`Ordinal`), çünkü SVG bir
    // XML lehçesidir ve öznitelik adları harf duyarlıdır: `VIEWBOX` diye bir
    // öznitelik yoktur. Arama harf duyarsız olduğunda `<svg VIEWBOX="0 0 100 40">`
    // kapıdan geçiyordu ve çizim katmanı onu boyutsuz görüp bütün belgeyi düzen
    // hatasına düşürüyordu — kullanıcıya "rapor çok büyük" (422) diyen, sebebi
    // yanlış söyleyen bir yanıt. Tam eşleşmeyle o SVG burada elenir: yalnız o
    // çizim atlanır, notu düşer, rapor üretilir. Etiket adı yine harf duyarsız
    // aranır; amaç etiketi BULMAK, geçerliliğine karar vermek değil.
    private static bool HasIntrinsicSize(string svg)
    {
        var open = svg.IndexOf("<svg", StringComparison.OrdinalIgnoreCase);
        if (open < 0) return false;

        var close = svg.IndexOf('>', open);
        if (close < 0) return false;

        var tag = svg[open..close];
        if (tag.Contains("viewBox=", StringComparison.Ordinal)) return true;

        return tag.Contains("width=", StringComparison.Ordinal)
            && tag.Contains("height=", StringComparison.Ordinal);
    }
}

public static class ReportFonts
{
    // Faz 3b: ekranın kullandığı üç ailenin tam kapsamlı ttf sürümleri
    // (`assets/report-fonts/`) PDF'e gömülür, böylece belge ekranla aynı yazı
    // tipini gösterir. Yalnız `*.ttf` okunur — sitenin woff2 alt kümelerini
    // çizim katmanı çözemez. Dizin yoksa sessizce atlanır, QuestPDF platform
    // yazı tipine düşer.
    /// <returns>Kaydedilen yazı tipi dosyası sayısı — sıfır, platform yazı tipine
    /// düşüleceği anlamına gelir. Çağıran bunu üretimde uyarı olarak basar:
    /// konteyner tabanı Debian'da sistem yazı tipi yoktur, sessizce düşülürse
    /// PDF'te yazılar kaybolur.</returns>
    public static int RegisterIfAvailable(string fontsDirectory)
    {
        if (!Directory.Exists(fontsDirectory)) return 0;
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(fontsDirectory, "*.ttf"))
        {
            using var stream = File.OpenRead(file);
            FontManager.RegisterFont(stream);
            count++;
        }
        return count;
    }
}

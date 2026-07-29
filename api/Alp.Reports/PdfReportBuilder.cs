using QuestPDF.Drawing;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Alp.Reports;

// docs/uyelik-ve-rapor-plani.md §5.3 + §12 (Faz 1 risk denemesi). Düzen ve
// renk kararları orada uçtan uca doğrulandı; bu, aynı kalıbın gerçek
// ReportPayload üzerinde çalışan hâli.
public class PdfReportBuilder(byte[] logoPng)
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

    public byte[] Build(ReportPayload payload)
    {
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
                        row.ConstantItem(110).Height(22).Image(logoPng).FitHeight();
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
        }).GeneratePdf();
    }

    private static void Section(
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
                    fig.Item().AlignCenter().Width(210).Svg(section.SchematicSvg);
                    if (!string.IsNullOrWhiteSpace(section.SchematicCaption))
                    {
                        fig.Item().PaddingTop(3).AlignCenter()
                            .Text(section.SchematicCaption).FontSize(7.5f).FontColor(muted).FontFamily(FontBody);
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
                    fig.Item().Svg(section.Chart.Svg);
                    if (!string.IsNullOrWhiteSpace(section.Chart.Title))
                    {
                        fig.Item().PaddingTop(3).AlignCenter()
                            .Text(section.Chart.Title).FontSize(7.5f).FontColor(muted).FontFamily(FontBody);
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
}

public static class ReportFonts
{
    // Faz 3b tamamlanınca web/public/fonts/ altındaki dosyalar aynı
    // dizinden PDF'e de gömülecek — tek kaynak. O ana kadar dizin yoksa
    // sessizce atlanır, QuestPDF platform yazı tipine düşer.
    public static void RegisterIfAvailable(string fontsDirectory)
    {
        if (!Directory.Exists(fontsDirectory)) return;
        foreach (var file in Directory.EnumerateFiles(fontsDirectory, "*.ttf"))
        {
            using var stream = File.OpenRead(file);
            FontManager.RegisterFont(stream);
        }
    }
}

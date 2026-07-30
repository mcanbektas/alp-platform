using Alp.Reports;
using QuestPDF.Infrastructure;

namespace Alp.Api.Tests;

// Boyutsuz SVG kapısı.
//
// Neden var: `viewBox` ya da `width`+`height` taşımayan bir SVG çizim katmanına
// verildiğinde süreç %248 CPU ve 7 GB belleğe çıkıyordu — tek istekle hizmet
// dışı bırakma. Kapı, dizeyi çizime hiç sokmuyor ve atlanan çizimi geri bildirim
// kanalına bildiriyor (sessiz atlama, zamanla çizimsiz rapor demek).
//
// Test kapıyı yansımayla değil, gerçek belge üreterek sınar: `PdfReportBuilder`
// örneği kurulur, çıktı baytı alınır ve `onSvgError` geri çağrısı dinlenir.
public class SvgSizeGateTests
{
    static SvgSizeGateTests()
    {
        // Program.cs'te de böyle kuruluyor; kurulmazsa QuestPDF üretimde durur.
        QuestPDF.Settings.License = LicenseType.Community;
    }

    // Başlıktaki logo sınanan şey değil, ama çözülebilir olmak zorunda: 1×1
    // beyaz PNG. Uygulamanın kendi `Assets/logo.png`ini kopyalamak testi bir
    // varlık dosyasına bağlardı, konu ise SVG kapısı.
    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4//8/AAX+Av4N70a4AAAAAElFTkSuQmCC");

    private static (byte[] Pdf, List<string> Errors) Build(string? schematicSvg, string? chartSvg = null)
    {
        var errors = new List<string>();
        var builder = new PdfReportBuilder(TinyPng, errors.Add);

        var section = new ReportSection(
            ToolName: "Yol Genişliği",
            Mode: "Analiz",
            Inputs: [new ReportField("Akım", "2.5", "A")],
            Formula: ["I = k·ΔT^0.44·A^0.725"],
            Results: [new ReportField("Genişlik", "0.62", "mm", true)],
            Notes: [new ReportNote("ok", "Tüm kontroller geçti")],
            SchematicSvg: schematicSvg,
            SchematicCaption: schematicSvg is null ? null : "Şema",
            Chart: chartSvg is null ? null : new ReportChart("Grafik", chartSvg, null));

        var payload = new ReportPayload(1, "Test", "Test", null, "2026-07-30", [section]);
        return (builder.Build(payload), errors);
    }

    [Theory]
    // viewBox yok, width/height yok
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><rect /></svg>")]
    // yalnız width var, height yok — ikisi birlikte şart
    [InlineData("<svg width=\"100\"><rect /></svg>")]
    // yalnız height
    [InlineData("<svg height=\"100\"><rect /></svg>")]
    // hiç <svg> etiketi yok
    [InlineData("<div>bu bir svg değil</div>")]
    // açılış etiketi kapanmıyor: şüpheli her şey reddedilir
    [InlineData("<svg viewBox=\"0 0 10 10\"")]
    public void Boyutsuz_svg_cizime_girmez_ve_bildirilir(string svg)
    {
        var (pdf, errors) = Build(svg);

        // Belge yine üretilir: eksik çizim raporu iptal etmez.
        Assert.NotEmpty(pdf);
        var message = Assert.Single(errors);
        Assert.Contains("boyut bilgisi taşımıyor", message);
    }

    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 100 40\"><rect width=\"10\" height=\"10\" /></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"40\"><rect width=\"10\" height=\"10\" /></svg>")]
    // Öznitelik SIRASI kapıyı değiştirmez
    [InlineData("<svg viewBox=\"0 0 100 40\" xmlns=\"http://www.w3.org/2000/svg\"><rect /></svg>")]
    public void Boyut_tasiyan_svg_gecer(string svg)
    {
        var (pdf, errors) = Build(svg);

        Assert.NotEmpty(pdf);
        Assert.Empty(errors);
    }

    [Fact]
    public void Grafik_svgsi_de_ayni_kapidan_gecer()
    {
        // Kapı iki çizim için de aynı: şematik ve grafik.
        var (pdf, errors) = Build(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 100 40\"><rect /></svg>",
            "<svg xmlns=\"http://www.w3.org/2000/svg\"><rect /></svg>");

        Assert.NotEmpty(pdf);
        var message = Assert.Single(errors);
        Assert.Contains("boyut bilgisi taşımıyor", message);
    }

    [Fact]
    public void Bildirimde_bozuk_parcanin_basi_yer_alir()
    {
        // Teşhis edilebilirlik sözleşmesi: hangi dizenin elendiği görünmeli,
        // yoksa günlükte "bir SVG atlandı" yazan işe yaramaz bir satır kalır.
        var (_, errors) = Build("<svg data-tool=\"padstack\"><rect /></svg>");

        Assert.Contains("data-tool=\"padstack\"", Assert.Single(errors));
    }

    [Fact]
    public void Svg_yoksa_bildirim_de_olmaz()
    {
        var (pdf, errors) = Build(null);

        Assert.NotEmpty(pdf);
        Assert.Empty(errors);
    }
}

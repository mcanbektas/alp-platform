using System.Text.Json;
using Alp.Api.Projects;

namespace Alp.Api.Tests;

// Proje listesindeki önizleme satırlarının süzme kuralları.
//
// Bu kural eskiden istemcideydi (`lib/savedCalculation.js` → `previewRows`) ve
// `savedCalculation.test.js` içinde altı testle korunuyordu. Kural sunucuya
// taşındı, testleri taşınmadı — burada karşılıkları var.
public class ReportPreviewTests
{
    // Önizleme artık dil seçiyor (kayıt bölümü her dilde saklanıyor, bkz.
    // StoredSection). Bu sınıfın sınadığı kural dil değil süzme mantığı;
    // çağrılar tek yerden sabit dille geçer.
    private static (IReadOnlyList<PreviewField> Rows, string? Mode) From(string? reportJson) =>
        ReportPreview.From(reportJson, "tr");

    private static string Json(object payload) =>
        JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Bos_yuk_bos_onizleme_verir(string? reportJson)
    {
        var (rows, mode) = From(reportJson);

        Assert.Empty(rows);
        Assert.Null(mode);
    }

    [Fact]
    public void Bozuk_json_satiri_dusurmez()
    {
        // Eski/bozuk bir bölüm satırı istisna fırlatmaz: kayıt önizlemesiz
        // görünür ama listeden düşmez ve hâlâ açılabilir.
        var (rows, mode) = From("{ bu json değil");

        Assert.Empty(rows);
        Assert.Null(mode);
    }

    [Fact]
    public void Results_yoksa_mod_yine_doner()
    {
        var (rows, mode) = From(Json(new { toolName = "Yol Genişliği", mode = "Analiz" }));

        Assert.Empty(rows);
        Assert.Equal("Analiz", mode);
    }

    [Fact]
    public void Vurgulanan_satir_basa_alinir()
    {
        var (rows, _) = From(Json(new
        {
            results = new object[]
            {
                new { label = "Akım", value = "2.5", unit = "A", emphasis = false },
                new { label = "Genişlik", value = "0.62", unit = "mm", emphasis = true },
            },
        }));

        Assert.Equal("Genişlik", rows[0].Label);
        Assert.True(rows[0].Emphasis);
        Assert.Equal("Akım", rows[1].Label);
    }

    [Fact]
    public void En_fazla_iki_satir_doner()
    {
        var (rows, _) = From(Json(new
        {
            results = Enumerable.Range(1, 9)
                .Select(i => new { label = $"Alan {i}", value = i.ToString(), unit = (string?)null, emphasis = false })
                .ToArray(),
        }));

        Assert.Equal(2, rows.Count);
        Assert.Equal("Alan 1", rows[0].Label);
        Assert.Equal("Alan 2", rows[1].Label);
    }

    [Fact]
    public void Iki_vurgulu_satir_vurgusuzlari_tamamen_iter()
    {
        var (rows, _) = From(Json(new
        {
            results = new object[]
            {
                new { label = "Sade", value = "1", unit = (string?)null, emphasis = false },
                new { label = "Baş", value = "2", unit = (string?)null, emphasis = true },
                new { label = "İkinci baş", value = "3", unit = (string?)null, emphasis = true },
            },
        }));

        Assert.Equal(["Baş", "İkinci baş"], rows.Select(r => r.Label));
    }

    [Fact]
    public void Etiketi_ya_da_degeri_okunamayan_satir_atlanir()
    {
        var (rows, _) = From(Json(new
        {
            results = new object?[]
            {
                null,
                new { label = (string?)null, value = "1", unit = (string?)null, emphasis = false },
                new { label = "Etiket", value = "   ", unit = (string?)null, emphasis = false },
                new { label = "Sağlam", value = "42", unit = "Ω", emphasis = false },
            },
        }));

        var only = Assert.Single(rows);
        Assert.Equal("Sağlam", only.Label);
        Assert.Equal("42", only.Value);
        Assert.Equal("Ω", only.Unit);
    }

    [Fact]
    public void Uzun_metin_80_karakterde_kirpilir()
    {
        var (rows, _) = From(Json(new
        {
            results = new object[]
            {
                new { label = new string('a', 81), value = new string('b', 80), unit = (string?)null, emphasis = false },
            },
        }));

        var only = Assert.Single(rows);
        Assert.Equal(new string('a', 80) + "…", only.Label);
        // Tam 80 karakter kırpılmaz: sınır dahildir, yoksa her değere gereksiz
        // bir "…" takılırdı.
        Assert.Equal(new string('b', 80), only.Value);
    }

    [Fact]
    public void Bastaki_ve_sondaki_bosluk_atilir()
    {
        var (rows, mode) = From(Json(new
        {
            mode = "  Sentez  ",
            results = new object[] { new { label = "  Ad  ", value = "  1  ", unit = "  mm  ", emphasis = false } },
        }));

        var only = Assert.Single(rows);
        Assert.Equal("Ad", only.Label);
        Assert.Equal("1", only.Value);
        Assert.Equal("mm", only.Unit);
        Assert.Equal("Sentez", mode);
    }

    [Fact]
    public void Satir_ici_svg_onizlemeye_girmez()
    {
        // Projeksiyonun bütün noktası bu: şematik ve grafik SVG'si sunucuda
        // kalır. Bir gün `results` yerine bütün bölüm dolaşılırsa 60 hesaplı
        // proje yanıtı yine yüz KB'lara çıkar.
        var svg = $"<svg viewBox=\"0 0 10 10\">{new string('x', 5000)}</svg>";
        var (rows, _) = From(Json(new
        {
            schematicSvg = svg,
            schematicCaption = "Şema",
            chart = new { title = "Grafik", svg },
            inputs = new object[] { new { label = "Girdi", value = "1", unit = (string?)null, emphasis = false } },
            formula = new[] { "I = k·ΔT^0.44" },
            results = new object[] { new { label = "Sonuç", value = "7", unit = (string?)null, emphasis = false } },
        }));

        var only = Assert.Single(rows);
        Assert.Equal("Sonuç", only.Label);
        Assert.DoesNotContain("<svg", only.Label + only.Value + only.Unit);
    }

    [Fact]
    public void Girdiler_onizlemeye_girmez()
    {
        // Yalnız `results` okunur; `inputs` boş bir önizleme demektir.
        var (rows, _) = From(Json(new
        {
            inputs = new object[] { new { label = "Akım", value = "2.5", unit = "A", emphasis = true } },
            results = Array.Empty<object>(),
        }));

        Assert.Empty(rows);
    }

    // ---- Write/ReadStored: PreviewJson kolonunun yazma/okuma çifti ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Write_bos_rapor_icin_null_doner(string? reportJson)
    {
        Assert.Null(ReportPreview.Write(reportJson));
    }

    [Fact]
    public void Write_iki_dil_icin_ayri_onizleme_uretir()
    {
        var reportJson = Json(new
        {
            tr = new { mode = "Analiz", results = new object[] { new { label = "Genişlik", value = "0.62", unit = "mm", emphasis = true } } },
            en = new { mode = "Analysis", results = new object[] { new { label = "Width", value = "0.62", unit = "mm", emphasis = true } } },
        });

        var previewJson = ReportPreview.Write(reportJson);

        var (trRows, trMode) = ReportPreview.ReadStored(previewJson, "tr");
        var (enRows, enMode) = ReportPreview.ReadStored(previewJson, "en");

        Assert.Equal("Analiz", trMode);
        Assert.Equal("Genişlik", Assert.Single(trRows).Label);
        Assert.Equal("Analysis", enMode);
        Assert.Equal("Width", Assert.Single(enRows).Label);
    }

    [Fact]
    public void Write_eski_sekilli_kayitta_iki_dil_ayni_onizlemeyi_tasir()
    {
        // Eski şekil (kayıt tek dilde, dil haritası yok): StoredSection.Read
        // dil parametresini yok sayar, o yüzden Write ürettiği haritada tr/en
        // aynı içeriği taşır — tek dille kaydedilmiş satır iki dilde de açılır.
        var reportJson = Json(new { results = new object[] { new { label = "Akım", value = "2.5", unit = "A", emphasis = false } } });

        var previewJson = ReportPreview.Write(reportJson);

        var (trRows, _) = ReportPreview.ReadStored(previewJson, "tr");
        var (enRows, _) = ReportPreview.ReadStored(previewJson, "en");
        Assert.Equal("Akım", Assert.Single(trRows).Label);
        Assert.Equal("Akım", Assert.Single(enRows).Label);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ bu json değil")]
    public void ReadStored_bos_veya_bozuk_yuk_bos_onizleme_verir(string? previewJson)
    {
        var (rows, mode) = ReportPreview.ReadStored(previewJson, "tr");

        Assert.Empty(rows);
        Assert.Null(mode);
    }

    [Fact]
    public void ReadStored_istenen_dil_yoksa_eldekine_duser()
    {
        // Yalnızca tr ile yazılmış bir PreviewJson (örn. dil eklenmeden önce
        // kaydedilmiş) — en istenince tr'ye düşer, boş dönmez.
        var previewJson = Json(new { tr = new { mode = "Sentez", rows = new object[] { new { label = "Kalınlık", value = "35", unit = "µm", emphasis = false } } } });

        var (rows, mode) = ReportPreview.ReadStored(previewJson, "en");

        Assert.Equal("Sentez", mode);
        Assert.Equal("Kalınlık", Assert.Single(rows).Label);
    }
}

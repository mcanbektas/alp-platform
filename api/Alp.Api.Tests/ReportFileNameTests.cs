using Alp.Api.Reports;

namespace Alp.Api.Tests;

// Dosya adı yardımcıları saf ve istemci verisi taşıyor (proje adı slug'a,
// dil eki doğrudan ada giriyor) — sıfır testle duruyorlardı.
public class ReportFileNameTests
{
    [Theory]
    [InlineData("Güç Kaynağı Şeması", "guc-kaynagi-semasi")]
    [InlineData("İĞÜŞÖÇ ığüşöç", "igusoc-igusoc")]
    [InlineData("a  --  b", "a-b")]
    [InlineData("---", "rapor")]
    [InlineData("", "rapor")]
    public void Slugify_turkce_katlar_ayraclari_toplar_bos_kalirsa_rapor(string input, string expected)
    {
        Assert.Equal(expected, ReportEndpoints.Slugify(input));
    }

    [Fact]
    public void Slugify_60_karakterde_keser()
    {
        var slug = ReportEndpoints.Slugify(new string('a', 80));
        Assert.Equal(60, slug.Length);
    }

    [Theory]
    [InlineData("tr", "-tr")]
    [InlineData("TR", "-tr")]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("t r", "")]
    [InlineData("x1", "")]
    [InlineData("../x", "")]
    public void LangSuffix_yalniz_harf_kabul_eder(string? lang, string expected)
    {
        // Değer istemciden gelir ve dosya adına girer — harf dışı her şey
        // sessizce düşer, ek hiç üretilmez.
        Assert.Equal(expected, ReportEndpoints.LangSuffix(lang));
    }

    [Fact]
    public void FileDate_belge_tarihini_iso_yapar_bozuksa_kayit_zamanina_duser()
    {
        var fallback = new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal("2026-07-29", ReportEndpoints.FileDate("29.07.2026", fallback));
        Assert.Equal("2026-07-31", ReportEndpoints.FileDate("bozuk", fallback));
    }
}

using Alp.Reports;

namespace Alp.Api.Tests;

// XlsxReportBuilder'ın saf yardımcıları — 340 satırlık dizgicinin kullanıcı
// verisine dokunan iki ucu. Sayfa adı Excel'in katı kurallarına çarpar,
// hücre metni CSV yeniden dışa aktarımında formüle dönüşebilir.
public class XlsxHelperTests
{
    [Theory]
    [InlineData("Yol: Genişliği/Akım", "Yol- Genişliği-Akım")]
    [InlineData("kisa ad", "kisa ad")]
    public void SanitizeSheetName_yasak_karakterleri_degistirir(string input, string expected)
    {
        Assert.Equal(expected, XlsxReportBuilder.SanitizeSheetName(input, "Hesap"));
    }

    [Fact]
    public void SanitizeSheetName_31i_asan_adi_kelime_sinirinda_keser()
    {
        var name = XlsxReportBuilder.SanitizeSheetName("1 Yol Genişliği ve Akım Kapasitesi", "Hesap");

        Assert.True(name.Length <= 31);
        // Yarım kelimeyle bitmemeli — kesme boşlukta yapılır.
        Assert.False(name.EndsWith(' '));
        Assert.Equal("1 Yol Genişliği ve Akım", name);
    }

    [Fact]
    public void SanitizeSheetName_bos_kalirsa_yedek_ad()
    {
        Assert.Equal("Hesap", XlsxReportBuilder.SanitizeSheetName("   ", "Hesap"));
    }

    [Theory]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+HÜCRE", "'+HÜCRE")]
    [InlineData("@cmd", "'@cmd")]
    [InlineData("-cmd()", "'-cmd()")]
    public void GuardFormulaLeadIn_formul_baslangicini_tirnaklar(string input, string expected)
    {
        Assert.Equal(expected, XlsxReportBuilder.GuardFormulaLeadIn(input));
    }

    [Theory]
    [InlineData("-5")]
    [InlineData("-0.25")]
    [InlineData("normal metin")]
    [InlineData("")]
    [InlineData("—")]
    public void GuardFormulaLeadIn_sayilara_ve_duz_metne_dokunmaz(string input)
    {
        // Negatif sayı zaten sayı hücresine gider; buraya düşse bile
        // bozulmamalı — mühendislik değeri tırnaklanırsa kopyala-yapıştır
        // akışı kirlenir.
        Assert.Equal(input, XlsxReportBuilder.GuardFormulaLeadIn(input));
    }
}

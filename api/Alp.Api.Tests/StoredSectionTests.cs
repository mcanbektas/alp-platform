using Alp.Api.Projects;

namespace Alp.Api.Tests;

// StoredSection.Read iki kayıt biçimini (eski tek dilli kök / yeni dil
// haritası) ayırt eder ve eksik dizi alanlarını onarır. Normalize'ın
// null-dizi onarımı üretimde yaşanmış bir 500'ü düzeltmişti ve regresyon
// testi yoktu.
public class StoredSectionTests
{
    [Fact]
    public void Eski_tek_dilli_kayit_kokten_okunur()
    {
        var section = StoredSection.Read("""{"toolName":"Yol Genişliği","results":[]}""", "en");

        Assert.NotNull(section);
        Assert.Equal("Yol Genişliği", section!.ToolName);
    }

    [Fact]
    public void Dil_haritasinda_istenen_dil_secilir()
    {
        var json = """{"tr":{"toolName":"Yol Genişliği"},"en":{"toolName":"Trace Width"}}""";

        Assert.Equal("Trace Width", StoredSection.Read(json, "en")!.ToolName);
        Assert.Equal("Yol Genişliği", StoredSection.Read(json, "tr")!.ToolName);
    }

    [Fact]
    public void Istenen_dil_yoksa_eldekine_dusulur()
    {
        // Yanlış dilde bölüm, hiç bölüm olmamasından iyidir — hesap rapordan
        // sessizce düşerse kullanıcı sebebini göremez.
        var json = """{"tr":{"toolName":"Yol Genişliği"}}""";

        Assert.Equal("Yol Genişliği", StoredSection.Read(json, "en")!.ToolName);
    }

    [Fact]
    public void Eksik_dizi_alanlari_bos_listeye_onarilir()
    {
        // Yalnızca toolName taşıyan kayıt: Inputs/Formula/Results/Notes JSON'da
        // hiç yok ve serializer null bırakır — dizgici sayarken 500'e düşerdi.
        var section = StoredSection.Read("""{"toolName":"X"}""", "tr");

        Assert.NotNull(section);
        Assert.Empty(section!.Inputs);
        Assert.Empty(section.Formula);
        Assert.Empty(section.Results);
        Assert.Empty(section.Notes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{bozuk json")]
    [InlineData("[1,2,3]")]
    [InlineData("\"düz dize\"")]
    public void Okunamayan_kayit_null_doner_firlatmaz(string? reportJson)
    {
        Assert.Null(StoredSection.Read(reportJson, "tr"));
    }
}

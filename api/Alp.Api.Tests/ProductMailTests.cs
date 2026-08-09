using Alp.Api.Auth;
using Microsoft.Extensions.Configuration;

namespace Alp.Api.Tests;

// Ürün başına posta yapılandırması (Faz 3, CLAUDE.md "Bilinen borç").
//
// Kural: PCB'nin eski davranışı (App:FrontendBaseUrl tek anahtarı) hiç
// yapılandırma değişmeden aynı kalmalı; Comm ise hem kendi anahtarından
// okunabilmeli hem de HİÇ ayarlanmamışken kırık bağlantı üretmeden makul bir
// varsayılana düşmeli.
public class ProductMailTests
{
    private static IConfiguration ConfigFrom(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void Pcb_eski_FrontendBaseUrl_anahtarini_aynen_kullanir()
    {
        var config = ConfigFrom(new() { ["App:FrontendBaseUrl"] = "https://pcb.ornek.test" });

        var branding = ProductMail.Resolve(config, product: null, lang: "tr");

        Assert.Equal("https://pcb.ornek.test", branding.BaseUrl);
        Assert.Equal("ALP PCB Toolkit", branding.Brand);
        Assert.Equal("/e-posta-dogrula", branding.ConfirmEmailPath);
    }

    [Fact]
    public void Tanimsiz_urun_pcb_sayilir()
    {
        var config = ConfigFrom(new() { ["App:FrontendBaseUrl"] = "https://pcb.ornek.test" });

        var branding = ProductMail.Resolve(config, product: "bilinmeyen", lang: "tr");

        Assert.Equal("https://pcb.ornek.test", branding.BaseUrl);
    }

    [Fact]
    public void Comm_kendi_anahtarindan_okunur()
    {
        var config = ConfigFrom(new()
        {
            ["App:FrontendBaseUrl"] = "https://pcb.ornek.test",
            ["App:Products:comm:FrontendBaseUrl"] = "https://comm.ornek.test",
        });

        var branding = ProductMail.Resolve(config, product: "comm", lang: "en");

        Assert.Equal("https://comm.ornek.test", branding.BaseUrl);
        Assert.Equal("ALP Comm Toolkit", branding.Brand);
        Assert.Equal("/en/confirm-email", branding.ConfirmEmailPath);
    }

    [Fact]
    public void Comm_ayarlanmamissa_varsayilana_duser()
    {
        var config = ConfigFrom(new() { ["App:FrontendBaseUrl"] = "https://pcb.ornek.test" });

        var branding = ProductMail.Resolve(config, product: "comm", lang: "tr");

        Assert.Equal("http://localhost:3001", branding.BaseUrl);
        Assert.Equal("ALP Comm Toolkit", branding.Brand);
    }

    [Fact]
    public void Urun_karsilastirmasi_buyuk_kucuk_harfe_duyarsizdir()
    {
        var config = ConfigFrom(new() { ["App:Products:comm:FrontendBaseUrl"] = "https://comm.ornek.test" });

        var branding = ProductMail.Resolve(config, product: "COMM", lang: "tr");

        Assert.Equal("https://comm.ornek.test", branding.BaseUrl);
    }
}

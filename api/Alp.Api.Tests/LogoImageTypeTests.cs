using Alp.Api.Auth;

namespace Alp.Api.Tests;

// Logo türü DOSYANIN KENDİSİNDEN okunur.
//
// `Content-Type` başlığı ve uzantı istemcinin iddiasıdır; ikisi de serbestçe
// uydurulur. Kabul edilen iki biçimin sihirli baytları sabittir ve başka hiçbir
// şey saklanmaz — böylece "logo" alanı rastgele veri taşıyan bir depoya dönüşemez.
public class LogoImageTypeTests
{
    private static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF];

    private static byte[] With(byte[] magic, int payload = 64) =>
        [.. magic, .. Enumerable.Repeat((byte)0x20, payload)];

    [Fact]
    public void Png_sihirli_baytlari_png_verir()
    {
        Assert.Equal("image/png", AuthEndpoints.DetectImageType(With(Png)));
    }

    [Fact]
    public void Jpeg_sihirli_baytlari_jpeg_verir()
    {
        Assert.Equal("image/jpeg", AuthEndpoints.DetectImageType(With(Jpeg)));
    }

    [Fact]
    public void Gif_reddedilir()
    {
        Assert.Null(AuthEndpoints.DetectImageType(With([(byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a'])));
    }

    [Fact]
    public void Svg_reddedilir()
    {
        // SVG metindir ve betik taşıyabilir; logo alanına hiç girmemeli.
        Assert.Null(AuthEndpoints.DetectImageType(System.Text.Encoding.UTF8.GetBytes("<svg viewBox=\"0 0 1 1\"></svg>")));
    }

    [Fact]
    public void Uzantisi_png_olan_duz_metin_reddedilir()
    {
        Assert.Null(AuthEndpoints.DetectImageType(System.Text.Encoding.UTF8.GetBytes("bu bir resim değil")));
    }

    [Fact]
    public void Bos_bayt_dizisi_reddedilir()
    {
        Assert.Null(AuthEndpoints.DetectImageType([]));
    }

    [Fact]
    public void Yalnizca_sihirli_bayt_kadar_uzun_dosya_reddedilir()
    {
        // Sınır dahil değil: sekiz baytlık bir "PNG" gövdesiz demektir.
        Assert.Null(AuthEndpoints.DetectImageType(Png));
        Assert.Null(AuthEndpoints.DetectImageType(Jpeg));
    }

    [Fact]
    public void Kesik_sihirli_bayt_reddedilir()
    {
        Assert.Null(AuthEndpoints.DetectImageType([0x89, (byte)'P', (byte)'N']));
    }

    [Fact]
    public void Sihirli_bayt_bastan_baska_yerdeyse_reddedilir()
    {
        // Gövdenin içine gömülü imza kabul edilmez; imza dosyanın BAŞINDA olmalı.
        Assert.Null(AuthEndpoints.DetectImageType([.. new byte[] { 0x00, 0x01 }, .. Png]));
    }
}

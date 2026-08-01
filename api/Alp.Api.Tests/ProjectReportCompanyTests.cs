using Alp.Api.Reports;
using Alp.Reports;

namespace Alp.Api.Tests;

// Proje raporu künyesindeki FİRMA alanının üç durumu.
//
// Firma eskiden yalnızca profilden okunuyordu ve kullanıcı belgede ne
// yazacağını indirme anında göremiyordu. Alan artık indirme kartında duruyor,
// profilden dolu geliyor ve düzenlenebilir; düzenleme profili DEĞİŞTİRMEZ.
//
// Üç durumun ayrı anlamı var ve karışırsa kusur sessizdir — belge üretilir,
// yalnız künyesi yanlış olur:
//   null      → alan hiç gönderilmemiş; profildeki firma yazılır
//   boş dize  → kullanıcı alanı bilerek boşalttı; firma YAZILMAZ
//   dolu      → tek seferlik değer yazılır, profil değişmez
// Boşaltmayı `null` saysaydık firmayı kaldırmak isteyen kullanıcı sessizce
// profildekini alırdı — bu sınıfın asıl koruduğu kural budur.
public class ProjectReportCompanyTests
{
    // Bölüm gövdesi bu testlerin konusu değil; künye sınanıyor. Tek geçerli
    // bölüm yeterli — bölümsüz projede yük hiç kurulmaz (ayrı kural).
    private const string GecerliBolum = """{"toolName":"Yol Genişliği"}""";

    private static readonly ReportLabels Etiketler = new(
        "Özet", "Hazırlayan", "Firma", "Tarih", "Hesap", "Girdiler", "Sonuçlar",
        "Denklemler", "Notlar", "Grafik verisi", "ipucu", "tek", "çok", "şema yok", "grafik yok");

    private static async Task<ReportPayload?> Kunye(TestDb host, string? sirket, string? profil)
    {
        var user = host.AddUser("a@alp.local");
        if (profil is not null)
        {
            user.Company = profil;
            host.Db.SaveChanges();
        }
        var project = host.AddProject(user);
        host.AddCalculation(project, reportJson: GecerliBolum);

        var (payload, _, _) = await ReportEndpoints.ProjectPayload(
            host.Db, project.Id, user.Id, "Rapor", "Alp Test", sirket, "31.07.2026", Etiketler, "tr");
        return payload;
    }

    [Fact]
    public async Task Alan_gonderilmemisse_profildeki_firma_yazilir()
    {
        using var host = new TestDb();

        var payload = await Kunye(host, sirket: null, profil: "ALP PCB");

        Assert.Equal("ALP PCB", payload!.Company);
    }

    [Fact]
    public async Task Dolu_alan_profili_ezer_ve_profil_degismez()
    {
        using var host = new TestDb();

        var payload = await Kunye(host, sirket: "Tek Seferlik A.Ş.", profil: "ALP PCB");

        Assert.Equal("Tek Seferlik A.Ş.", payload!.Company);
        // Kalıcı değişiklik Hesabım ekranının işi: rapor indirmek profili
        // güncellemez.
        Assert.Equal("ALP PCB", host.NewContext().Users.Single().Company);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Bosaltilan_alan_profildekine_DUSMEZ(string sirket)
    {
        using var host = new TestDb();

        var payload = await Kunye(host, sirket, profil: "ALP PCB");

        // Asıl kural: boş dize "profildekini kullan" demek DEĞİLDİR.
        Assert.Null(payload!.Company);
    }

    [Fact]
    public async Task Profil_de_alan_da_bossa_firma_yazilmaz()
    {
        using var host = new TestDb();

        var payload = await Kunye(host, sirket: null, profil: null);

        Assert.Null(payload!.Company);
    }

    [Fact]
    public async Task Bos_dize_null_olarak_gecer_dizgiciye_bos_satir_gitmez()
    {
        using var host = new TestDb();

        var payload = await Kunye(host, sirket: "  ", profil: null);

        // Dizgici `null` bekliyor; boş dize künyede boş bir satır bırakırdı.
        Assert.Null(payload!.Company);
    }
}

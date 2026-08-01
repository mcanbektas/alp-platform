using Alp.Api.Projects;
using Alp.Api.Reports;
using Alp.Domain;
using Alp.Reports;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Alp.Api.Tests;

// Rapor anlık görüntüsü (docs/rapor-snapshot-karari.md).
//
// Sınanan kural tek cümle: **geçmişten indirme, raporun ÜRETİLDİĞİ ANDAKİ
// içeriği basar.** Bu kural derlemeden ve öteki testlerden kaçar, çünkü
// bozulduğunda yanıt yine 200'dür ve belge yine üretilir — yalnız içindeki
// sayı bugünkü sayıdır. Kütükteki tarihle içerik sessizce ayrışır.
//
// Belge dizgisi burada taklit edilir (`Build`): kural yükün NEREDEN geldiğiyle
// ilgili, PDF'in nasıl göründüğüyle değil.
public class ReportSnapshotTests : IDisposable
{
    private readonly TestDb db = new();

    public void Dispose() => db.Dispose();

    private static readonly ReportLabels Labels = new(
        "Summary", "Prepared by", "Company", "Date", "Calculation",
        "Inputs", "Results", "Equations", "Notes", "Chart data",
        "chart hint", "hint single", "hint many",
        "schematic failed", "chart failed");

    private static IConfiguration Config(long? quotaBytes = null) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["App:SnapshotQuotaBytes"] = quotaBytes?.ToString(),
        })
        .Build();

    // Kaydedilmiş bölüm: dil haritası şekli (istemcinin bugün yazdığı şekil).
    private static string SectionJson(string width) =>
        "{\"tr\":{\"toolName\":\"Yol Genişliği\",\"results\":"
        + $"[{{\"label\":\"Genişlik\",\"value\":\"{width}\",\"unit\":\"mm\"}}]}},"
        + "\"en\":{\"toolName\":\"Trace Width\",\"results\":"
        + $"[{{\"label\":\"Width\",\"value\":\"{width}\",\"unit\":\"mm\"}}]}}}}";

    private static byte[] Build(ReportPayload payload) => [1, 2, 3];

    private static ProjectReportRequest Request(string? company = null) =>
        new("DONANIM RAPORU", "Alp Test", company, "01.08.2026", Labels, "tr");

    private async Task<Guid> GenerateAsync(
        ApplicationUser user, Project project, IConfiguration? config = null, string? company = null)
    {
        // Yeni kütük satırı FARKLA bulunur, zaman damgasıyla değil: iki rapor
        // arka arkaya üretildiğinde `GeneratedAt` aynı tike düşebilir ve
        // "en yenisi" belirsizleşirdi.
        var before = await db.Db.Reports.Select(r => r.Id).ToListAsync();

        var result = await ReportEndpoints.GenerateProjectReport(
            project.Id, Request(company), db.Db, TestHttp.For(user), config ?? Config(),
            ReportFormat.Pdf, Build);

        Assert.Equal(StatusCodes.Status200OK, (result as IStatusCodeHttpResult)?.StatusCode ?? 200);

        var after = await db.Db.Reports.Select(r => r.Id).ToListAsync();
        return Assert.Single(after.Except(before));
    }

    private Task<(ReportEndpoints.DownloadPayload? Source, IResult? Error)> DownloadAsync(
        ApplicationUser user, Guid reportId, string lang = "tr") =>
        ReportEndpoints.DownloadSource(db.Db, reportId, user.Id, new ReportLabelsRequest(Labels, lang));

    private static string FirstValue(ReportPayload payload) => payload.Sections[0].Results![0].Value;

    [Fact]
    public async Task Gecmisten_indirme_o_gunku_sayiyi_basar()
    {
        var user = db.AddUser("kullanici@test.local");
        var project = db.AddProject(user);
        var calculation = db.AddCalculation(project, reportJson: SectionJson("0.8"));

        var reportId = await GenerateAsync(user, project);

        // Rapor basıldıktan SONRA hesap değişiyor — kusurun senaryosu bu.
        calculation.ReportJson = SectionJson("1.2");
        db.Db.SaveChanges();

        var (source, error) = await DownloadAsync(user, reportId);

        Assert.Null(error);
        Assert.Equal("0.8", FirstValue(source!.Payload));
    }

    [Fact]
    public async Task Yeni_rapor_yeni_sayiyi_dondurur()
    {
        var user = db.AddUser("kullanici@test.local");
        var project = db.AddProject(user);
        var calculation = db.AddCalculation(project, reportJson: SectionJson("0.8"));

        var eski = await GenerateAsync(user, project);
        calculation.ReportJson = SectionJson("1.2");
        db.Db.SaveChanges();
        var yeni = await GenerateAsync(user, project);

        Assert.Equal("0.8", FirstValue((await DownloadAsync(user, eski)).Source!.Payload));
        Assert.Equal("1.2", FirstValue((await DownloadAsync(user, yeni)).Source!.Payload));
    }

    // Donan şey İÇERİKTİR, dil değil: aynı rapor sonradan öbür dilde de
    // indirilebilir (karar §1 — bayt saklama bu yeteneği öldürürdü).
    [Fact]
    public async Task Donmus_rapor_obur_dilde_de_indirilebilir()
    {
        var user = db.AddUser("kullanici@test.local");
        var project = db.AddProject(user);
        db.AddCalculation(project, reportJson: SectionJson("0.8"));

        var reportId = await GenerateAsync(user, project);
        var (source, _) = await DownloadAsync(user, reportId, "en");

        Assert.Equal("Trace Width", source!.Payload.Sections[0].ToolName);
        Assert.Equal("0.8", FirstValue(source.Payload));
    }

    // Aynı içerik iki raporda tek satır — israfı önleyen asıl kural (karar §1).
    [Fact]
    public async Task Ayni_bolum_iki_raporda_tek_blob_tutar()
    {
        var user = db.AddUser("kullanici@test.local");
        var project = db.AddProject(user);
        db.AddCalculation(project, reportJson: SectionJson("0.8"));

        await GenerateAsync(user, project);
        await GenerateAsync(user, project);

        Assert.Equal(1, await db.Db.SectionBlobs.CountAsync());
        Assert.Equal(2, await db.Db.ReportSnapshotSections.CountAsync());
    }

    [Fact]
    public async Task Silinen_proje_donmus_raporu_engellemez()
    {
        var user = db.AddUser("kullanici@test.local");
        var project = db.AddProject(user);
        db.AddCalculation(project, reportJson: SectionJson("0.8"));

        var reportId = await GenerateAsync(user, project);

        db.Db.Projects.Remove(project);
        db.Db.SaveChanges();

        var (source, error) = await DownloadAsync(user, reportId);

        Assert.Null(error);
        Assert.Equal("0.8", FirstValue(source!.Payload));
    }

    // Göç öncesi kayıtlar: manifest yok, davranış eskisi gibi kalmalı.
    [Fact]
    public async Task Snapshotsiz_rapor_eski_davranisi_surdurur()
    {
        var user = db.AddUser("kullanici@test.local");
        var project = db.AddProject(user);
        var calculation = db.AddCalculation(project, reportJson: SectionJson("0.8"));

        var report = new Report
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            UserId = user.Id,
            Title = "DONANIM RAPORU",
            PreparedBy = "Alp Test",
            Format = ReportFormat.Pdf,
            GeneratedAt = DateTimeOffset.UtcNow,
        };
        db.Db.Reports.Add(report);
        db.Db.SaveChanges();

        calculation.ReportJson = SectionJson("1.2");
        db.Db.SaveChanges();

        var (source, error) = await DownloadAsync(user, report.Id);

        Assert.Null(error);
        Assert.Equal("1.2", FirstValue(source!.Payload));
    }

    // Snapshot'sız ve projesi de silinmiş rapor: bugünkü 409 sözleşmesi durur.
    [Fact]
    public async Task Snapshotsiz_ve_projesiz_rapor_409_dondurur()
    {
        var user = db.AddUser("kullanici@test.local");
        var report = new Report
        {
            Id = Guid.NewGuid(),
            ProjectId = null,
            UserId = user.Id,
            Title = "DONANIM RAPORU",
            PreparedBy = "Alp Test",
            Format = ReportFormat.Pdf,
            GeneratedAt = DateTimeOffset.UtcNow,
        };
        db.Db.Reports.Add(report);
        db.Db.SaveChanges();

        var (source, error) = await DownloadAsync(user, report.Id);

        Assert.Null(source);
        Assert.Equal(StatusCodes.Status409Conflict, ResultAssert.Status(error!));
    }

    // Künyedeki firma da donar (§27 boşluğu): profil sonradan değişse bile
    // o günkü belge o günkü firmayı taşır.
    [Fact]
    public async Task Firma_kunyede_donar()
    {
        var user = db.AddUser("kullanici@test.local");
        user.Company = "Eski Firma";
        db.Db.SaveChanges();

        var project = db.AddProject(user);
        db.AddCalculation(project, reportJson: SectionJson("0.8"));

        var reportId = await GenerateAsync(user, project);

        user.Company = "Yeni Firma";
        db.Db.SaveChanges();

        var (source, _) = await DownloadAsync(user, reportId);

        Assert.Equal("Eski Firma", source!.Payload.Company);
    }

    // Kota aşıldığında rapor reddedilmez; en eski snapshot düşer ve o rapor
    // "güncelden üret" davranışına geriler (karar §2).
    [Fact]
    public async Task Kota_asilinca_en_eski_snapshot_dusurulur()
    {
        var user = db.AddUser("kullanici@test.local");
        var project = db.AddProject(user);
        var calculation = db.AddCalculation(project, reportJson: SectionJson("0.8"));

        // Kota tek bölümü bile taşıyamayacak kadar küçük: her yeni rapordan
        // sonra kendinden öncekiler düşer.
        var config = Config(quotaBytes: 10);

        var eski = await GenerateAsync(user, project, config);
        // İki rapor aynı tike düşerse "en eski" belirsiz kalırdı; kural
        // zamana bağlı olduğu için zaman damgası açıkça ayrılır.
        (await db.Db.Reports.FirstAsync(r => r.Id == eski)).GeneratedAt =
            DateTimeOffset.UtcNow.AddDays(-1);
        db.Db.SaveChanges();

        calculation.ReportJson = SectionJson("1.2");
        db.Db.SaveChanges();
        var yeni = await GenerateAsync(user, project, config);

        // Kütük satırları duruyor.
        Assert.Equal(2, await db.Db.Reports.CountAsync());
        // En yeni rapor donmuş kalır, eskisi geriler.
        Assert.False(await db.Db.ReportSnapshotSections.AnyAsync(s => s.ReportId == eski));
        Assert.True(await db.Db.ReportSnapshotSections.AnyAsync(s => s.ReportId == yeni));

        // Gerileyen rapor artık güncel içerikten üretilir.
        Assert.Equal("1.2", FirstValue((await DownloadAsync(user, eski)).Source!.Payload));
    }

    [Fact]
    public async Task Rapor_silinince_sahipsiz_blob_toplanir()
    {
        var user = db.AddUser("kullanici@test.local");
        var project = db.AddProject(user);
        db.AddCalculation(project, reportJson: SectionJson("0.8"));

        var reportId = await GenerateAsync(user, project);
        Assert.Equal(1, await db.Db.SectionBlobs.CountAsync());

        db.Db.Reports.Remove(await db.Db.Reports.FirstAsync(r => r.Id == reportId));
        db.Db.SaveChanges();

        // Manifest kaskadla gitti, blob sahipsiz kaldı.
        Assert.Empty(await db.Db.ReportSnapshotSections.ToListAsync());
        Assert.Equal(1, await db.Db.SectionBlobs.CountAsync());

        var freed = await ReportSnapshot.CollectOrphansAsync(db.Db, user.Id);

        Assert.True(freed > 0);
        Assert.Equal(0, await db.Db.SectionBlobs.CountAsync());
    }

    // Hâlâ bir manifest tarafından gösterilen blob toplanmaz — dedup edilmiş
    // bir bölümün başka bir raporu düşürmesi, o raporu sessizce boşaltırdı.
    [Fact]
    public async Task Kullanilan_blob_toplanmaz()
    {
        var user = db.AddUser("kullanici@test.local");
        var project = db.AddProject(user);
        db.AddCalculation(project, reportJson: SectionJson("0.8"));

        var ilk = await GenerateAsync(user, project);
        await GenerateAsync(user, project);

        db.Db.Reports.Remove(await db.Db.Reports.FirstAsync(r => r.Id == ilk));
        db.Db.SaveChanges();

        var freed = await ReportSnapshot.CollectOrphansAsync(db.Db, user.Id);

        Assert.Equal(0, freed);
        Assert.Equal(1, await db.Db.SectionBlobs.CountAsync());
    }

    // Dedup KULLANICI sınırındadır: iki kullanıcının aynı içeriği asla aynı
    // satırı paylaşmaz, yoksa biri hesabını silince öbürünün raporu boşalırdı.
    [Fact]
    public async Task Dedup_kullanici_sinirinda_kalir()
    {
        var birinci = db.AddUser("bir@test.local");
        var ikinci = db.AddUser("iki@test.local");
        var p1 = db.AddProject(birinci);
        var p2 = db.AddProject(ikinci);
        db.AddCalculation(p1, reportJson: SectionJson("0.8"));
        db.AddCalculation(p2, reportJson: SectionJson("0.8"));

        await GenerateAsync(birinci, p1);
        await GenerateAsync(ikinci, p2);

        Assert.Equal(2, await db.Db.SectionBlobs.CountAsync());
        Assert.Equal(1, await db.Db.SectionBlobs.CountAsync(b => b.UserId == birinci.Id));
    }
}

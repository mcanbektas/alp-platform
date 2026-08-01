using Alp.Data;
using Microsoft.EntityFrameworkCore;

namespace Alp.Api.Reports;

// Rapor anlık görüntülerinin bakım turu (docs/rapor-snapshot-karari.md §2).
//
// Kota, üretim isteğinin İÇİNDE de uygulanır (ReportEndpoints.Freeze) — o
// yüzden buradaki tur bir güvenlik ağıdır, tek savunma değil. İki iş yapar:
//
//   1. SAHİPSİZ BLOB TOPLAMA. Rapor silindiğinde manifesti de gider (Cascade)
//      ama blob kalır; hiçbir manifestin göstermediği blob burada silinir.
//      Silme kaskadı uygulama kodundan geçmediği için (hesap/proje silme,
//      elle müdahale) bu tur olmadan tablo sessizce büyürdü.
//   2. KOTA GERİLETMESİ. Yapılandırma sonradan küçültülürse ya da bir yazma
//      turu yarıda kaldıysa, kotayı aşan kullanıcının en eski snapshot'ları
//      düşürülür ve o raporlar "güncelden üret" davranışına geriler.
//
// Kütük satırı (Reports) HİÇBİR koşulda silinmez: kullanıcı raporu ne zaman
// aldığını görmeye devam eder, yalnız donmuşluk kaybolur.
public sealed class ReportSnapshotCleanupService(
    IServiceScopeFactory scopeFactory,
    IConfiguration config,
    ILogger<ReportSnapshotCleanupService> logger) : BackgroundService
{
    // Yenileme token'ı turuyla aynı tempo: bakım işidir, sıkı zamanlama
    // gerektirmez.
    private static readonly TimeSpan Period = TimeSpan.FromHours(6);

    // Açılışta migration ve sağlık kontrolleriyle yarışmasın diye kısa gecikme.
    // Token turuyla aynı dakikaya düşmesin diye bir tık sonra başlar.
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunOnce(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Temizlik düşerse uygulama düşmez — bir sonraki turda
                    // yeniden denenir. Sessiz de kalmaz.
                    logger.LogError(ex, "Rapor anlık görüntüsü bakımı başarısız — sonraki turda yeniden denenecek.");
                }

                await Task.Delay(Period, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Uygulama kapanıyor.
        }
    }

    private async Task RunOnce(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var quota = ReportEndpoints.SnapshotQuotaBytes(config);

        // Yalnızca blob'u olan kullanıcılar dolaşılır; tablo boşsa tur da boş.
        var owners = await db.SectionBlobs
            .Select(b => b.UserId)
            .Distinct()
            .ToListAsync(ct);

        long freed = 0;
        foreach (var userId in owners)
        {
            freed += await ReportSnapshot.CollectOrphansAsync(db, userId, ct);
            await ReportSnapshot.EnforceQuotaAsync(db, userId, quota, ct);
        }

        if (freed > 0)
        {
            logger.LogInformation("Sahipsiz rapor bölümü temizlendi: {Bytes} karakter.", freed);
        }
    }
}

using Alp.Data;
using Microsoft.EntityFrameworkCore;

namespace Alp.Api.Auth;

// AuditEvents sınırsız büyürdü — burada yalnızca App:AuditRetentionDays gün
// sınırından ESKİ kayıtlar silinir (docs/brifler/11-loglama.md §5, F3).
// Sınır dolmamış hiçbir satıra dokunulmaz. Saklama süresi Gizlilik ve KVKK
// metinlerinde beyan edilen süreyle AYNI kaynaktan anlatılır — buradaki
// varsayılan değişirse web/src/data/legalText.js'teki cümle de güncellenir.
public sealed class AuditCleanupService(
    IServiceScopeFactory scopeFactory,
    IConfiguration config,
    ILogger<AuditCleanupService> logger) : BackgroundService
{
    // RefreshTokenCleanupService ile aynı tempo — bakım işidir, sıkı
    // zamanlama gerektirmez.
    private static readonly TimeSpan Period = TimeSpan.FromHours(6);

    // Açılışta migration ve sağlık kontrolleriyle yarışmasın diye kısa bir
    // gecikmeyle başlar.
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(1);

    private const int DefaultRetentionDays = 365;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var cutoff = DateTimeOffset.UtcNow.AddDays(-RetentionDays(config));
                    var removed = await db.AuditEvents
                        .Where(e => e.OccurredAt <= cutoff)
                        .ExecuteDeleteAsync(stoppingToken);

                    if (removed > 0)
                    {
                        logger.LogInformation("Saklama süresi dolmuş {Count} denetim izi kaydı silindi.", removed);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Temizlik düşerse uygulama düşmez — bir sonraki turda
                    // yeniden denenir. Sessiz de kalmaz.
                    logger.LogError(ex, "Denetim izi temizliği başarısız — sonraki turda yeniden denenecek.");
                }

                await Task.Delay(Period, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Kapanış — olağan yol.
        }
    }

    internal static int RetentionDays(IConfiguration config)
    {
        var raw = config["App:AuditRetentionDays"];
        return int.TryParse(raw, out var value) && value > 0 ? value : DefaultRetentionDays;
    }
}

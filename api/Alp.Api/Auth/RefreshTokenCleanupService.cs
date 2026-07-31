using Alp.Data;
using Microsoft.EntityFrameworkCore;

namespace Alp.Api.Auth;

// RefreshTokens tablosu her yenilemede bir satır büyür (rotasyon) ve hiçbir
// yol eskiyi silmiyordu — tablo sınırsız büyüyordu. Burada yalnızca SÜRESİ
// DOLMUŞ satırlar silinir; iptal edilmiş ama süresi dolmamış satırlar bilerek
// tutulur: çalıntı-tekrar tespiti (AuthEndpoints → Refresh, RevokedAt dalı)
// döndürülmüş token'ın satırını bulabilmeye dayanır. Süresi dolan token zaten
// hem kullanılamaz hem de tespit değeri kalmamıştır.
public sealed class RefreshTokenCleanupService(
    IServiceScopeFactory scopeFactory,
    ILogger<RefreshTokenCleanupService> logger) : BackgroundService
{
    // Sıkı bir zamanlama gerekmez — günde birkaç tur tabloyu düz tutar.
    private static readonly TimeSpan Period = TimeSpan.FromHours(6);

    // Açılışta migration ve sağlık kontrolleriyle yarışmasın diye kısa bir
    // gecikmeyle başlar.
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(1);

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
                    var now = DateTimeOffset.UtcNow;
                    var removed = await db.RefreshTokens
                        .Where(t => t.ExpiresAt <= now)
                        .ExecuteDeleteAsync(stoppingToken);

                    if (removed > 0)
                    {
                        logger.LogInformation("Süresi dolmuş {Count} yenileme token'ı silindi.", removed);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Temizlik düşerse uygulama düşmez — bir sonraki turda
                    // yeniden denenir. Sessiz de kalmaz.
                    logger.LogError(ex, "Yenileme token'ı temizliği başarısız — sonraki turda yeniden denenecek.");
                }

                await Task.Delay(Period, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Kapanış — olağan yol.
        }
    }
}

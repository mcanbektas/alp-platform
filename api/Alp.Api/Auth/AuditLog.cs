using System.Text.Json;
using Alp.Data;
using Alp.Domain;

namespace Alp.Api.Auth;

// Olay seti v1, dar tutulur (docs/brifler/11-loglama.md §3). Genişletmek ayrı
// karar — burada elle çoğaltılan tek bir kaynak.
internal static class AuditEventCodes
{
    public const string AccountRegistered = "account.registered";
    public const string AccountEmailConfirmed = "account.email-confirmed";
    public const string AuthPasswordChanged = "auth.password-changed";
    public const string AuthPasswordReset = "auth.password-reset";
    public const string AuthLockout = "auth.lockout";
    public const string AdminUserDeleted = "admin.user-deleted";
    public const string AdminRoleGranted = "admin.role-granted";
    public const string AdminRoleRevoked = "admin.role-revoked";
}

// Denetim izinin TEK yazım noktası — uçlar elle `db.AuditEvents.Add` yazmaz.
// Tabloya yazar VE `ILogger`a tek satır düşürür; tablo kanoniktir, log satırı
// yalnızca operasyonel görünürlüktür.
//
// Dayanıklılık sözleşmesi iki kademeli:
//   - WriteCriticalAsync: çağıranın açık transaction'ı İÇİNDE, aynı `db`
//     üzerinden koşar ve patlarsa YUKARI FIRLATIR. admin.user-deleted bunu
//     kullanır — iz yazılamıyorsa silme de olmasın diye.
//   - WriteAsync: best-effort. Yazım patlarsa yutulur, yalnız ILogger'a
//     düşer; ana akış audit satırı yüzünden asla düşmez.
internal sealed class AuditLog(AppDbContext db, ILogger<AuditLog> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task WriteCriticalAsync(
        string @event,
        ApplicationUser? actor,
        ApplicationUser? target,
        HttpContext? http = null,
        object? detail = null,
        CancellationToken ct = default) =>
        SaveAsync(@event, actor, target, http, detail, ct);

    public async Task WriteAsync(
        string @event,
        ApplicationUser? actor,
        ApplicationUser? target,
        HttpContext? http = null,
        object? detail = null,
        CancellationToken ct = default)
    {
        try
        {
            await SaveAsync(@event, actor, target, http, detail, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Denetim izi yazılamadı: {Event}", @event);
        }
    }

    private async Task SaveAsync(
        string @event, ApplicationUser? actor, ApplicationUser? target,
        HttpContext? http, object? detail, CancellationToken ct)
    {
        var entry = new AuditEvent
        {
            OccurredAt = DateTimeOffset.UtcNow,
            Event = @event,
            ActorUserId = actor?.Id,
            ActorEmail = actor?.Email,
            TargetUserId = target?.Id,
            TargetEmail = target?.Email,
            Ip = http?.Connection.RemoteIpAddress?.ToString(),
            DetailJson = detail is null ? null : JsonSerializer.Serialize(detail, JsonOptions),
        };

        db.AuditEvents.Add(entry);
        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Denetim: {Event} actor={ActorId} target={TargetId}", @event, entry.ActorUserId, entry.TargetUserId);
    }
}

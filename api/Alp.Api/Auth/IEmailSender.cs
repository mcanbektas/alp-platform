namespace Alp.Api.Auth;

public interface IEmailSender
{
    Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken ct = default);
}

// Geliştirme sırasında SMTP gerekmesin diye: e-postayı göndermek yerine
// konsola yazar. docs/uyelik-ve-rapor-plani.md §13 madde 2 — sağlayıcı
// seçilince gerçek SmtpEmailSender bunun yerini alır, arayüz değişmez.
//
// Gövde yalnızca GELİŞTİRMEDE yazılır. Üretimde SMTP kurulmadan bu sınıf
// devredeyse gövde, parola sıfırlama ve e-posta doğrulama TOKEN'larını taşır —
// log'a düşen token, log'u okuyabilen herkese hesap devralma yolu demektir.
// Üretim yalnızca alıcı ve konuyu görür; SMTP eksikliği zaten Program.cs'te
// ayrıca uyarı basar.
public class ConsoleEmailSender(ILogger<ConsoleEmailSender> logger, IHostEnvironment env) : IEmailSender
{
    public Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken ct = default)
    {
        if (env.IsDevelopment())
        {
            logger.LogInformation("[dev e-posta] Kime: {To} — Konu: {Subject}\n{Body}", toEmail, subject, htmlBody);
        }
        else
        {
            logger.LogWarning(
                "[e-posta GÖNDERİLMEDİ — SMTP yok] Kime: {To} — Konu: {Subject} (gövde bastırıldı: token içerir)",
                toEmail, subject);
        }
        return Task.CompletedTask;
    }
}

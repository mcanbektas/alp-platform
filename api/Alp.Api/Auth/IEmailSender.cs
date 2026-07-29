namespace Alp.Api.Auth;

public interface IEmailSender
{
    Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken ct = default);
}

// Geliştirme sırasında SMTP gerekmesin diye: e-postayı göndermek yerine
// konsola yazar. docs/uyelik-ve-rapor-plani.md §13 madde 2 — sağlayıcı
// seçilince gerçek SmtpEmailSender bunun yerini alır, arayüz değişmez.
public class ConsoleEmailSender(ILogger<ConsoleEmailSender> logger) : IEmailSender
{
    public Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken ct = default)
    {
        logger.LogInformation("[dev e-posta] Kime: {To} — Konu: {Subject}\n{Body}", toEmail, subject, htmlBody);
        return Task.CompletedTask;
    }
}

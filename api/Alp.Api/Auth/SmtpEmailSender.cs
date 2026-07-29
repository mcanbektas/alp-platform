using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Alp.Api.Auth;

/// <summary>
/// Gerçek SMTP göndericisi. `ConsoleEmailSender` ile aynı arayüzü uygular;
/// hangisinin bağlanacağı Program.cs'te `SmtpOptions.IsConfigured` ile seçilir —
/// SMTP bilgisi verilmemişse geliştirmedeki konsol davranışı aynen sürer.
/// docs/uyelik-ve-rapor-plani.md §7
/// </summary>
public class SmtpEmailSender(IOptions<SmtpOptions> options, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    private readonly SmtpOptions _opt = options.Value;

    public async Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken ct = default)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_opt.FromName, _opt.FromAddress));
        message.To.Add(MailboxAddress.Parse(toEmail));
        message.Subject = subject;
        message.Body = new BodyBuilder { HtmlBody = htmlBody }.ToMessageBody();

        var security = _opt.Security switch
        {
            SmtpSecurity.None => SecureSocketOptions.None,
            SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
            _ => SecureSocketOptions.StartTls,
        };

        using var client = new SmtpClient();
        try
        {
            await client.ConnectAsync(_opt.Host, _opt.Port, security, ct);
            if (!string.IsNullOrWhiteSpace(_opt.User))
            {
                await client.AuthenticateAsync(_opt.User, _opt.Password, ct);
            }
            await client.SendAsync(message, ct);
            await client.DisconnectAsync(true, ct);
        }
        catch (Exception ex)
        {
            // Gönderim hatası çağıranın akışını kesmez: kayıt uçları e-posta
            // gönderilemediği için 500 dönerse saldırgan, hangi adresin sistemde
            // olduğunu yanıt farkından okuyabilir (AuthEndpoints.cs'teki numaralandırma
            // savunması aynı gerekçeyle var). Hata günlüğe yazılır, akış sürer.
            logger.LogError(ex, "E-posta gönderilemedi. Kime: {To} — Konu: {Subject}", toEmail, subject);
        }
    }
}

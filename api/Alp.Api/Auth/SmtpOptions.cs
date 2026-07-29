namespace Alp.Api.Auth;

/// <summary>
/// SMTP gönderici ayarları. Değerler ortam değişkeninden gelir, depoya girmez:
/// `Smtp__Host`, `Smtp__Port`, `Smtp__User`, `Smtp__Password`, `Smtp__FromAddress`.
/// docs/uyelik-ve-rapor-plani.md §7
/// </summary>
public class SmtpOptions
{
    public const string SectionName = "Smtp";

    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;

    /// <summary>Boş bırakılabilir — kimlik doğrulaması istemeyen iç röle için.</summary>
    public string User { get; set; } = "";
    public string Password { get; set; } = "";

    public string FromAddress { get; set; } = "";
    public string FromName { get; set; } = "ALP PCB Toolkit";

    /// <summary>
    /// 587 için STARTTLS (varsayılan), 465 için baştan itibaren TLS. Kapatmak
    /// parolayı düz metin göndermek demektir; yalnızca yerel deneme için.
    /// </summary>
    public SmtpSecurity Security { get; set; } = SmtpSecurity.StartTls;

    /// <summary>Yapılandırılmamış ayar sessizce Console göndericisine düşer, hata vermez.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(FromAddress);
}

public enum SmtpSecurity
{
    /// <summary>Şifresiz — yalnızca yerel deneme rölesi (MailHog vb.).</summary>
    None,
    StartTls,
    SslOnConnect,
}

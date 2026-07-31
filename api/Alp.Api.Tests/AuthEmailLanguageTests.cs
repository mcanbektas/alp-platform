using Alp.Api.Auth;
using Alp.Data;
using Alp.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Alp.Api.Tests;

// Kimlik postalarının dili (docs/eposta-dili-karari.md).
//
// Kural: postanın dili, postayı tetikleyen isteğin `lang` alanıdır; tanınmayan
// ya da verilmeyen değer Türkçedir ve bağlantı yolu da dile göre değişir.
// İkisinden biri kaçarsa İngilizce kullanıcı ya Türkçe posta alır ya da
// bağlantısı Türkçe sayfaya düşer — ikisi de derlemeden ve öteki testlerden
// kaçar, çünkü gövde sadece bir dizedir.
public class AuthEmailLanguageTests : IDisposable
{
    private sealed class RecordingEmailSender : IEmailSender
    {
        public List<(string To, string Subject, string Body)> Sent { get; } = [];

        public Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken ct = default)
        {
            Sent.Add((toEmail, subject, htmlBody));
            return Task.CompletedTask;
        }
    }

    private readonly TestDb db = new();
    private readonly ServiceProvider provider;
    private readonly IServiceScope scope;
    private readonly RecordingEmailSender mail = new();

    // ResendConfirmationTests ile aynı kurulum: token sağlayıcıları gerçek
    // DI'dan gelir, elle kurulan bir UserManager veri koruma anahtarını veremez.
    public AuthEmailLanguageTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddScoped(_ => db.NewContext());
        services.AddIdentityCore<ApplicationUser>(opt =>
        {
            opt.SignIn.RequireConfirmedEmail = true;
            opt.User.RequireUniqueEmail = true;
        })
            .AddEntityFrameworkStores<AppDbContext>()
            .AddDefaultTokenProviders();

        provider = services.BuildServiceProvider();
        scope = provider.CreateScope();
    }

    private UserManager<ApplicationUser> Users =>
        scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

    private static IConfiguration Config => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["App:FrontendBaseUrl"] = "https://ornek.test" })
        .Build();

    public void Dispose()
    {
        scope.Dispose();
        provider.Dispose();
        db.Dispose();
    }

    [Theory]
    [InlineData("en", "/en/confirm-email", "Confirm your e-mail address")]
    [InlineData("tr", "/e-posta-dogrula", "E-posta adresini doğrula")]
    // Tanınmayan ve verilmeyen dil Türkçeye düşer; bölge eki kabul edilir.
    [InlineData("de", "/e-posta-dogrula", "E-posta adresini doğrula")]
    [InlineData(null, "/e-posta-dogrula", "E-posta adresini doğrula")]
    [InlineData("EN-US", "/en/confirm-email", "Confirm your e-mail address")]
    public async Task Dogrulama_postasi_istegin_dilinde(string? lang, string path, string subject)
    {
        var user = db.AddUser("bekleyen@test.local"); // EmailConfirmed = false

        var result = await AuthEndpoints.ResendConfirmation(
            new ResendConfirmationRequest(user.Email!, lang), Users, mail, Config);

        Assert.Equal(StatusCodes.Status200OK, ResultAssert.Status(result));
        var sent = Assert.Single(mail.Sent);
        Assert.Equal(subject, sent.Subject);
        Assert.Contains($"https://ornek.test{path}?", sent.Body);
        Assert.Contains("token=", sent.Body);
    }

    [Fact]
    public async Task Ingilizce_dogrulama_postasinda_turkce_govde_kalmaz()
    {
        var user = db.AddUser("bekleyen@test.local");

        await AuthEndpoints.ResendConfirmation(
            new ResendConfirmationRequest(user.Email!, "en"), Users, mail, Config);

        var sent = Assert.Single(mail.Sent);
        Assert.Contains("To confirm your account", sent.Body);
        Assert.DoesNotContain("Hesabını", sent.Body);
        // Türkçe yol İngilizce gövdede hiç geçmemeli: `/e-posta-dogrula`
        // bağlantısı İngilizce arayüzden gelen kullanıcıyı TR sayfaya atardı.
        Assert.DoesNotContain("/e-posta-dogrula", sent.Body);
    }

    [Theory]
    [InlineData("en", "/en/reset-password", "Password reset")]
    [InlineData("tr", "/parola-sifirla", "Parola sıfırlama")]
    [InlineData(null, "/parola-sifirla", "Parola sıfırlama")]
    public async Task Parola_sifirlama_postasi_istegin_dilinde(string? lang, string path, string subject)
    {
        var user = db.AddUser("dogrulanmis@test.local");
        user.EmailConfirmed = true;
        db.Db.SaveChanges();

        var result = await AuthEndpoints.ForgotPassword(
            new ForgotPasswordRequest(user.Email!, lang), Users, mail, Config);

        Assert.Equal(StatusCodes.Status200OK, ResultAssert.Status(result));
        var sent = Assert.Single(mail.Sent);
        Assert.Equal(subject, sent.Subject);
        Assert.Contains($"https://ornek.test{path}?", sent.Body);
        Assert.Contains("token=", sent.Body);
    }

    // Kayıt denemesi bildirimi de çevrilir (§7): yanıt yine numaralandırmaya
    // kapalı 201'dir ve YENİ hesap açılmaz.
    [Fact]
    public async Task Kayit_denemesi_bildirimi_istegin_dilinde()
    {
        var user = db.AddUser("zaten@test.local");
        user.EmailConfirmed = true;
        db.Db.SaveChanges();

        var result = await AuthEndpoints.Register(
            new RegisterRequest(user.Email!, "Baska1Parola!", "Baska Kisi", "en"),
            Users, mail, Config);

        Assert.Equal(StatusCodes.Status201Created, ResultAssert.Status(result));
        var sent = Assert.Single(mail.Sent);
        Assert.Equal("Registration attempt", sent.Subject);
        Assert.Contains("You already have an account", sent.Body);
    }
}

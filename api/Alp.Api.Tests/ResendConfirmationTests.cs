using Alp.Api.Auth;
using Alp.Data;
using Alp.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Alp.Api.Tests;

// Yeni ucun kuralı (CLAUDE.md: uç eklenirken kuralı da test edilir):
// resend-confirmation numaralandırmaya kapalıdır — hesap yok / var ama
// doğrulanmış / var ve doğrulanmamış, ÜÇÜ DE aynı 200'ü döner; posta yalnızca
// doğrulanmamış gerçek hesaba gider.
public class ResendConfirmationTests : IDisposable
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

    // UserManager token sağlayıcılarıyla birlikte gerçek DI'dan kurulur:
    // GenerateEmailConfirmationTokenAsync veri koruma anahtarı ister, elle
    // `new UserManager(...)` bunu veremezdi. Depo, TestDb'nin paylaşımlı
    // SQLite veritabanına bağlanır — üretimdeki şema kuralları geçerli kalır.
    public ResendConfirmationTests()
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

    [Fact]
    public async Task Dogrulanmamis_hesaba_posta_gider_ve_dogrulama_yolunu_tasir()
    {
        var user = db.AddUser("bekleyen@test.local"); // EmailConfirmed = false

        var result = await AuthEndpoints.ResendConfirmation(
            new ResendConfirmationRequest(user.Email!), Users, mail, Config);

        Assert.Equal(StatusCodes.Status200OK, ResultAssert.Status(result));
        var sent = Assert.Single(mail.Sent);
        Assert.Equal(user.Email, sent.To);
        Assert.Contains("/e-posta-dogrula", sent.Body);
        Assert.Contains("token=", sent.Body);
    }

    [Fact]
    public async Task Hesap_yok_ve_zaten_dogrulanmis_ayni_200_posta_yok()
    {
        var confirmed = db.AddUser("dogrulanmis@test.local");
        confirmed.EmailConfirmed = true;
        db.Db.SaveChanges();

        var unknown = await AuthEndpoints.ResendConfirmation(
            new ResendConfirmationRequest("yok@test.local"), Users, mail, Config);
        var already = await AuthEndpoints.ResendConfirmation(
            new ResendConfirmationRequest(confirmed.Email!), Users, mail, Config);

        // Yanıt şekli iki durumda birebir aynı — dışarıya tek bit sızmaz.
        Assert.Equal(StatusCodes.Status200OK, ResultAssert.Status(unknown));
        Assert.Equal(StatusCodes.Status200OK, ResultAssert.Status(already));
        Assert.Empty(mail.Sent);
    }

    [Fact]
    public async Task Bos_eposta_400()
    {
        var result = await AuthEndpoints.ResendConfirmation(
            new ResendConfirmationRequest("  "), Users, mail, Config);

        Assert.Equal(StatusCodes.Status400BadRequest, ResultAssert.Status(result));
        Assert.Empty(mail.Sent);
    }
}

using System.Text.Json;
using System.Text.RegularExpressions;
using Alp.Api.Auth;
using Alp.Api.Common;
using Alp.Data;
using Alp.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

namespace Alp.Api.Tests;

// Hesap kilidi: eşik/süre yapılandırması, kilitlenme postası, kilit açma ucu.
//
// Üç kural build'den ve öteki testlerden kaçar:
//   1. Eşik ve süre artık yapılandırmadan gelir (App:LockoutMaxAttempts /
//      App:LockoutMinutes). Yanlış okunursa hiçbir şey patlamaz — hesaplar
//      sessizce Identity varsayılanıyla (5/5) kilitlenmeye devam eder.
//   2. Kilit açma token'ı O KİLİT DÖNGÜSÜNE bağlıdır (purpose'a LockoutEnd
//      gömülür). Bağ kopar da token yalnız "kullanıcıya" bağlı kalırsa aynı
//      bağlantı bir sonraki kilidi de açar — tekrar oynatma açık kalır.
//   3. Parola sıfırlama aktif kilidi de temizler. Temizlemezse kilitlenme
//      postasındaki "parolanı sıfırla" tavsiyesi çıkmaz sokaktır: kullanıcı
//      yeni parolasıyla da giremez.
//
// Uçlar HTTP üzerinden değil, işleyicileri doğrudan çağrılarak sınanır
// (AuditLogTests ile aynı kalıp) — ama her çağrı KENDİ DI kapsamını alır:
// kullanıcı her seferinde veritabanından yeniden okunur, yani gömülü zaman
// damgasının gidiş-dönüşü de gerçekten sınanmış olur.
public class AccountLockoutTests : IDisposable
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

    private sealed class FakeEnv : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Alp.Api.Tests";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = ".";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static readonly ITokenService Tokens = new TokenService(Options.Create(new JwtOptions
    {
        Key = new string('k', 48),
        Issuer = "test",
        Audience = "test",
    }));

    private const string BaseUrl = "https://ornek.test";
    private const string Password = "Parola-12345!";
    private const string WrongPassword = "Yanlis-Parola-1!";

    // Varsayılanlardan (3 / 10) KASITLI olarak farklı: değerlerin gerçekten
    // yapılandırmadan geldiğini, koda gömülü bir sabitten değil, ancak farklı
    // bir değer görülünce anlarız.
    private const int MaxAttempts = 4;
    private const int LockMinutes = 25;

    private readonly TestDb db = new();
    private readonly ServiceProvider provider;
    private readonly RecordingEmailSender mail = new();

    private readonly IConfiguration config = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["App:FrontendBaseUrl"] = BaseUrl,
            [LockoutSettings.MaxAttemptsKey] = MaxAttempts.ToString(),
            [LockoutSettings.MinutesKey] = LockMinutes.ToString(),
        })
        .Build();

    public AccountLockoutTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddScoped(_ => db.NewContext());
        services.AddIdentityCore<ApplicationUser>(opt =>
        {
            opt.SignIn.RequireConfirmedEmail = true;
            opt.User.RequireUniqueEmail = true;
            // Program.cs'teki iki satırın BİREBİR aynısı. Program.cs bu
            // testlerden çağrılamıyor (uçlar işleyici olarak sınanıyor, host
            // ayağa kalkmıyor); okunan kuralın kendisi LockoutSettings'te
            // durduğu ve aşağıda ayrıca sınandığı için dikiş burada kalıyor.
            opt.Lockout.MaxFailedAccessAttempts = LockoutSettings.MaxAttempts(config);
            opt.Lockout.DefaultLockoutTimeSpan = LockoutSettings.Duration(config);
        })
            .AddEntityFrameworkStores<AppDbContext>()
            .AddDefaultTokenProviders();
        services.AddScoped<AuditLog>();

        provider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        provider.Dispose();
        db.Dispose();
        GC.SuppressFinalize(this);
    }

    // ---- istek kapsamları ----

    private async Task<T> InScope<T>(Func<IServiceProvider, Task<T>> body)
    {
        using var scope = provider.CreateScope();
        return await body(scope.ServiceProvider);
    }

    private Task<ApplicationUser> NewUserAsync(string email) => InScope(async sp =>
    {
        var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = email,
            Email = email,
            DisplayName = email,
            EmailConfirmed = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var result = await users.CreateAsync(user, Password);
        Assert.True(result.Succeeded, string.Join(",", result.Errors.Select(e => e.Code)));
        return user;
    });

    private Task<IResult> LoginAsync(string email, string password, string? lang = null) => InScope(sp =>
        AuthEndpoints.Login(
            new LoginRequest(email, password, false, lang),
            sp.GetRequiredService<UserManager<ApplicationUser>>(),
            Tokens,
            sp.GetRequiredService<AppDbContext>(),
            sp.GetRequiredService<AuditLog>(),
            mail,
            config,
            TestHttp.Anonymous(),
            new FakeEnv()));

    private Task<IResult> UnlockAsync(string userId, string token) => InScope(sp =>
        AuthEndpoints.Unlock(
            new UnlockRequest(userId, token),
            sp.GetRequiredService<UserManager<ApplicationUser>>(),
            sp.GetRequiredService<AuditLog>(),
            TestHttp.Anonymous()));

    private Task<IResult> ResetPasswordAsync(string email, string token, string newPassword) => InScope(sp =>
        AuthEndpoints.ResetPassword(
            new ResetPasswordRequest(email, token, newPassword),
            sp.GetRequiredService<UserManager<ApplicationUser>>(),
            sp.GetRequiredService<AppDbContext>(),
            sp.GetRequiredService<AuditLog>(),
            TestHttp.Anonymous()));

    private Task<string> ResetTokenAsync(ApplicationUser user) => InScope(async sp =>
    {
        var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
        return await users.GeneratePasswordResetTokenAsync((await users.FindByIdAsync(user.Id))!);
    });

    private Task<bool> IsLockedOutAsync(ApplicationUser user) => InScope(async sp =>
    {
        var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
        return await users.IsLockedOutAsync((await users.FindByIdAsync(user.Id))!);
    });

    private Task<DateTimeOffset?> LockoutEndAsync(ApplicationUser user) => InScope(async sp =>
    {
        var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
        return (await users.FindByIdAsync(user.Id))!.LockoutEnd;
    });

    // Eşiği aşana kadar yanlış parola dener — dönüşte hesap kilitlidir.
    private async Task LockAsync(ApplicationUser user, string? lang = null)
    {
        for (var i = 0; i < MaxAttempts; i++) await LoginAsync(user.Email!, WrongPassword, lang);
        Assert.True(await IsLockedOutAsync(user));
    }

    private static string LinkFrom(string body, string path)
    {
        var match = Regex.Match(body, $@"href=""({Regex.Escape($"{BaseUrl}{path}")}\?[^""]+)""");
        Assert.True(match.Success, $"'{path}' bağlantısı postada yok:\n{body}");
        return match.Groups[1].Value;
    }

    private static string TokenFrom(string link) =>
        Uri.UnescapeDataString(Regex.Match(link, @"[?&]token=([^&]+)").Groups[1].Value);

    private (string Unlock, string Reset) Links()
    {
        var sent = Assert.Single(mail.Sent);
        return (LinkFrom(sent.Body, "/kilit-ac"), LinkFrom(sent.Body, "/parola-sifirla"));
    }

    // ---- 1. eşik ve süre yapılandırmadan okunur ----

    [Fact]
    public void yapilandirma_yoksa_varsayilan_3_deneme_10_dakika()
    {
        var empty = new ConfigurationBuilder().Build();

        Assert.Equal(3, LockoutSettings.MaxAttempts(empty));
        Assert.Equal(TimeSpan.FromMinutes(10), LockoutSettings.Duration(empty));
    }

    [Theory]
    // Ayrıştırılamayan, sıfır ve eksi değer sessizce varsayılana düşer:
    // yanlış yazılmış tek bir env satırı yüzünden "her deneme kilitler" ya da
    // "hiç kilitlenmez" davranışı doğmamalı.
    [InlineData("abc", null, 3, 10)]
    [InlineData("0", "0", 3, 10)]
    [InlineData("-1", "-5", 3, 10)]
    [InlineData("", "", 3, 10)]
    [InlineData("7", "45", 7, 45)]
    public void esik_ve_sure_yapilandirmadan_okunur(string? attempts, string? minutes, int expectedAttempts, int expectedMinutes)
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [LockoutSettings.MaxAttemptsKey] = attempts,
                [LockoutSettings.MinutesKey] = minutes,
            })
            .Build();

        Assert.Equal(expectedAttempts, LockoutSettings.MaxAttempts(cfg));
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), LockoutSettings.Duration(cfg));
    }

    // Yapılandırılan değer gerçekten UYGULANIYOR mu: eşiğin bir altında kilit
    // YOK, eşikte kilit VAR ve süresi yapılandırılan süre kadar.
    [Fact]
    public async Task kilit_yapilandirilan_esikte_ve_surede_olusur()
    {
        var user = await NewUserAsync("esik@ornek.test");

        for (var i = 0; i < MaxAttempts - 1; i++) await LoginAsync(user.Email!, WrongPassword);
        Assert.False(await IsLockedOutAsync(user));
        Assert.Empty(mail.Sent);

        await LoginAsync(user.Email!, WrongPassword);
        Assert.True(await IsLockedOutAsync(user));

        var remaining = (await LockoutEndAsync(user))!.Value - DateTimeOffset.UtcNow;
        Assert.InRange(remaining.TotalMinutes, LockMinutes - 1, LockMinutes);
    }

    // ---- 2. kilitlenme postası ----

    [Theory]
    [InlineData(null, "/kilit-ac", "/parola-sifirla", "Hesabın kilitlendi")]
    [InlineData("tr", "/kilit-ac", "/parola-sifirla", "Hesabın kilitlendi")]
    [InlineData("en", "/en/unlock-account", "/en/reset-password", "Your account has been locked")]
    [InlineData("EN-US", "/en/unlock-account", "/en/reset-password", "Your account has been locked")]
    public async Task kilitlenme_postasi_iki_baglantiyi_istegin_dilinde_tasir(
        string? lang, string unlockPath, string resetPath, string subject)
    {
        var user = await NewUserAsync("postali@ornek.test");

        await LockAsync(user, lang);

        var sent = Assert.Single(mail.Sent);
        Assert.Equal(user.Email, sent.To);
        Assert.Equal(subject, sent.Subject);
        // Eşik ve süre postada YAZILI: kullanıcı beklemesi gereken süreyi
        // görmeden "bir şey yapmama" seçeneğini tartamaz.
        Assert.Contains(MaxAttempts.ToString(), sent.Body);
        Assert.Contains(LockMinutes.ToString(), sent.Body);

        var unlockLink = LinkFrom(sent.Body, unlockPath);
        Assert.Contains($"userId={Uri.EscapeDataString(user.Id)}", unlockLink);
        Assert.Contains("&token=", unlockLink);

        var resetLink = LinkFrom(sent.Body, resetPath);
        Assert.Contains($"email={Uri.EscapeDataString(user.Email!)}", resetLink);
        Assert.Contains("&token=", resetLink);
    }

    // İngilizce postada Türkçe yol kalmamalı — kullanıcıyı TR sayfaya atardı
    // (AuthEmailLanguageTests'teki kuralın kilitlenme postasındaki karşılığı).
    [Fact]
    public async Task ingilizce_kilitlenme_postasinda_turkce_yol_kalmaz()
    {
        var user = await NewUserAsync("ingilizce@ornek.test");

        await LockAsync(user, "en");

        var sent = Assert.Single(mail.Sent);
        Assert.DoesNotContain("/kilit-ac", sent.Body);
        Assert.DoesNotContain("/parola-sifirla", sent.Body);
    }

    // Kilitliyken gelen sonraki denemeler erken dönüşe takılır — posta da
    // audit gibi yalnız eşik GEÇİŞİNDE bir kez gider, her denemede değil.
    [Fact]
    public async Task kilitliyken_sonraki_denemeler_ikinci_posta_gondermez()
    {
        var user = await NewUserAsync("tek-posta@ornek.test");

        await LockAsync(user);
        await LoginAsync(user.Email!, WrongPassword);
        await LoginAsync(user.Email!, Password);

        Assert.Single(mail.Sent);
    }

    // ---- 3. kilit açma ucu ----

    [Fact]
    public async Task gecerli_token_kilidi_acar_ve_auth_lockout_cleared_izini_yazar()
    {
        var user = await NewUserAsync("acilacak@ornek.test");
        await LockAsync(user);
        var lockoutEnd = await LockoutEndAsync(user);

        var result = await UnlockAsync(user.Id, TokenFrom(Links().Unlock));

        Assert.Equal(StatusCodes.Status204NoContent, ResultAssert.Status(result));
        Assert.False(await IsLockedOutAsync(user));
        Assert.Null(await LockoutEndAsync(user));

        using var after = db.NewContext();
        var row = Assert.Single(after.AuditEvents.Where(a => a.Event == AuditEventCodes.AuthLockoutCleared));
        Assert.Null(row.ActorUserId);
        Assert.Equal(user.Id, row.TargetUserId);

        using var detail = JsonDocument.Parse(row.DetailJson!);
        Assert.Equal(AuthEndpoints.UnlockViaLink, detail.RootElement.GetProperty("via").GetString());
        Assert.Equal(
            lockoutEnd!.Value.ToUnixTimeMilliseconds(),
            detail.RootElement.GetProperty("previousLockoutEnd").GetDateTimeOffset().ToUnixTimeMilliseconds());
    }

    // Kilit açıldıktan sonra kullanıcı ESKİ parolasıyla girebilmeli — bu ucun
    // varlık sebebi bu (parola sıfırlamadan farkı).
    [Fact]
    public async Task kilit_acildiktan_sonra_eski_parolayla_giris_calisir()
    {
        var user = await NewUserAsync("eski-parola@ornek.test");
        await LockAsync(user);

        await UnlockAsync(user.Id, TokenFrom(Links().Unlock));

        Assert.Equal(StatusCodes.Status200OK, ResultAssert.Status(await LoginAsync(user.Email!, Password)));
    }

    // TEKRAR OYNATMA (replay) — bu testin düşmesi, postalanmış tek bir
    // bağlantının sonraki bütün kilitleri de açabilmesi demektir.
    [Fact]
    public async Task yeniden_kilitlenince_eski_token_reddedilir()
    {
        var user = await NewUserAsync("tekrar-kilit@ornek.test");
        await LockAsync(user);
        var oldToken = TokenFrom(Links().Unlock);

        // Kilidi aç, sonra hesabı YENİDEN kilitle: LockoutEnd yeni bir değer
        // alır ve eski token'ın purpose'una gömülü damga artık tutmaz.
        Assert.Equal(StatusCodes.Status204NoContent, ResultAssert.Status(await UnlockAsync(user.Id, oldToken)));
        mail.Sent.Clear();
        await LockAsync(user);

        var result = await UnlockAsync(user.Id, oldToken);

        Assert.Equal(StatusCodes.Status400BadRequest, ResultAssert.Status(result));
        Assert.Equal("INVALID_TOKEN", ResultAssert.Value<ApiError>(result).Error);
        Assert.True(await IsLockedOutAsync(user));
    }

    // Aynı token'ı ikinci kez kullanmak da geçersiz: başarılı açmadan sonra
    // LockoutEnd null olur ve purpose bir daha eşleşmez.
    [Fact]
    public async Task ayni_token_ikinci_kez_calismaz()
    {
        var user = await NewUserAsync("iki-kez@ornek.test");
        await LockAsync(user);
        var token = TokenFrom(Links().Unlock);

        await UnlockAsync(user.Id, token);
        var second = await UnlockAsync(user.Id, token);

        Assert.Equal(StatusCodes.Status400BadRequest, ResultAssert.Status(second));
    }

    // Başka bir hesabın token'ı bu hesabı açmaz.
    [Fact]
    public async Task baska_hesabin_tokeni_reddedilir()
    {
        var victim = await NewUserAsync("kurban@ornek.test");
        var other = await NewUserAsync("baskasi@ornek.test");
        await LockAsync(other);
        var otherToken = TokenFrom(Links().Unlock);
        mail.Sent.Clear();
        await LockAsync(victim);

        var result = await UnlockAsync(victim.Id, otherToken);

        Assert.Equal(StatusCodes.Status400BadRequest, ResultAssert.Status(result));
        Assert.True(await IsLockedOutAsync(victim));
    }

    // Hatalı istekler TEK bir kod döner: "hesap yok" ile "token geçersiz"
    // ayrımı sızarsa bu uç oturumsuz bir numaralandırma kahinine döner.
    [Fact]
    public async Task bozuk_istekler_ayni_generik_hatayi_verir()
    {
        var user = await NewUserAsync("bozuk@ornek.test");
        await LockAsync(user);
        var good = TokenFrom(Links().Unlock);

        var cases = new[]
        {
            await UnlockAsync(user.Id, "cop-token"),
            await UnlockAsync(user.Id, ""),
            await UnlockAsync("", good),
            await UnlockAsync(Guid.NewGuid().ToString(), good),
        };

        foreach (var result in cases)
        {
            Assert.Equal(StatusCodes.Status400BadRequest, ResultAssert.Status(result));
            Assert.Equal("INVALID_TOKEN", ResultAssert.Value<ApiError>(result).Error);
        }

        Assert.True(await IsLockedOutAsync(user));
        using var after = db.NewContext();
        Assert.Empty(after.AuditEvents.Where(a => a.Event == AuditEventCodes.AuthLockoutCleared));
    }

    // Kilitli DEĞİLKEN gelen istek de reddedilir: purpose "none"a düşer ve
    // kilitliyken üretilmiş hiçbir token onunla eşleşmez.
    [Fact]
    public async Task kilitli_degilken_eski_token_reddedilir()
    {
        var user = await NewUserAsync("kilitsiz@ornek.test");
        await LockAsync(user);
        var token = TokenFrom(Links().Unlock);

        // Kilidi bu uçtan DEĞİL, doğrudan temizle (yönetici paneli / doğal
        // süre dolumu gibi) — token yine de ölmeli.
        await InScope(async sp =>
        {
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            return await users.SetLockoutEndDateAsync((await users.FindByIdAsync(user.Id))!, null);
        });

        Assert.Equal(StatusCodes.Status400BadRequest, ResultAssert.Status(await UnlockAsync(user.Id, token)));
    }

    // ---- 4. parola sıfırlama kilidi de temizler ----

    [Fact]
    public async Task parola_sifirlama_aktif_kilidi_temizler_ve_ayri_iz_yazar()
    {
        var user = await NewUserAsync("sifirlayip-acacak@ornek.test");
        await LockAsync(user);
        var lockoutEnd = await LockoutEndAsync(user);
        var token = TokenFrom(Links().Reset);

        var result = await ResetPasswordAsync(user.Email!, token, "Yepyeni-Parola-9!");

        Assert.Equal(StatusCodes.Status200OK, ResultAssert.Status(result));
        Assert.False(await IsLockedOutAsync(user));
        Assert.Equal(StatusCodes.Status200OK, ResultAssert.Status(await LoginAsync(user.Email!, "Yepyeni-Parola-9!")));

        using var after = db.NewContext();
        Assert.Single(after.AuditEvents.Where(a => a.Event == AuditEventCodes.AuthPasswordReset));
        var row = Assert.Single(after.AuditEvents.Where(a => a.Event == AuditEventCodes.AuthLockoutCleared));
        Assert.Null(row.ActorUserId);
        Assert.Equal(user.Id, row.TargetUserId);

        using var detail = JsonDocument.Parse(row.DetailJson!);
        Assert.Equal(AuthEndpoints.UnlockViaPasswordReset, detail.RootElement.GetProperty("via").GetString());
        Assert.Equal(
            lockoutEnd!.Value.ToUnixTimeMilliseconds(),
            detail.RootElement.GetProperty("previousLockoutEnd").GetDateTimeOffset().ToUnixTimeMilliseconds());
    }

    // Kilitli DEĞİLKEN sıfırlama iz bırakmaz — hiçbir şey değişmedi, gürültü
    // olmasın ("değer değişmediyse iz yazma" kuralı).
    [Fact]
    public async Task kilitsiz_sifirlama_kilit_izi_birakmaz()
    {
        var user = await NewUserAsync("kilitsiz-sifirlama@ornek.test");
        var token = await ResetTokenAsync(user);

        var result = await ResetPasswordAsync(user.Email!, token, "Yepyeni-Parola-9!");

        Assert.Equal(StatusCodes.Status200OK, ResultAssert.Status(result));

        using var after = db.NewContext();
        Assert.Single(after.AuditEvents.Where(a => a.Event == AuditEventCodes.AuthPasswordReset));
        Assert.Empty(after.AuditEvents.Where(a => a.Event == AuditEventCodes.AuthLockoutCleared));
    }
}

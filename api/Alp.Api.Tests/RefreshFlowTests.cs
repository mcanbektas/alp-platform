using Alp.Api.Auth;
using Alp.Api.Common;
using Alp.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

namespace Alp.Api.Tests;

// Yenileme token'ı durum makinesi: rotasyon, tekrar-kullanım penceresi ve
// zincir iptali. Deponun en yüksek sonuçlu mantığıydı ve kapsam dışıydı —
// bir gerileme, oturum çalınmasının fark edilmemesi ya da meşru kullanıcının
// her sekmede oturumdan atılması demek (ikisi de daha önce gerçekten yaşandı,
// bkz. AuthEndpoints içindeki gerekçeler).
public class RefreshFlowTests
{
    // Handler'ın SetRefreshCookie'si env.IsDevelopment()'a yalnızca Secure
    // bayrağı için bakar — testte hangisi olduğu önemsiz, sabit bir ortam adı
    // yeter.
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
        // Gerçek imzalama yapılır (CreateAccessToken) — anahtar 32 bayttan uzun olmalı.
        Key = new string('k', 48),
        Issuer = "test",
        Audience = "test",
    }));

    private static HttpContext WithCookie(string rawToken)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Cookie = $"alp_rt={rawToken}";
        return ctx;
    }

    // Kayıtlı bir oturum kurar: kullanıcı + aktif yenileme token'ı. Ham değeri
    // döndürür — handler çerezden ham değeri okuyup hash'ler.
    private static (ApplicationUser User, string Raw, RefreshToken Row) Seed(
        TestDb db, bool persistent = false)
    {
        var user = db.AddUser($"u-{Guid.NewGuid():N}@test.local");
        var raw = Tokens.CreateRefreshToken();
        var row = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = Tokens.Hash(raw),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            CreatedAt = DateTimeOffset.UtcNow,
            Persistent = persistent,
        };
        db.Db.RefreshTokens.Add(row);
        db.Db.SaveChanges();
        return (user, raw, row);
    }

    [Fact]
    public async Task Gecerli_token_dondurulur_eskisi_iptal_yenisi_eklenir()
    {
        using var db = new TestDb();
        var (user, raw, row) = Seed(db);

        var result = await AuthEndpoints.Refresh(Tokens, db.NewContext(), WithCookie(raw), new FakeEnv());

        Assert.Equal(StatusCodes.Status200OK, ResultAssert.Status(result));
        var body = ResultAssert.Value<LoginResponse>(result);
        Assert.False(string.IsNullOrEmpty(body.AccessToken));

        var fresh = db.NewContext();
        var old = fresh.RefreshTokens.Single(t => t.Id == row.Id);
        Assert.NotNull(old.RevokedAt);
        Assert.NotNull(old.ReplacedByHash);

        var replacement = fresh.RefreshTokens.Single(t => t.TokenHash == old.ReplacedByHash);
        Assert.Null(replacement.RevokedAt);
        Assert.Equal(user.Id, replacement.UserId);
    }

    [Fact]
    public async Task Persistent_secimi_dondurulen_tokena_tasinir()
    {
        using var db = new TestDb();
        var (_, raw, row) = Seed(db, persistent: true);

        await AuthEndpoints.Refresh(Tokens, db.NewContext(), WithCookie(raw), new FakeEnv());

        var fresh = db.NewContext();
        var old = fresh.RefreshTokens.Single(t => t.Id == row.Id);
        var replacement = fresh.RefreshTokens.Single(t => t.TokenHash == old.ReplacedByHash);
        Assert.True(replacement.Persistent);
    }

    [Fact]
    public async Task Pencere_icinde_tekrar_sunum_401_ama_zincir_iptal_edilmez()
    {
        using var db = new TestDb();
        var (_, raw, row) = Seed(db);

        // İlk yenileme döndürür; aynı ham token hemen (pencere içinde) tekrar
        // sunulur — sekme yarışı senaryosu.
        await AuthEndpoints.Refresh(Tokens, db.NewContext(), WithCookie(raw), new FakeEnv());
        var replay = await AuthEndpoints.Refresh(Tokens, db.NewContext(), WithCookie(raw), new FakeEnv());

        Assert.Equal(StatusCodes.Status401Unauthorized, ResultAssert.Status(replay));
        Assert.Equal("INVALID_REFRESH_TOKEN", ResultAssert.Value<ApiError>(replay).Error);

        // Kazananın yepyeni token'ı YAŞAMAYA DEVAM ETMELİ — pencere tam bunun
        // için var: meşru sekme yarışı oturumu öldürmemeli.
        var fresh = db.NewContext();
        var old = fresh.RefreshTokens.Single(t => t.Id == row.Id);
        var replacement = fresh.RefreshTokens.Single(t => t.TokenHash == old.ReplacedByHash);
        Assert.Null(replacement.RevokedAt);
    }

    [Fact]
    public async Task Pencere_disinda_tekrar_sunum_zinciri_iptal_eder()
    {
        using var db = new TestDb();
        var (_, raw, row) = Seed(db);

        await AuthEndpoints.Refresh(Tokens, db.NewContext(), WithCookie(raw), new FakeEnv());

        // Döndürme anını pencerenin (30 sn) dışına itiyoruz — artık bu tekrar
        // sunum sekme yarışı değil, çalıntı token oynatması sayılır.
        var setup = db.NewContext();
        var oldRow = setup.RefreshTokens.Single(t => t.Id == row.Id);
        oldRow.RevokedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        setup.SaveChanges();

        var replay = await AuthEndpoints.Refresh(Tokens, db.NewContext(), WithCookie(raw), new FakeEnv());

        Assert.Equal(StatusCodes.Status401Unauthorized, ResultAssert.Status(replay));

        // Hırsızın elindeki zincirin ucundaki AKTİF halka da iptal edilmiş
        // olmalı — saldırgan döndürülmüş token üzerinden oturuma tutunamaz.
        var fresh = db.NewContext();
        var old = fresh.RefreshTokens.Single(t => t.Id == row.Id);
        var replacement = fresh.RefreshTokens.Single(t => t.TokenHash == old.ReplacedByHash);
        Assert.NotNull(replacement.RevokedAt);
    }

    [Fact]
    public async Task Suresi_dolmus_token_401()
    {
        using var db = new TestDb();
        var (_, raw, row) = Seed(db);

        var setup = db.NewContext();
        var expired = setup.RefreshTokens.Single(t => t.Id == row.Id);
        expired.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        setup.SaveChanges();

        var result = await AuthEndpoints.Refresh(Tokens, db.NewContext(), WithCookie(raw), new FakeEnv());

        Assert.Equal(StatusCodes.Status401Unauthorized, ResultAssert.Status(result));
        // Süresi dolan token döndürülMEZ — yeni satır eklenmemiş olmalı.
        Assert.Equal(1, db.NewContext().RefreshTokens.Count());
    }

    [Fact]
    public async Task Cerez_yok_veya_taninmayan_token_401()
    {
        using var db = new TestDb();
        Seed(db);

        var noCookie = await AuthEndpoints.Refresh(Tokens, db.NewContext(), new DefaultHttpContext(), new FakeEnv());
        Assert.Equal(StatusCodes.Status401Unauthorized, ResultAssert.Status(noCookie));

        var garbage = await AuthEndpoints.Refresh(
            Tokens, db.NewContext(), WithCookie("hic-var-olmamis-token"), new FakeEnv());
        Assert.Equal(StatusCodes.Status401Unauthorized, ResultAssert.Status(garbage));
    }
}

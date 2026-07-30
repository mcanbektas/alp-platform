using Alp.Api.Common;
using Alp.Api.Http;
using Alp.Data;
using Alp.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Alp.Api.Auth;

public static class AuthEndpoints
{
    private const string RefreshCookieName = "alp_rt";

    // Döndürülmüş bir yenileme token'ının tekrar sunulması bu süre içinde
    // YARIŞ sayılır, hırsızlık değil (bkz. Refresh). Sekme yarışı saniyeler
    // içinde olur; çalıntı bir token'ın tekrar oynatılması genelde çok
    // sonra gelir. Pencere darsa meşru yarış hâlâ oturumdan atar, genişse
    // çalıntı token daha uzun süre kullanılabilir.
    private static readonly TimeSpan RotationGrace = TimeSpan.FromSeconds(30);
    private const string RefreshCookiePath = "/api/auth";

    // Küçük JSON gövdeli uçlar (e-posta/parola) için üst sınır — rate sınırı
    // aşılmadan önce bile tek bir istekle bellek baskısı oluşturulamasın.
    private const long AuthBodyLimitBytes = 16 * 1024;

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth");

        group.MapPost("/register", Register).RequireRateLimiting("auth").LimitBodySize(AuthBodyLimitBytes);
        group.MapPost("/login", Login).RequireRateLimiting("auth").LimitBodySize(AuthBodyLimitBytes);
        group.MapPost("/refresh", Refresh).RequireRateLimiting("refresh");
        group.MapPost("/logout", Logout).RequireRateLimiting("logout");
        group.MapPost("/forgot-password", ForgotPassword).RequireRateLimiting("auth").LimitBodySize(AuthBodyLimitBytes);
        group.MapPost("/reset-password", ResetPassword).RequireRateLimiting("auth").LimitBodySize(AuthBodyLimitBytes);
        group.MapGet("/confirm-email", ConfirmEmail).RequireRateLimiting("auth");

        // Profil uçları oturum grubunun dışında, kendi kökünde: `/api/auth/*`
        // oturum kurma/kapatma yüzeyidir, bunlar ise oturum açmış kullanıcının
        // kendi kaydını okuyup düzenlediği yüzey.
        var me = app.MapGroup("/api/me").RequireAuthorization();

        me.MapGet("/", Me);
        me.MapPatch("/", UpdateMe).RequireRateLimiting("writes").LimitBodySize(AuthBodyLimitBytes);
        // Parola değiştirme profil güncellemesiyle aynı yüzeyde ama kendi
        // sınırında: mevcut parola üzerinde deneme yapılmasına açık olduğu için
        // "writes" yetmez, "auth" ise IP başına ve login'le ortak olduğundan
        // meşru kullanıcıyı kilitlerdi (gerekçe: Program.cs, "password").
        me.MapPost("/password", ChangePassword).RequireRateLimiting("password").LimitBodySize(AuthBodyLimitBytes);
    }

    // appsettings.json anahtarı boş dize olarak commit edilir (gizli değer
    // yok), `??` yalnızca null'da devreye girer — boş dize de "ayarlanmamış"
    // sayılmalı. Program.cs'teki CORS kararıyla aynı gerekçe.
    private static string FrontendBaseUrl(IConfiguration config)
    {
        var url = config["App:FrontendBaseUrl"];
        return string.IsNullOrWhiteSpace(url) ? "http://localhost:3000" : url;
    }

    private static async Task<IResult> Register(
        RegisterRequest req,
        UserManager<ApplicationUser> userManager,
        IEmailSender emailSender,
        IConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password)
            || string.IsNullOrWhiteSpace(req.DisplayName))
        {
            return Results.BadRequest(new ApiError("MISSING_FIELDS"));
        }

        var user = new ApplicationUser
        {
            UserName = req.Email,
            Email = req.Email,
            DisplayName = req.DisplayName.Trim(),
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var result = await userManager.CreateAsync(user, req.Password);
        if (!result.Succeeded)
        {
            var codes = result.Errors.Select(e => e.Code).ToArray();

            // E-posta zaten kayıtlıysa YENİ hesap açılmaz, ama yanıt normal
            // kayıt başarısıyla birebir aynıdır — numaralandırmaya kapalı.
            // Gerçek sahibi bilgilendirilir; bu, "biri e-postanla kayıt
            // olmaya çalıştı" bildirimidir, saldırgana hiçbir şey söylemez.
            if (codes.Contains("DuplicateUserName") || codes.Contains("DuplicateEmail"))
            {
                var existing = await userManager.FindByEmailAsync(req.Email);
                if (existing is not null)
                {
                    await emailSender.SendAsync(existing.Email!, "Kayıt denemesi",
                        "Bu e-posta adresiyle ALP PCB Toolkit üzerinde yeni bir hesap açılmaya "
                        + "çalışıldı. Zaten bir hesabın var — bu sen değilsen görmezden gelebilirsin. "
                        + "Parolanı unuttuysan parola sıfırlama sayfasını kullan.");
                }
                return Results.Created();
            }

            return Results.BadRequest(new ApiError("IDENTITY_ERROR", new { codes }));
        }

        var token = await userManager.GenerateEmailConfirmationTokenAsync(user);
        var link = $"{FrontendBaseUrl(config)}/e-posta-dogrula"
            + $"?userId={Uri.EscapeDataString(user.Id)}&token={Uri.EscapeDataString(token)}";
        await emailSender.SendAsync(user.Email!, "E-posta adresini doğrula",
            $"Hesabını doğrulamak için: <a href=\"{link}\">{link}</a>");

        return Results.Created();
    }

    // Numaralandırmaya kapalı giriş: hesap yok / parola yanlış / e-posta
    // doğrulanmamış — parola doğru bilinene kadar HEPSİ aynı 401 yanıtını
    // verir. Kilit ve doğrulama durumu yalnızca parola ispatlandıktan SONRA
    // açığa çıkar, çünkü o noktada saldırgan zaten hesabı "kazanmış" demektir.
    // SignInManager.CheckPasswordSignInAsync bunu güvenli yapmaz: iç PreSignInCheck
    // adımı e-posta doğrulamasını parolayı hiç kontrol etmeden reddediyor — bu
    // yüzden burada UserManager ile elle, sıralı bir akış kuruluyor.
    private static async Task<IResult> Login(
        LoginRequest req,
        UserManager<ApplicationUser> userManager,
        ITokenService tokenService,
        AppDbContext db,
        HttpContext http,
        IWebHostEnvironment env)
    {
        var user = await userManager.FindByEmailAsync(req.Email);
        if (user is null)
        {
            // Zamanlama farkını daraltmak için sahte bir özet hesabı yine de
            // yapılır — "hesap yok" ile "parola yanlış" birbirine yaklaşır.
            userManager.PasswordHasher.HashPassword(new ApplicationUser(), req.Password);
            return Results.Json(new ApiError("INVALID_CREDENTIALS"), statusCode: StatusCodes.Status401Unauthorized);
        }

        var passwordOk = await userManager.CheckPasswordAsync(user, req.Password);
        if (!passwordOk)
        {
            if (userManager.SupportsUserLockout) await userManager.AccessFailedAsync(user);
            return Results.Json(new ApiError("INVALID_CREDENTIALS"), statusCode: StatusCodes.Status401Unauthorized);
        }

        // Buradan sonrası yalnızca doğru parolayı bilen taraf görür.
        if (await userManager.IsLockedOutAsync(user))
        {
            return Results.Json(new ApiError("LOCKED_OUT"), statusCode: StatusCodes.Status423Locked);
        }
        if (userManager.SupportsUserLockout) await userManager.ResetAccessFailedCountAsync(user);

        if (userManager.Options.SignIn.RequireConfirmedEmail && !await userManager.IsEmailConfirmedAsync(user))
        {
            return Results.Json(new ApiError("EMAIL_NOT_CONFIRMED"), statusCode: StatusCodes.Status403Forbidden);
        }

        return await IssueSession(user, tokenService, db, http, env, req.RememberMe);
    }

    private static async Task<IResult> Refresh(
        ITokenService tokenService,
        AppDbContext db,
        HttpContext http,
        IWebHostEnvironment env)
    {
        if (!http.Request.Cookies.TryGetValue(RefreshCookieName, out var raw) || string.IsNullOrEmpty(raw))
        {
            return Results.Json(new ApiError("NO_REFRESH_TOKEN"), statusCode: StatusCodes.Status401Unauthorized);
        }

        var hash = tokenService.Hash(raw);
        var existing = await db.RefreshTokens.Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash);
        var now = DateTimeOffset.UtcNow;

        if (existing is null || existing.User is null)
        {
            ClearRefreshCookie(http);
            return Results.Json(new ApiError("INVALID_REFRESH_TOKEN"), statusCode: StatusCodes.Status401Unauthorized);
        }

        if (existing.RevokedAt is not null)
        {
            // Zaten döndürülmüş bir token tekrar sunuldu. Bu HER ZAMAN
            // hırsızlık değildir: iki sekme aynı anda açıldığında ikisi de
            // aynı çerezle yenilemeye gelir ve biri kaçınılmaz olarak
            // döndürülmüş token'ı sunar.
            //
            // Ölçüldü: ayrım yapılmadığında iki eşzamanlı yenileme meşru
            // kullanıcıyı HER SEKMEDE oturumdan atıyordu — kaybeden istek
            // zinciri iptal ediyor, kazananın az önce aldığı yepyeni token da
            // ölüyordu.
            //
            // Ayrım zamanla yapılır: döndürmenin hemen ardından gelen tekrar
            // yarıştır, günler sonra gelen tekrar çalıntıdır. Pencere dar
            // tutulur — çalınan bir token'ın kullanılabildiği süre bu kadardır.
            if (now - existing.RevokedAt.Value > RotationGrace)
            {
                await RevokeDescendantChain(db, existing, now);
                ClearRefreshCookie(http);
            }
            // Pencere içindeyse ÇEREZ SİLİNMEZ. Çerez sekmeler arasında
            // ortaktır; silmek, işini doğru yapan öteki sekmenin oturumunu da
            // götürürdü. Bu istek başarısız olur, istemci yeniden dener.
            return Results.Json(new ApiError("INVALID_REFRESH_TOKEN"), statusCode: StatusCodes.Status401Unauthorized);
        }

        if (existing.ExpiresAt <= now)
        {
            ClearRefreshCookie(http);
            return Results.Json(new ApiError("INVALID_REFRESH_TOKEN"), statusCode: StatusCodes.Status401Unauthorized);
        }

        var newRaw = tokenService.CreateRefreshToken();
        var newHash = tokenService.Hash(newRaw);

        // Atomik döndürme: yalnızca hâlâ RevokedAt IS NULL ise işaretlenir.
        // İki eşzamanlı istek aynı token'ı sunarsa yalnızca biri bu UPDATE'i
        // "kazanır" (TOCTOU yarışı burada kapanır) — kaybeden 0 satır etkiler
        // ve reuse dalına düşer.
        var rotated = await db.RefreshTokens
            .Where(t => t.Id == existing.Id && t.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(t => t.RevokedAt, now)
                .SetProperty(t => t.ReplacedByHash, newHash));

        if (rotated == 0)
        {
            // Yarışı kaybeden taraf — aynı token'ı gerçekten aynı anda sunan
            // başka bir istek onu bizden önce döndürdü (tarayıcı yeniden
            // denemesi, çift efekt tetiklemesi gibi çoğunlukla zararsız bir
            // durum). Kazananın zincirini burada iptal ETMİYORUZ: bunu
            // yapmak sıradan bir çakışmayı "hırsızlık" gibi ele alıp meşru
            // kullanıcıyı da oturumdan atardı. Gerçek çalıntı-tekrar senaryosu
            // zaten yukarıdaki `existing.RevokedAt is not null` dalında
            // yakalanıyor — orada token AYRI, SONRAKİ bir istekte sunuluyor.
            //
            // Çerez de SİLİNMEZ: yarışı kazanan istek geçerli bir token yazdı,
            // çerez sekmeler arasında ortak. Buradan silmek kazananın oturumunu
            // da götürürdü — asıl arıza buydu.
            return Results.Json(new ApiError("INVALID_REFRESH_TOKEN"), statusCode: StatusCodes.Status401Unauthorized);
        }

        db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = existing.UserId,
            TokenHash = newHash,
            ExpiresAt = now.Add(tokenService.RefreshTokenLifetime),
            CreatedByIp = http.Connection.RemoteIpAddress?.ToString(),
            CreatedAt = now,
            // Döndürülen token ilk girişteki seçimi taşır — taşınmazsa oturum
            // çerezi ilk yenilemede sessizce kalıcıya döner.
            Persistent = existing.Persistent,
        });
        await db.SaveChangesAsync();

        var access = tokenService.CreateAccessToken(existing.User);
        SetRefreshCookie(http, newRaw, now.Add(tokenService.RefreshTokenLifetime), env, existing.Persistent);
        return Results.Ok(new LoginResponse(access.Value, access.ExpiresAt));
    }

    // Döndürme zincirinde ileri yürüyüp hâlâ aktif olan halkayı iptal eder.
    // `guard`, bozuk/döngüsel veriye karşı sonlu adım sayısı garantisidir.
    private static async Task RevokeDescendantChain(AppDbContext db, RefreshToken start, DateTimeOffset now)
    {
        var cursor = start;
        var guard = 0;
        while (cursor.ReplacedByHash is not null && guard++ < 50)
        {
            var next = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == cursor.ReplacedByHash);
            if (next is null) break;
            if (next.RevokedAt is null)
            {
                next.RevokedAt = now;
                await db.SaveChangesAsync();
            }
            cursor = next;
        }
    }

    private static async Task<IResult> Logout(ITokenService tokenService, AppDbContext db, HttpContext http)
    {
        if (http.Request.Cookies.TryGetValue(RefreshCookieName, out var raw) && !string.IsNullOrEmpty(raw))
        {
            var hash = tokenService.Hash(raw);
            var existing = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash);
            if (existing is not null && existing.RevokedAt is null)
            {
                existing.RevokedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync();
            }
        }

        ClearRefreshCookie(http);
        return Results.NoContent();
    }

    private static async Task<IResult> ForgotPassword(
        ForgotPasswordRequest req,
        UserManager<ApplicationUser> userManager,
        IEmailSender emailSender,
        IConfiguration config)
    {
        // Hesap var mı yok mu her zaman aynı yanıt döner — numaralandırma
        // saldırısına bilgi sızdırmaz.
        var user = await userManager.FindByEmailAsync(req.Email);
        if (user is not null && await userManager.IsEmailConfirmedAsync(user))
        {
            var token = await userManager.GeneratePasswordResetTokenAsync(user);
            var link = $"{FrontendBaseUrl(config)}/parola-sifirla"
                + $"?email={Uri.EscapeDataString(req.Email)}&token={Uri.EscapeDataString(token)}";
            await emailSender.SendAsync(req.Email, "Parola sıfırlama",
                $"Parolanı sıfırlamak için: <a href=\"{link}\">{link}</a>");
        }

        return Results.Ok();
    }

    private static async Task<IResult> ResetPassword(
        ResetPasswordRequest req,
        UserManager<ApplicationUser> userManager,
        AppDbContext db)
    {
        var user = await userManager.FindByEmailAsync(req.Email);
        if (user is null)
        {
            // Hesap yokken de token doğrulama hatasıyla AYNI şekle dönülür —
            // "hesap yok" ile "token geçersiz" ayrımı dışarı sızmaz.
            return Results.BadRequest(new ApiError("IDENTITY_ERROR", new { codes = new[] { "InvalidToken" } }));
        }

        var result = await userManager.ResetPasswordAsync(user, req.Token, req.NewPassword);
        if (!result.Succeeded)
        {
            return Results.BadRequest(new ApiError("IDENTITY_ERROR", new { codes = result.Errors.Select(e => e.Code) }));
        }

        // Parola sıfırlanınca çalınmış olabilecek her yenileme token'ı da
        // geçersiz kılınır — aksi hâlde saldırgan sıfırlamadan SONRA da
        // oturumu döndürmeye devam edebilirdi.
        var now = DateTimeOffset.UtcNow;
        await db.RefreshTokens
            .Where(t => t.UserId == user.Id && t.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(t => t.RevokedAt, now));

        return Results.Ok();
    }

    private static async Task<IResult> ConfirmEmail(
        [FromQuery] string userId,
        [FromQuery] string token,
        UserManager<ApplicationUser> userManager)
    {
        var user = await userManager.FindByIdAsync(userId);
        if (user is null)
        {
            return Results.BadRequest(new ApiError("INVALID_TOKEN"));
        }

        var result = await userManager.ConfirmEmailAsync(user, token);
        if (!result.Succeeded)
        {
            return Results.BadRequest(new ApiError("INVALID_TOKEN"));
        }

        return Results.Ok();
    }

    private static async Task<IResult> Me(UserManager<ApplicationUser> userManager, HttpContext http)
    {
        var id = http.User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
        if (id is null) return Results.Unauthorized();

        var user = await userManager.FindByIdAsync(id);
        if (user is null) return Results.Unauthorized();

        return Results.Ok(ToMeResponse(user));
    }

    private static MeResponse ToMeResponse(ApplicationUser user) =>
        new(user.Id, user.Email!, user.DisplayName, user.Company, user.Plan);

    private static async Task<IResult> UpdateMe(
        UpdateMeRequest req, UserManager<ApplicationUser> userManager, HttpContext http)
    {
        var user = await CurrentUser(userManager, http);
        if (user is null) return Results.Unauthorized();

        if (req.DisplayName is not null)
        {
            var name = req.DisplayName.Trim();
            // Boşa çekilemez: rapordaki "Hazırlayan" varsayılanı bu addır.
            if (name.Length == 0) return Results.BadRequest(new ApiError("MISSING_FIELDS", new { field = "displayName" }));
            if (name.Length > DisplayNameMax)
            {
                return Results.BadRequest(new ApiError("TOO_LONG", new { field = "displayName", max = DisplayNameMax }));
            }
            user.DisplayName = name;
        }

        if (req.Company is not null)
        {
            var company = req.Company.Trim();
            if (company.Length > CompanyMax)
            {
                return Results.BadRequest(new ApiError("TOO_LONG", new { field = "company", max = CompanyMax }));
            }
            // Boş dize "alanı temizle" demektir; `null` ise alan hiç gönderilmemiş
            // sayılır ve yukarıdaki `is not null` koşulu zaten devreye girmez.
            user.Company = company.Length == 0 ? null : company;
        }

        var result = await userManager.UpdateAsync(user);
        if (!result.Succeeded) return Results.BadRequest(new ApiError("UPDATE_FAILED"));

        return Results.Ok(ToMeResponse(user));
    }

    // Parola değiştirme. `ResetPassword` ile aynı iki adımı yapar — parolayı
    // yazar, sonra o kullanıcının BÜTÜN yenileme token'larını iptal eder —
    // ama bir farkla: burada oturumu değiştiren kişi kullanıcının kendisidir
    // ve o an açık olan sekmesi vardır. Bu yüzden iptalin ardından bu oturum
    // için yeni bir token basılır: diğer bütün cihazlar düşer, kullanıcı
    // kendi ekranında kalır. Aksi hâlde "parolamı değiştirdim" eylemi
    // kullanıcıyı kendi oturumundan da atardı.
    private static async Task<IResult> ChangePassword(
        ChangePasswordRequest req,
        UserManager<ApplicationUser> userManager,
        ITokenService tokenService,
        AppDbContext db,
        HttpContext http,
        IWebHostEnvironment env)
    {
        if (string.IsNullOrWhiteSpace(req.CurrentPassword) || string.IsNullOrWhiteSpace(req.NewPassword))
        {
            return Results.BadRequest(new ApiError("MISSING_FIELDS"));
        }

        var user = await CurrentUser(userManager, http);
        if (user is null)
        {
            return Results.Json(new ApiError("UNAUTHORIZED"), statusCode: StatusCodes.Status401Unauthorized);
        }

        // Mevcut parolayı da Identity doğrular; yanlışsa `PasswordMismatch`
        // koduyla döner ve parola politikası hataları aynı zarfla taşınır.
        var result = await userManager.ChangePasswordAsync(user, req.CurrentPassword, req.NewPassword);
        if (!result.Succeeded)
        {
            return Results.BadRequest(new ApiError("IDENTITY_ERROR", new { codes = result.Errors.Select(e => e.Code) }));
        }

        // "Beni kaydet" seçimi bu oturumun mevcut token'ından okunur: yeni
        // token onu taşımazsa oturum çerezi sessizce kalıcıya (ya da tersi)
        // döner — Refresh'teki `Persistent` aktarımıyla aynı gerekçe.
        var persistent = false;
        if (http.Request.Cookies.TryGetValue(RefreshCookieName, out var rawCookie)
            && !string.IsNullOrEmpty(rawCookie))
        {
            var hash = tokenService.Hash(rawCookie);
            persistent = await db.RefreshTokens
                .Where(t => t.TokenHash == hash)
                .Select(t => t.Persistent)
                .FirstOrDefaultAsync();
        }

        var now = DateTimeOffset.UtcNow;
        await db.RefreshTokens
            .Where(t => t.UserId == user.Id && t.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(t => t.RevokedAt, now));

        return await IssueSession(user, tokenService, db, http, env, persistent);
    }

    private const int DisplayNameMax = 80;
    private const int CompanyMax = 120;

    private static async Task<ApplicationUser?> CurrentUser(UserManager<ApplicationUser> userManager, HttpContext http)
    {
        var id = http.User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
        return id is null ? null : await userManager.FindByIdAsync(id);
    }

    private static async Task<IResult> IssueSession(
        ApplicationUser user, ITokenService tokenService, AppDbContext db, HttpContext http, IWebHostEnvironment env,
        bool persistent)
    {
        var access = tokenService.CreateAccessToken(user);
        var rawRefresh = tokenService.CreateRefreshToken();
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(tokenService.RefreshTokenLifetime);

        db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = tokenService.Hash(rawRefresh),
            ExpiresAt = expiresAt,
            CreatedByIp = http.Connection.RemoteIpAddress?.ToString(),
            CreatedAt = now,
            Persistent = persistent,
        });
        await db.SaveChangesAsync();

        SetRefreshCookie(http, rawRefresh, expiresAt, env, persistent);
        return Results.Ok(new LoginResponse(access.Value, access.ExpiresAt));
    }

    private static void SetRefreshCookie(
        HttpContext http, string rawToken, DateTimeOffset expiresAt, IWebHostEnvironment env, bool persistent)
    {
        http.Response.Cookies.Append(RefreshCookieName, rawToken, new CookieOptions
        {
            HttpOnly = true,
            // Yalnızca geliştirmede gevşetilir: yerel http üzerinde Secure=true
            // çerezi tarayıcı hiç saklamaz, yenileme akışı hiç sınanamaz.
            // Üretimde her zaman true.
            Secure = !env.IsDevelopment(),
            SameSite = SameSiteMode.Strict,
            Path = RefreshCookiePath,
            // "Beni kaydet" seçilmediyse Expires VERİLMEZ: çerez oturum çerezi
            // olur ve tarayıcı kapanınca silinir. Sunucudaki token yine 30 gün
            // geçerli kalır — kısaltmak, aynı hesabın "beni kaydet" seçilmiş
            // başka bir cihazdaki oturumunu da etkilemezdi ama gereksiz;
            // istemcinin çerezi gittiğinde o token zaten kimseye yaramaz.
            Expires = persistent ? expiresAt : null,
        });
    }

    private static void ClearRefreshCookie(HttpContext http)
    {
        http.Response.Cookies.Delete(RefreshCookieName, new CookieOptions { Path = RefreshCookiePath });
    }
}

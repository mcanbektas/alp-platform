using System.Globalization;
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
        group.MapPost("/resend-confirmation", ResendConfirmation).RequireRateLimiting("auth").LimitBodySize(AuthBodyLimitBytes);
        group.MapPost("/reset-password", ResetPassword).RequireRateLimiting("auth").LimitBodySize(AuthBodyLimitBytes);
        // Kilitlenme postasındaki bağlantının ucu. Oturum İSTEMEZ (kullanıcı
        // zaten giremiyor) ve tam da bu yüzden "auth" kovasında durur: login /
        // register ile AYNI IP kotasını paylaşması bilinçlidir — token'ı kaba
        // kuvvetle denemek, aynı kovadan yanlış parola denemekle aynı bütçeye
        // düşsün.
        group.MapPost("/unlock", Unlock).RequireRateLimiting("auth").LimitBodySize(AuthBodyLimitBytes);
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
        // Kullanıcının kendi hesabını silmesi KALDIRILDI (uç dahil, ekranla
        // birlikte). Silme yalnızca yönetim panelinden yapılır —
        // AdminEndpoints.cs. Silme mantığı `AccountDeletion`da durur.
        // Yasal metinlerdeki silme hakkı başvuru adresi üzerinden anlatılır
        // (web/src/data/legalText.js).
    }

    // appsettings.json anahtarı boş dize olarak commit edilir (gizli değer
    // yok), `??` yalnızca null'da devreye girer — boş dize de "ayarlanmamış"
    // sayılmalı. Program.cs'teki CORS kararıyla aynı gerekçe.
    private static string FrontendBaseUrl(IConfiguration config)
    {
        var url = config["App:FrontendBaseUrl"];
        return string.IsNullOrWhiteSpace(url) ? "http://localhost:3000" : url;
    }

    internal static async Task<IResult> Register(
        RegisterRequest req,
        UserManager<ApplicationUser> userManager,
        IEmailSender emailSender,
        AuditLog auditLog,
        IConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password)
            || string.IsNullOrWhiteSpace(req.DisplayName))
        {
            return Results.BadRequest(new ApiError("MISSING_FIELDS"));
        }

        var lang = AuthEmailText.Normalize(req.Lang);

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
                    // Bu postanın alıcısı hesabın GERÇEK sahibi, isteği yapan
                    // ise bir yabancı olabilir: dil burada bir tahmindir.
                    // Kabul edildi, çünkü içerik zararsız (token taşımaz) ve
                    // daha iyisi kalıcı hesap dili olurdu — o da elendi
                    // (docs/eposta-dili-karari.md §1, §7).
                    await emailSender.SendAsync(existing.Email!,
                        AuthEmailText.DuplicateRegistrationSubject(lang),
                        AuthEmailText.DuplicateRegistrationBody(lang));
                }
                return Results.Created();
            }

            return Results.BadRequest(new ApiError("IDENTITY_ERROR", new { codes }));
        }

        await auditLog.WriteAsync(AuditEventCodes.AccountRegistered, actor: user, target: null);

        // Yapılandırmadaki admin listesi hesap AÇILMADAN önce yazılmış olabilir;
        // açılıştaki eşitleme (AdminSeeder.SyncAsync) o an ortada olmayan hesaba
        // yetki veremez. Bu satır olmasaydı yetki bir sonraki yeniden başlatmaya
        // kadar gecikirdi. Yetkinin kaynağı yine yapılandırmadır — kayıt isteği
        // rol talep edemez.
        await AdminSeeder.GrantIfListedAsync(userManager, config, user, auditLog);

        await SendConfirmationMail(user, userManager, emailSender, config, lang);

        return Results.Created();
    }

    // Register ile ResendConfirmation aynı postayı kurar — bağlantı biçimi
    // ayrışırsa ikinci posta ilk ekranda açılmaz.
    private static async Task SendConfirmationMail(
        ApplicationUser user,
        UserManager<ApplicationUser> userManager,
        IEmailSender emailSender,
        IConfiguration config,
        string lang)
    {
        var token = await userManager.GenerateEmailConfirmationTokenAsync(user);
        var link = $"{FrontendBaseUrl(config)}{AuthEmailText.ConfirmEmailPath(lang)}"
            + $"?userId={Uri.EscapeDataString(user.Id)}&token={Uri.EscapeDataString(token)}";
        await emailSender.SendAsync(user.Email!,
            AuthEmailText.ConfirmEmailSubject(lang),
            AuthEmailText.ConfirmEmailBody(lang, link));
    }

    // Doğrulama postası kaybolduğunda tek kurtarma yolu. ForgotPassword ile
    // aynı numaralandırma sözleşmesi: hesap var/yok/zaten doğrulanmış — yanıt
    // her durumda aynı 200. Posta yalnızca gerçekten doğrulanmamış bir hesaba
    // gider; doğrulanmış hesaba tekrar göndermek hem gereksiz hem de "bu adres
    // kayıtlı ve doğrulanmış" bilgisini postayla bile olsa üretmemeli.
    internal static async Task<IResult> ResendConfirmation(
        ResendConfirmationRequest req,
        UserManager<ApplicationUser> userManager,
        IEmailSender emailSender,
        IConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(req.Email)) return Results.BadRequest(new ApiError("MISSING_FIELDS"));

        var user = await userManager.FindByEmailAsync(req.Email);
        if (user is not null && !await userManager.IsEmailConfirmedAsync(user))
        {
            await SendConfirmationMail(user, userManager, emailSender, config,
                AuthEmailText.Normalize(req.Lang));
        }

        return Results.Ok();
    }

    // Numaralandırmaya kapalı giriş: hesap yok / parola yanlış / e-posta
    // doğrulanmamış — parola doğru bilinene kadar HEPSİ aynı 401 yanıtını
    // verir. Kilit ve doğrulama durumu yalnızca parola ispatlandıktan SONRA
    // açığa çıkar, çünkü o noktada saldırgan zaten hesabı "kazanmış" demektir.
    // SignInManager.CheckPasswordSignInAsync bunu güvenli yapmaz: iç PreSignInCheck
    // adımı e-posta doğrulamasını parolayı hiç kontrol etmeden reddediyor — bu
    // yüzden burada UserManager ile elle, sıralı bir akış kuruluyor.
    // `internal`: kilitlenme eşiği testi (auth.lockout denetim izi) doğrudan
    // çağırarak sınar — Refresh'teki kalıpla aynı.
    internal static async Task<IResult> Login(
        LoginRequest req,
        UserManager<ApplicationUser> userManager,
        ITokenService tokenService,
        AppDbContext db,
        AuditLog auditLog,
        IEmailSender emailSender,
        IConfiguration config,
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

        // Kilitliyken parola HİÇ değerlendirilmez. Eskiden kilit yalnızca
        // parola doğru bilindikten sonra kontrol ediliyordu — kilitli hesapta
        // deneme sınırsız sürebiliyor ve doğru tahmin 423 ile kendini belli
        // ediyordu (kilit süresince bile bir kahin). Şimdi kilitli hesapta her
        // deneme, parola doğru olsa da, sıradan INVALID_CREDENTIALS alır:
        // saldırgan kilit boyunca tek bit öğrenemez. Yanıt şekli hesap-yok /
        // parola-yanlış ile aynıdır, kilit durumu dışarı sızmaz; meşru
        // kullanıcı kilidi ancak kilit dolup doğru parolayla girince fark
        // etmez — kabul edilen bedel, aşağıdaki 423 dalı yalnızca kilidin tam
        // o istekte oluştuğu dar aralıkta görünür.
        if (userManager.SupportsUserLockout && await userManager.IsLockedOutAsync(user))
        {
            userManager.PasswordHasher.HashPassword(new ApplicationUser(), req.Password);
            return Results.Json(new ApiError("INVALID_CREDENTIALS"), statusCode: StatusCodes.Status401Unauthorized);
        }

        var passwordOk = await userManager.CheckPasswordAsync(user, req.Password);
        if (!passwordOk)
        {
            if (userManager.SupportsUserLockout)
            {
                var wasLockedOut = await userManager.IsLockedOutAsync(user);
                await userManager.AccessFailedAsync(user);

                // Yalnız eşiği AŞAN denemede düşer — kilitliyken gelen sonraki
                // denemeler zaten yukarıdaki erken dönüşe takılır, tekrar
                // düşmez. Identity eşiğe ulaşınca sayacı kendi içinde sıfırlar
                // (store.ResetAccessFailedCountAsync), bu yüzden sayı BURADAN
                // DEĞİL yapılandırılmış eşikten okunur.
                if (!wasLockedOut && await userManager.IsLockedOutAsync(user))
                {
                    await auditLog.WriteAsync(
                        AuditEventCodes.AuthLockout, actor: null, target: user, http,
                        new { failedCount = userManager.Options.Lockout.MaxFailedAccessAttempts });

                    await SendLockoutMail(user, userManager, emailSender, config, AuthEmailText.Normalize(req.Lang));
                }
            }
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

    // Kilitlenme anında giden bilgilendirme. İKİ bağlantı taşır, çünkü kilit
    // iki farklı sebeple oluşur ve doğru cevapları farklıdır:
    //   - Denemeler kullanıcının kendisiyse parolayı değiştirmek gereksizdir,
    //     kilidi açmak yeter → /kilit-ac.
    //   - Denemeler bir yabancıysa kilidi açmak yetmez, parola da değişmeli
    //     → var olan parola sıfırlama sayfası (yeni bir uç açılmadı).
    //
    // Gönderim best-effort'tur: IEmailSender'ın kendisi hatayı yutar
    // (SmtpEmailSender), yani posta gitmese de giriş yanıtı değişmez — 401
    // her koşulda aynı kalır ve numaralandırma savunması bozulmaz.
    private static async Task SendLockoutMail(
        ApplicationUser user,
        UserManager<ApplicationUser> userManager,
        IEmailSender emailSender,
        IConfiguration config,
        string lang)
    {
        var baseUrl = FrontendBaseUrl(config);

        // Genel amaçlı token sağlayıcısı + KENDİNE ÖZGÜ purpose — e-posta
        // doğrulama ve parola sıfırlamanın zaten yaptığı şey. Yeni bir sağlayıcı
        // kaydı gerekmez; purpose'un tekliği token'ları birbirinden ayırır.
        var unlockToken = await userManager.GenerateUserTokenAsync(
            user, TokenOptions.DefaultProvider, UnlockPurpose(user.LockoutEnd));
        var unlockLink = $"{baseUrl}{AuthEmailText.UnlockAccountPath(lang)}"
            + $"?userId={Uri.EscapeDataString(user.Id)}&token={Uri.EscapeDataString(unlockToken)}";

        // ForgotPassword ile AYNI mekanizma ve AYNI sayfa — ikinci bir sıfırlama
        // yüzeyi açılırsa biri düzeltilip öteki unutulur.
        var resetToken = await userManager.GeneratePasswordResetTokenAsync(user);
        var resetLink = $"{baseUrl}{AuthEmailText.ResetPasswordPath(lang)}"
            + $"?email={Uri.EscapeDataString(user.Email!)}&token={Uri.EscapeDataString(resetToken)}";

        await emailSender.SendAsync(user.Email!,
            AuthEmailText.AccountLockedSubject(lang),
            AuthEmailText.AccountLockedBody(
                lang,
                userManager.Options.Lockout.MaxFailedAccessAttempts,
                (int)userManager.Options.Lockout.DefaultLockoutTimeSpan.TotalMinutes,
                unlockLink,
                resetLink));
    }

    // Kilit açma token'ının `purpose` dizesi — üretim (SendLockoutMail) ve
    // doğrulama (Unlock) TEK bu fonksiyondan geçer.
    //
    // `LockoutEnd` purpose'a GÖMÜLÜR ve token'ı böylece o KİLİT DÖNGÜSÜNE
    // bağlar: kilit doğal süreyle, yönetici eliyle ya da bu bağlantıyla açılıp
    // hesap YENİDEN kilitlenirse `LockoutEnd` yeni bir değer alır, eski
    // token'ın purpose'u artık tutmaz ve doğrulama reddeder. Tekrar oynatma
    // (replay) buradan kapanır — token'ın kendi ömrü (varsayılan 1 gün) tek
    // başına bunu vermez, çünkü aynı gün içindeki ikinci kilidi de açardı.
    // Başarılı açmadan sonra `LockoutEnd` null olur ve purpose "none"a döner:
    // aynı token ikinci kez çalışmaz.
    //
    // Zaman damgası MİLİSANİYE olarak gömülür, `:O` (100 ns çözünürlük) ile
    // DEĞİL. Gerekçe ölçülebilir: PostgreSQL `timestamptz` MİKROSANİYE tutar,
    // .NET tick'i 100 ns'dir — `AccessFailedAsync`in bellekte ürettiği değerin
    // son basamağı veritabanına yazılırken kırpılır ve doğrulama anında geri
    // okunan damga, postayı üreten damgayla ARTIK EŞLEŞMEZ. Testlerdeki SQLite
    // DateTimeOffset'i 7 basamaklı metin olarak sakladığı için bu ayrım orada
    // hiç görünmez: üretimde her kilit açma bağlantısı sessizce çalışmazdı.
    // Milisaniye hem iki sağlayıcıda da tam yuvarlanır hem de iki ayrı kilit
    // döngüsünü ayırmaya fazlasıyla yeter.
    private static string UnlockPurpose(DateTimeOffset? lockoutEnd) =>
        "unlock:" + (lockoutEnd is null
            ? "none"
            : lockoutEnd.Value.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));

    // Kilitlenme postasındaki "kilidi aç" bağlantısının ucu. Parolayı
    // DEĞİŞTİRMEZ, oturum AÇMAZ: yalnızca kilidi kaldırır, kullanıcı ardından
    // normal giriş yapar.
    //
    // Hata şekli tektir ve Login'in numaralandırma savunmasıyla aynı ruhtadır:
    // eksik alan, olmayan kullanıcı ve geçersiz token AYNI `INVALID_TOKEN`
    // 400'ünü verir. Ayrıştırılsalardı bu uç, oturumsuz ve hız sınırı dışında
    // kalan bir "bu kimlik var mı" kahini olurdu — ConfirmEmail de aynı
    // gerekçeyle tek koda düşer.
    internal static async Task<IResult> Unlock(
        UnlockRequest req,
        UserManager<ApplicationUser> userManager,
        AuditLog auditLog,
        HttpContext http)
    {
        if (string.IsNullOrWhiteSpace(req.UserId) || string.IsNullOrWhiteSpace(req.Token))
        {
            return Results.BadRequest(new ApiError("INVALID_TOKEN"));
        }

        var user = await userManager.FindByIdAsync(req.UserId);
        if (user is null) return Results.BadRequest(new ApiError("INVALID_TOKEN"));

        // Purpose O ANKİ LockoutEnd'den kurulur — postadaki token'ın gömülü
        // damgasıyla eşleşmesi şart. Kilit değiştiyse eşleşmez.
        var previousLockoutEnd = user.LockoutEnd;
        var valid = await userManager.VerifyUserTokenAsync(
            user, TokenOptions.DefaultProvider, UnlockPurpose(previousLockoutEnd), req.Token);
        if (!valid) return Results.BadRequest(new ApiError("INVALID_TOKEN"));

        var cleared = await userManager.SetLockoutEndDateAsync(user, null);
        if (!cleared.Succeeded)
        {
            return Results.BadRequest(new ApiError("IDENTITY_ERROR", new { codes = cleared.Errors.Select(e => e.Code) }));
        }

        // Sayaç AYRICA sıfırlanır — AdminEndpoints.UnlockUser'daki gerekçenin
        // aynısı: Identity'nin eşik anında sayacı sıfırlaması iç bir yan
        // etkidir, sözleşme değil. Sayaç dolu kalırsa kilit açıldıktan sonra
        // TEK bir yanlış parola hesabı yeniden kilitler ve kullanıcı "açtım"
        // dediği hâlde yine dışarıda kalır.
        await userManager.ResetAccessFailedCountAsync(user);

        // `actor: null` — istek kimlik doğrulamalı değil; kanıt yalnızca
        // postadaki token. `target` kilidi açılan hesap: `admin.lockout-cleared`
        // ile aynı yerde durur, panel iki kodu tek şablonla okuyabilsin.
        // `via` iki kullanıcı rotasını (bağlantı / parola sıfırlama) üçüncü bir
        // olay kodu açmadan ayırır.
        await auditLog.WriteAsync(
            AuditEventCodes.AuthLockoutCleared, actor: null, target: user, http,
            new { previousLockoutEnd, via = UnlockViaLink });

        return Results.NoContent();
    }

    // Denetim izi detayındaki `via` değerleri — yapısal ve dilsiz (brif 11 §3).
    internal const string UnlockViaLink = "unlock-link";
    internal const string UnlockViaPasswordReset = "password-reset";

    // `internal`: test durum makinesini (rotasyon, yarış penceresi, zincir
    // iptali) doğrudan çağırarak sınar — GetProject'teki kalıpla aynı.
    internal static async Task<IResult> Refresh(
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
                // Pencere dışı tekrar = çalıntı kanıtı. Yalnızca zinciri değil,
                // kullanıcının BÜTÜN aktif oturumları iptal edilir: token bir
                // kez çalındıysa aynı istemcinin başka oturumları da şüphelidir
                // ve zincir iptali saldırganın önceden açtığı ikinci bir
                // oturumu (ayrı login) yaşatıyordu. Meşru kullanıcı bedeli
                // yeniden giriş yapmaktır — çalıntı sonrası doğru bedel.
                await db.RefreshTokens
                    .Where(t => t.UserId == existing.UserId && t.RevokedAt == null)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(t => t.RevokedAt, now));
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

    internal static async Task<IResult> ForgotPassword(
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
            var lang = AuthEmailText.Normalize(req.Lang);
            var token = await userManager.GeneratePasswordResetTokenAsync(user);
            var link = $"{FrontendBaseUrl(config)}{AuthEmailText.ResetPasswordPath(lang)}"
                + $"?email={Uri.EscapeDataString(req.Email)}&token={Uri.EscapeDataString(token)}";
            await emailSender.SendAsync(req.Email,
                AuthEmailText.ResetPasswordSubject(lang),
                AuthEmailText.ResetPasswordBody(lang, link));
        }

        return Results.Ok();
    }

    internal static async Task<IResult> ResetPassword(
        ResetPasswordRequest req,
        UserManager<ApplicationUser> userManager,
        AppDbContext db,
        AuditLog auditLog,
        HttpContext http)
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

        await auditLog.WriteAsync(AuditEventCodes.AuthPasswordReset, actor: user, target: null);

        // Sıfırlama AYRICA aktif bir kilidi de temizler. Bu olmadan akış çıkmaz
        // sokaktı: kilitlenme postası "sen değilsen parolanı sıfırla" diyor ama
        // sıfırlama kilide dokunmasaydı kullanıcı yeni parolasıyla da giremez,
        // kilidin dolmasını beklerdi — hem de tam saldırı şüphesinin olduğu anda.
        // Sıfırlama kilit açmanın kendisinden GÜÇLÜ bir kanıttır (aynı posta
        // kutusu + parola değişimi), yani ayrı bir doğrulama gerekmez.
        //
        // AYRI bir denetim satırı yazılır, `auth.password-reset`e bırakılmaz:
        // "bu sıfırlama aktif bir kilidi de temizledi" güvenlik incelemesi
        // açısından rutin bir parola değişiminden farklı bir sinyaldir, ve
        // kilidin nasıl açıldığı sorusu tek bir olay kodundan (kim: admin mi
        // kullanıcı mı) okunabilmelidir — `admin.lockout-cleared` ile birlikte.
        // Yalnız GERÇEKTEN kilitliyken yazılır: kilitsiz sıfırlamada hiçbir şey
        // değişmediği için iz de bırakılmaz (AdminEndpoints.UnlockUser ve
        // ChangePlan'daki "değer değişmediyse iz yok" kuralı).
        if (userManager.SupportsUserLockout && await userManager.IsLockedOutAsync(user))
        {
            var previousLockoutEnd = user.LockoutEnd;
            await userManager.SetLockoutEndDateAsync(user, null);
            await userManager.ResetAccessFailedCountAsync(user);
            await auditLog.WriteAsync(
                AuditEventCodes.AuthLockoutCleared, actor: null, target: user, http,
                new { previousLockoutEnd, via = UnlockViaPasswordReset });
        }

        return Results.Ok();
    }

    internal static async Task<IResult> ConfirmEmail(
        [FromQuery] string userId,
        [FromQuery] string token,
        UserManager<ApplicationUser> userManager,
        AuditLog auditLog)
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

        await auditLog.WriteAsync(AuditEventCodes.AccountEmailConfirmed, actor: user, target: null);

        return Results.Ok();
    }

    private static async Task<IResult> Me(UserManager<ApplicationUser> userManager, HttpContext http)
    {
        var id = http.User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
        if (id is null) return Results.Unauthorized();

        var user = await userManager.FindByIdAsync(id);
        if (user is null) return Results.Unauthorized();

        return Results.Ok(await ToMeResponse(user, userManager));
    }

    // `isAdmin` yalnızca arayüz içindir — yönetim bağlantısını çizip çizmemeye
    // yarar. Yetki kararı DEĞİLDİR: admin uçları rolü kendileri doğrular
    // (AdminEndpoints.RequireAdmin), istemcinin ne gördüğüne bakmazlar.
    private static async Task<MeResponse> ToMeResponse(
        ApplicationUser user, UserManager<ApplicationUser> userManager) =>
        new(user.Id, user.Email!, user.DisplayName, user.Company, user.Plan,
            await userManager.IsInRoleAsync(user, AdminRole.Name));

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
            if (company.Length > ApplicationUser.CompanyMaxLength)
            {
                return Results.BadRequest(new ApiError("TOO_LONG", new { field = "company", max = ApplicationUser.CompanyMaxLength }));
            }
            // Boş dize "alanı temizle" demektir; `null` ise alan hiç gönderilmemiş
            // sayılır ve yukarıdaki `is not null` koşulu zaten devreye girmez.
            user.Company = company.Length == 0 ? null : company;
        }

        var result = await userManager.UpdateAsync(user);
        if (!result.Succeeded) return Results.BadRequest(new ApiError("UPDATE_FAILED"));

        return Results.Ok(await ToMeResponse(user, userManager));
    }

    // Parola değiştirme. `ResetPassword` ile aynı iki adımı yapar — parolayı
    // yazar, sonra o kullanıcının BÜTÜN yenileme token'larını iptal eder —
    // ama bir farkla: burada oturumu değiştiren kişi kullanıcının kendisidir
    // ve o an açık olan sekmesi vardır. Bu yüzden iptalin ardından bu oturum
    // için yeni bir token basılır: diğer bütün cihazlar düşer, kullanıcı
    // kendi ekranında kalır. Aksi hâlde "parolamı değiştirdim" eylemi
    // kullanıcıyı kendi oturumundan da atardı.
    internal static async Task<IResult> ChangePassword(
        ChangePasswordRequest req,
        UserManager<ApplicationUser> userManager,
        ITokenService tokenService,
        AppDbContext db,
        AuditLog auditLog,
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

        await auditLog.WriteAsync(AuditEventCodes.AuthPasswordChanged, actor: user, target: null, http);

        return await IssueSession(user, tokenService, db, http, env, persistent);
    }

    // Hesabın ve ona bağlı her şeyin silinmesi. KVKK m.7 / m.11 silme hakkının
    // karşılığıdır; aydınlatma metni bu ucu tarif eder.
    //
    // Silme VERİTABANI CASCADE'İNE BIRAKILMAZ, elle ve bağımlılık sırasıyla
    // yapılır. İki ölçülmüş gerekçe:
    //
    // 1. `ThicknessRecords`in hiç foreign key'i yok (AppDbContext yalnız dizin
    //    kuruyor, migration'da `table.ForeignKey` yok). Kullanıcı satırı
    //    silindiğinde bu satırlar sessizce KALIR — ölü bir özelliğin tablosu
    //    ama `UserId` taşıyor, yani geride kişisel veri bırakır.
    // 2. `ReportSnapshotSections → SectionBlobs` bağı `Restrict`. Kullanıcı
    //    silindiğinde Postgres iki cascade tetikleyicisini FK'ların OLUŞTURULMA
    //    SIRASINA göre işletir; bugün çalışmasının tek nedeni `Reports` FK'sının
    //    daha eski olması (manifest önce gidiyor). Sıra terse döndüğünde silme
    //    FK ihlaliyle patlıyor (üretilerek doğrulandı). `Reports→AspNetUsers`
    //    bağını yeniden kuran herhangi bir migration bunu çalışma anında kırardı.
    //
    // Elle silme her iki tuzağı da kapatır ve sırayı görünür kılar.
    private const int DisplayNameMax = 80;

    internal static async Task<ApplicationUser?> CurrentUser(UserManager<ApplicationUser> userManager, HttpContext http)
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

using System.Text;
using System.Threading.RateLimiting;
using Alp.Api.Auth;
using Alp.Api.Common;
using Alp.Api.Projects;
using Alp.Api.Records;
using Alp.Api.Reports;
using Alp.Data;
using Alp.Domain;
using Alp.Reports;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using QuestPDF.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

// ---- Veritabanı ----
builder.Services.AddDbContext<AppDbContext>(opt =>
    opt.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

// ---- Identity ----
// Parola özeti, kilitlenme, e-posta doğrulama Identity'den gelir — elle
// auth yazılmaz. docs/uyelik-ve-rapor-plani.md §4.1
builder.Services
    .AddIdentityCore<ApplicationUser>(opt =>
    {
        opt.Password.RequiredLength = 10;
        opt.SignIn.RequireConfirmedEmail = true;
        opt.User.RequireUniqueEmail = true;
        // Kilitlenme varsayılanları açık: 5 başarısız denemede 5 dk kilit.
        // Login uç noktası kilit durumunu yalnızca parola doğru bilindiğinde
        // açığa çıkarır — bkz. AuthEndpoints.cs → Login.
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders()
    .AddSignInManager();

// ---- JWT ----
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
    ?? throw new InvalidOperationException("Jwt yapılandırması eksik.");

// Boş/çok kısa anahtarla açılış anında değil, ilk korumalı istekte patlaması
// (SymmetricSecurityKey gecikmeli kurulur) teşhisi zorlaştırırdı — burada
// erkenden ve gürültülü şekilde durdurulur.
if (string.IsNullOrWhiteSpace(jwt.Key) || Encoding.UTF8.GetByteCount(jwt.Key) < 32)
{
    throw new InvalidOperationException(
        "Jwt:Key eksik veya çok kısa (HMAC-SHA256 için en az 32 bayt gerekir). " +
        "appsettings.Development.json.example içindeki `openssl rand -base64 48` komutuyla üretin.");
}

builder.Services
    .AddAuthentication(opt =>
    {
        opt.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        opt.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(opt =>
    {
        // JwtBearerHandler varsayılanı "sub" gibi kısa claim adlarını eski
        // WS-Federation URI'lerine (ClaimTypes.NameIdentifier vb.) sessizce
        // eşler — bu açıkken http.User.FindFirst(JwtRegisteredClaimNames.Sub)
        // (auth/rate-limit/project uçlarının hepsinde kullanılan kalıp) hep
        // null döner ve doğrulanmış token yine de 401'e düşer. Kapatılmazsa
        // gerçek bir HTTP isteğiyle asla ortaya çıkmaz — yalnızca gerçek
        // token'la korumalı bir uca gerçekten istek atınca görülür.
        opt.MapInboundClaims = false;
        opt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddSingleton<ITokenService, TokenService>();

// ---- E-posta ----
// SMTP bilgisi verilmişse gerçek gönderici, verilmemişse konsol göndericisi.
// Sessiz düşüş bilinçli: geliştirici SMTP hesabı kurmadan çalışabilsin diye.
// Üretimde bu düşüş sessiz kalmaz — aşağıda uyarı basılır, çünkü
// SignIn.RequireConfirmedEmail açıkken doğrulama postası gitmezse HİÇBİR
// kullanıcı giriş yapamaz.
builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection(SmtpOptions.SectionName));
var smtp = builder.Configuration.GetSection(SmtpOptions.SectionName).Get<SmtpOptions>() ?? new SmtpOptions();
if (smtp.IsConfigured)
{
    builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
}
else
{
    builder.Services.AddSingleton<IEmailSender, ConsoleEmailSender>();
}

// ---- Hız sınırı ----
// IP başına ayrı kova — AddFixedWindowLimiter(name, ...) TEK bir global kova
// kurar (herkes aynı sayaçı paylaşır), o yüzden burada AddPolicy +
// RateLimitPartition.GetFixedWindowLimiter kullanılır: her istemci IP'si
// kendi penceresini alır. docs/uyelik-ve-rapor-plani.md §4.4
static string ClientKey(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

// Kimlik doğrulamalı uçlar için kullanıcı bazlı bölüm — aynı NAT/IP
// arkasındaki farklı kullanıcılar birbirinin kotasını yemesin.
static string UserKey(HttpContext ctx) =>
    ctx.User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
        ?? ClientKey(ctx);

builder.Services.AddRateLimiter(opt =>
{
    opt.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // register / login / forgot-password / reset-password — düşük hacimli,
    // hesap oluşturma ve kimlik doğrulama denemeleri.
    opt.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ClientKey(ctx), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(5),
            QueueLimit = 0,
        }));

    // refresh — her sayfa yüklemesinde sessizce çağrılır (AuthProvider açılışta
    // yeniler), meşru trafik "auth" politikasından çok daha sık gerçekleşir.
    //
    // Neden hâlâ IP başına, oturum başına değil: yenileme token'ı her yenilemede
    // DÖNDÜĞÜ (rotating) için çerezin değeri her istekte farklıdır — çerezi
    // anahtar yapmak meşru oturuma her seferinde taze kova verir (hiç
    // sınırlanmaz) ama çöp çerez gönderen bir saldırgana da öyle verir, yani
    // sınır tamamen kaçar (DB araması seli). Token 256-bit CSPRNG olduğu için
    // bu uçta kaba kuvvet zaten imkânsız; sınırın tek işi kaynak/DoS koruması.
    // O yüzden IP başına kalır, ama NAT/CGNAT arkasındaki çok kullanıcı tek
    // kovayı paylaştığından limit cömert tutulur: 30 çok düşüktü (küçük bir
    // ofiste 30 sayfa yüklemesi 5 dakikada meşru kullanıcıları kilitliyordu).
    opt.AddPolicy("refresh", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ClientKey(ctx), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 120,
            Window = TimeSpan.FromMinutes(5),
            QueueLimit = 0,
        }));

    // logout — kaba kuvvetle bir anlamı yok (yalnızca oturumu kapatır), ama
    // "auth" kovasında olması onu login/register/forgot ile aynı 10'luk IP
    // kotasına sokuyordu; ofisin onbirinci çıkışı ya da girişi 429 alıyordu.
    // Kendi cömert IP kovasına alınır — refresh ile aynı boyutta.
    opt.AddPolicy("logout", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ClientKey(ctx), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 120,
            Window = TimeSpan.FromMinutes(5),
            QueueLimit = 0,
        }));

    // Rapor üretimi CPU/disk yoğun (QuestPDF, ClosedXML) — kimlik doğrulamalı
    // bir hesap bile bunu tekrarlayarak kaynak tüketebilir. Kullanıcı bazlı:
    // aynı ofisteki farklı kullanıcılar birbirinin kotasını paylaşmaz.
    opt.AddPolicy("reports", ctx => RateLimitPartition.GetFixedWindowLimiter(
        UserKey(ctx), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 20,
            Window = TimeSpan.FromMinutes(5),
            QueueLimit = 0,
        }));

    // Oturum açmış kullanıcının yazma uçları (profil, logo, kayıtlar). Rapor
    // kadar pahalı değiller ama hepsi veritabanına yazıyor ve logo yüklemesi
    // yarım megabaytlık gövde taşıyor; sınırsız bırakıldığında tek hesap
    // sürekli yazma üretebilir. Kullanıcı bazlı: bir hesabın gürültüsü
    // diğerlerini etkilemez. Elle kullanımda bu tavana yaklaşmak imkânsız.
    opt.AddPolicy("writes", ctx => RateLimitPartition.GetFixedWindowLimiter(
        UserKey(ctx), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 120,
            Window = TimeSpan.FromMinutes(5),
            QueueLimit = 0,
        }));
});

// ---- CORS ----
// Yalnızca kendi alan adı. Boş dize de "ayarlanmamış" sayılır — appsettings.json
// anahtarı boş dize olarak commit eder, `??` yalnızca null'da devreye girer.
var frontendOrigin = builder.Configuration["App:FrontendBaseUrl"];
if (string.IsNullOrWhiteSpace(frontendOrigin)) frontendOrigin = "http://localhost:3000";
builder.Services.AddCors(opt =>
{
    opt.AddDefaultPolicy(policy =>
        policy.WithOrigins(frontendOrigin).AllowAnyHeader().AllowAnyMethod().AllowCredentials());
});

// ---- Rapor üretimi ----
// docs/uyelik-ve-rapor-plani.md §5, §12 (Faz 1 risk denemesinde doğrulandı).
QuestPDF.Settings.License = LicenseType.Community;

// Üretilen belge için depolama ayarı YOKTUR: rapor diske yazılmaz, "tekrar
// indir" kaydedilmiş hesaplardan yeniden üretir (bkz. ReportEndpoints).
// ---- Data Protection ----
// Identity'nin e-posta doğrulama ve parola sıfırlama jetonları bu anahtarlarla
// korunur. Varsayılan konum konteynerin kendi dosya sistemidir (~/.aspnet):
// konteyner her yeniden yaratıldığında (yani HER dağıtımda) anahtarlar kaybolur
// ve o an postası yolda olan bütün doğrulama/sıfırlama bağlantıları geçersizleşir.
// Kalıcı volume'a yazılır. SetApplicationName sabittir — değişirse eski
// anahtarlarla üretilmiş jetonlar çözülemez.
var keysPath = builder.Configuration["DataProtection:KeysPath"];
if (!string.IsNullOrWhiteSpace(keysPath))
{
    Directory.CreateDirectory(keysPath);
    builder.Services
        .AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(keysPath))
        .SetApplicationName("alp-pcb-toolkit");
}

var logoPath = Path.Combine(builder.Environment.ContentRootPath, "Assets", "logo.png");
var logoBytes = File.ReadAllBytes(logoPath);
// Çözülemeyen bir SVG raporu düşürmez, atlanır — ama sessiz kalmaz: belgede
// yerine not basılır, sunucuda uyarı olarak görünür. Aksi hâlde raporlar
// zamanla çizimsiz çıkar ve kimse fark etmez.
builder.Services.AddSingleton(sp => new PdfReportBuilder(
    logoBytes,
    message => sp.GetRequiredService<ILogger<PdfReportBuilder>>().LogWarning("{Message}", message)));
builder.Services.AddSingleton<XlsxReportBuilder>();

var app = builder.Build();

// ---- Migration ----
// Konteynerde `dotnet ef` yoktur (SDK yok, yalnızca runtime). Bu yüzden şema
// açılışta uygulanır — ama yalnızca açıkça istendiğinde: birden çok kopya aynı
// anda ayağa kalkarsa ikisi birden migration koşmaya çalışır. Tek kopyalı
// dağıtımda (deploy/docker-compose.yml) açıktır; kopya sayısı artarsa kapatılıp
// ayrı bir migration adımına taşınır. docs/uyelik-ve-rapor-plani.md §7
if (builder.Configuration.GetValue("Database:MigrateOnStartup", false))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
}

// Rapor yazı tipleri (Faz 3b). Konteynerde yol `Reports__FontsPath` ile verilir
// (/app/fonts, bkz. api/Dockerfile); yerelde depodaki dizin kullanılır. Dizin
// yoksa sessizce atlanır ve PdfReportBuilder platform yazı tipine düşer.
var fontsPath = builder.Configuration["Reports:FontsPath"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "..", "..", "assets", "report-fonts");
var registeredFonts = ReportFonts.RegisterIfAvailable(fontsPath);
if (!smtp.IsConfigured && !app.Environment.IsDevelopment())
{
    app.Logger.LogWarning(
        "SMTP yapılandırılmadı — doğrulama ve parola sıfırlama postaları yalnızca günlüğe yazılıyor. "
        + "E-posta doğrulaması zorunlu olduğu için kullanıcılar giriş YAPAMAZ. Smtp__Host / Smtp__FromAddress kurun.");
}

if (registeredFonts == 0 && !app.Environment.IsDevelopment())
{
    app.Logger.LogWarning(
        "Rapor yazı tipi bulunamadı ({Path}) — PDF platform yazı tipine düşecek. "
        + "Konteyner tabanında sistem yazı tipi yoksa yazılar boş çıkar. Faz 3b.",
        fontsPath);
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}
else
{
    // Üretimde işlenmemiş bir istisna çıplak 500 döndürüyordu: gövdesi boş,
    // istemci tarafında `API_ERR_PARSE`'a düşen bir yanıt. Ortam `Development`
    // olmadığı için yığın izi sızmıyordu, ama arayüz hatayı diğer uçlarla aynı
    // sözleşmeden okuyamıyordu. Burada hem tek biçimli `{ error }` gövdesi
    // yazılır hem de kayıt garanti altına alınır.
    //
    // Geliştirmede bilinçli olarak KAPALI: orada geliştirici istisna sayfası
    // (yığın izi dahil) daha yararlıdır.
    app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
    {
        var feature = context.Features.Get<IExceptionHandlerFeature>();
        app.Logger.LogError(
            feature?.Error,
            "İşlenmemiş istisna: {Method} {Path}",
            context.Request.Method,
            feature?.Path ?? context.Request.Path.Value);

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new ApiError("SERVER_ERROR"));
    }));
}

// Ters vekil arkasında gerçek istemci IP'sini görebilmek için (hız sınırı ve
// RefreshToken.CreatedByIp bu değeri kullanır). KnownProxies/KnownNetworks
// boşken yalnızca loopback güvenilir; konteynerde nginx loopback'ten gelmez,
// bu yüzden vekilin bulunduğu ağ burada tanıtılmazsa X-Forwarded-For sessizce
// yok sayılır ve BÜTÜN istekler nginx konteynerinin tek IP'sine düşer — hız
// sınırı tek kova olur, kayıtlı IP hep aynı çıkar.
// `App:KnownProxyNetworks` CIDR listesidir (compose ağının alt ağı),
// `App:KnownProxies` tek adres listesi. İkisi de boşsa varsayılan (loopback)
// davranış korunur.
var forwardedOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
};

foreach (var cidr in builder.Configuration.GetSection("App:KnownProxyNetworks").Get<string[]>() ?? [])
{
    // "172.20.0.0/16" — adres ve önek ayrı ayrı ayrıştırılır; bozuk değer
    // sessizce atlanmaz, açılışta durdurur (yanlış yazılmış bir CIDR, hız
    // sınırının çalıştığı sanılırken çalışmaması demektir).
    var parts = cidr.Split('/');
    if (parts.Length != 2
        || !System.Net.IPAddress.TryParse(parts[0], out var network)
        || !int.TryParse(parts[1], out var prefix))
    {
        throw new InvalidOperationException($"App:KnownProxyNetworks içindeki '{cidr}' geçerli bir CIDR değil.");
    }
    forwardedOptions.KnownNetworks.Add(new IPNetwork(network, prefix));
}

foreach (var address in builder.Configuration.GetSection("App:KnownProxies").Get<string[]>() ?? [])
{
    if (!System.Net.IPAddress.TryParse(address, out var proxy))
    {
        throw new InvalidOperationException($"App:KnownProxies içindeki '{address}' geçerli bir IP adresi değil.");
    }
    forwardedOptions.KnownProxies.Add(proxy);
}

app.UseForwardedHeaders(forwardedOptions);

// TLS ters vekilde biter; konteynerdeki uygulama yalnızca HTTP dinler ve
// yönlendirilecek bir HTTPS portu bilmez. Açık bırakılırsa her istekte uyarı
// basar, kapatılırsa yönlendirmeyi nginx yapar. Geliştirmede varsayılan açık.
if (builder.Configuration.GetValue("App:HttpsRedirection", true))
{
    app.UseHttpsRedirection();
}
app.UseCors();
// Authentication/Authorization RateLimiter'dan ÖNCE gelir: "reports"
// politikası kullanıcı kimliğine göre bölümlenir (UserKey), bu da
// ctx.User'ın rate limiter çalışana kadar doldurulmuş olmasını gerektirir.
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// ---- Sağlık uçları ----
// Konteyner düzeyinde iki ayrı soru sorulur ve karıştırılmamalı:
// `/health` süreç ayakta mı (liveness), `/health/ready` veritabanına
// bağlanabiliyor mu (readiness). İkisi tek uçta birleştirilirse geçici bir
// veritabanı kesintisi konteynerin öldürülüp yeniden başlatılmasına yol açar —
// yeniden başlatma veritabanını geri getirmez.
// Yol `/api` altındadır: nginx yalnızca `/api/` konumunu vekile geçirir,
// bunun dışındaki her yol SPA'ya düşer. `/health` yazılsaydı dışarıdan
// istendiğinde index.html dönerdi ve kontrol her zaman "sağlıklı" görünürdü.
app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }))
    .AllowAnonymous()
    .DisableRateLimiting();

app.MapGet("/api/health/ready", async (AppDbContext db, CancellationToken ct) =>
        await db.Database.CanConnectAsync(ct)
            ? Results.Ok(new { status = "ready" })
            : Results.StatusCode(StatusCodes.Status503ServiceUnavailable))
    .AllowAnonymous()
    .DisableRateLimiting();

app.MapAuthEndpoints();
app.MapReportEndpoints();
app.MapProjectEndpoints();
app.MapThicknessRecordEndpoints();

app.Run();

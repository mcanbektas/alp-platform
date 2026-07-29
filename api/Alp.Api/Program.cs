using System.Text;
using System.Threading.RateLimiting;
using Alp.Api.Auth;
using Alp.Api.Projects;
using Alp.Api.Reports;
using Alp.Data;
using Alp.Domain;
using Alp.Reports;
using Microsoft.AspNetCore.Authentication.JwtBearer;
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
builder.Services.AddSingleton<IEmailSender, ConsoleEmailSender>();

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

    // refresh — sekme başına sayfa yüklemesinde sessizce çağrılır, meşru
    // trafik "auth" politikasından daha sık gerçekleşir.
    opt.AddPolicy("refresh", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ClientKey(ctx), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 30,
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

builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.SectionName));

var logoPath = Path.Combine(builder.Environment.ContentRootPath, "Assets", "logo.png");
builder.Services.AddSingleton(new PdfReportBuilder(File.ReadAllBytes(logoPath)));
builder.Services.AddSingleton<XlsxReportBuilder>();

var app = builder.Build();

// Faz 3b tamamlanınca web/public/fonts/ altına gerçek dosyalar konacak; o ana
// kadar dizin yok, sessizce atlanır (PdfReportBuilder platform yazı tipine düşer).
var fontsPath = builder.Configuration["Reports:FontsPath"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "..", "..", "web", "public", "fonts");
ReportFonts.RegisterIfAvailable(fontsPath);

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Ters vekil arkasında gerçek istemci IP'sini görebilmek için (hız sınırı ve
// RefreshToken.CreatedByIp bu değeri kullanır). KnownProxies/KnownNetworks
// boşken yalnızca loopback güvenilir — üretim dağıtımında (Faz 8) vekilin
// gerçek adresi buraya eklenir, yoksa varsayılan olarak başlık yok sayılır.
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
});

app.UseHttpsRedirection();
app.UseCors();
// Authentication/Authorization RateLimiter'dan ÖNCE gelir: "reports"
// politikası kullanıcı kimliğine göre bölümlenir (UserKey), bu da
// ctx.User'ın rate limiter çalışana kadar doldurulmuş olmasını gerektirir.
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapAuthEndpoints();
app.MapReportEndpoints();
app.MapProjectEndpoints();

app.Run();

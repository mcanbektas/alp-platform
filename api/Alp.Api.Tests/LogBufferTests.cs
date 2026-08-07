using Alp.Api.Auth;
using Alp.Api.Logging;
using Alp.Data;
using Alp.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Events;
using Serilog.Filters;
using Serilog.Parsing;

namespace Alp.Api.Tests;

// Operasyonel log ekranının kaynağı (docs/brifler/12-loglama-ekrani.md §3).
// Denetim iziyle (AuditLogTests.cs) KARIŞTIRILMAZ — bu tampon uçucudur.
public class LogBufferSinkTests
{
    private static readonly MessageTemplateParser Parser = new();

    private static LogEvent MakeEvent(
        LogEventLevel level,
        string message,
        string? sourceContext = null,
        string? requestPath = null,
        string? userId = null,
        Exception? exception = null,
        DateTimeOffset? at = null)
    {
        var props = new List<LogEventProperty>();
        if (sourceContext is not null) props.Add(new LogEventProperty("SourceContext", new ScalarValue(sourceContext)));
        if (requestPath is not null) props.Add(new LogEventProperty("RequestPath", new ScalarValue(requestPath)));
        if (userId is not null) props.Add(new LogEventProperty("UserId", new ScalarValue(userId)));

        return new LogEvent(at ?? DateTimeOffset.UtcNow, level, exception, Parser.Parse(message), props);
    }

    [Fact]
    public void kapasite_asilinca_en_eski_kayit_dusuyor()
    {
        var sink = new LogBufferSink(capacity: 3);
        for (var i = 0; i < 4; i++)
        {
            sink.Emit(MakeEvent(LogEventLevel.Information, $"olay-{i}"));
        }

        var snapshot = sink.Snapshot();
        Assert.Equal(3, snapshot.Count);
        Assert.DoesNotContain(snapshot, e => e.Message == "olay-0");
        Assert.Contains(snapshot, e => e.Message == "olay-3");
    }

    // Sağlık ucu ve gürültülü Debug satırları panele hiç girmemeli — eşik
    // sink'in kendi kapısı (Emit içinde), Program.cs'teki WriteTo.Sink
    // çağrısında AYRICA tekrarlanmaz.
    [Fact]
    public void information_altindaki_seviyeler_tampona_girmez()
    {
        var sink = new LogBufferSink(capacity: 10);
        sink.Emit(MakeEvent(LogEventLevel.Verbose, "gorunmemeli-verbose"));
        sink.Emit(MakeEvent(LogEventLevel.Debug, "gorunmemeli-debug"));
        sink.Emit(MakeEvent(LogEventLevel.Information, "gorunmeli"));

        var entry = Assert.Single(sink.Snapshot());
        Assert.Equal("gorunmeli", entry.Message);
    }

    // Yığın izi stdout'ta zaten var; tamponda yalnız ilk satır (mesaj)
    // tutulur, bellek satır sayısıyla şişmesin.
    [Fact]
    public void istisnanin_yalniz_ilk_satiri_saklanir()
    {
        var sink = new LogBufferSink(capacity: 10);
        var ex = new InvalidOperationException("dış hata");

        sink.Emit(MakeEvent(LogEventLevel.Error, "patladi", exception: ex));

        var entry = Assert.Single(sink.Snapshot());
        Assert.NotNull(entry.Exception);
        Assert.DoesNotContain('\n', entry.Exception);
        Assert.Contains("dış hata", entry.Exception);
    }

    [Fact]
    public void kaynak_yol_ve_kullanici_ozellikleri_okunur()
    {
        var sink = new LogBufferSink(capacity: 10);
        sink.Emit(MakeEvent(
            LogEventLevel.Information, "istek tamamlandi",
            sourceContext: "Alp.Api.Auth.AuditLog", requestPath: "/api/admin/users", userId: "u1"));

        var entry = Assert.Single(sink.Snapshot());
        Assert.Equal("Alp.Api.Auth.AuditLog", entry.SourceContext);
        Assert.Equal("/api/admin/users", entry.RequestPath);
        Assert.Equal("u1", entry.UserId);
    }

    // Regresyon (bulgu, 2026-08-07): ConsoleEmailSender dev'de e-posta
    // gövdesini — doğrulama/parola sıfırlama TOKEN'ı dahil — stdout'a yazar
    // (IEmailSender.cs üstündeki gerekçe: geliştirici konsoldan okuyabilsin
    // diye). LogBufferSink eklenince bu satır ilk turda panelde de göründü —
    // web admin girişi terminal erişiminden daha geniş bir yüzey, token'ı
    // oraya taşımak bu sınıfın var olma sebebini deler. Program.cs'teki
    // WriteTo.Logger(...).Filter.ByExcluding(Matching.FromSource<...>())
    // zincirinin AYNISI burada kurulup doğrulanır — stdout'ta kalır, tampona
    // hiç girmez.
    [Fact]
    public void console_email_sender_kaynakli_satirlar_tampona_hic_girmez()
    {
        var buffer = new LogBufferSink(capacity: 10);
        using var logger = new LoggerConfiguration()
            .WriteTo.Logger(lc => lc
                .Filter.ByExcluding(Matching.FromSource<ConsoleEmailSender>())
                .WriteTo.Sink(buffer))
            .CreateLogger();

        logger.ForContext<ConsoleEmailSender>().Information(
            "[dev e-posta] Kime: {To} — Konu: {Subject}\n{Body}", "x@ornek.test", "Konu", "gizli-token");
        logger.ForContext<AuditLog>().Information("normal satir");

        var snapshot = buffer.Snapshot();
        var row = Assert.Single(snapshot);
        Assert.Equal("normal satir", row.Message);
        Assert.DoesNotContain(snapshot, e => e.Message.Contains("gizli-token"));
    }

    [Fact]
    public void ozellik_verilmeyen_alanlar_null_kalir()
    {
        var sink = new LogBufferSink(capacity: 10);
        sink.Emit(MakeEvent(LogEventLevel.Information, "sade mesaj"));

        var entry = Assert.Single(sink.Snapshot());
        Assert.Null(entry.SourceContext);
        Assert.Null(entry.RequestPath);
        Assert.Null(entry.UserId);
        Assert.Null(entry.Exception);
    }
}

// GET /api/admin/logs — yetki + süzme + take tavanı. Kurulum AuditLogTests'in
// ServiceCollection desenini birebir izler.
public class ListLogsEndpointTests : IDisposable
{
    private readonly TestDb db = new();
    private readonly ServiceProvider provider;
    private readonly IServiceScope scope;

    public ListLogsEndpointTests()
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
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<AppDbContext>()
            .AddDefaultTokenProviders();

        provider = services.BuildServiceProvider();
        scope = provider.CreateScope();
    }

    private UserManager<ApplicationUser> Users =>
        scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

    private RoleManager<IdentityRole> Roles =>
        scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();

    private const string Password = "Parola-12345!";

    // TestDb.AddUser KULLANILMAZ: o ayrı bir DbContext'te ekler ve o kullanıcıyı
    // sonradan UserManager.AddToRoleAsync'e vermek (UpdateAsync → context.Attach)
    // "aynı Id'li başka bir instance zaten tracked" hatasıyla patlar — kullanıcı
    // UserManager'IN KENDİ context'inde yaratılmalı (AuditLogTests.NewUserAsync
    // deseninin aynısı).
    private async Task<ApplicationUser> NewUserAsync(string email)
    {
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = email,
            Email = email,
            DisplayName = email,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var result = await Users.CreateAsync(user, Password);
        Assert.True(result.Succeeded, string.Join(",", result.Errors.Select(e => e.Code)));
        return user;
    }

    private async Task<ApplicationUser> NewAdminAsync(string email = "yonetici-logs@ornek.test")
    {
        var admin = await NewUserAsync(email);
        if (!await Roles.RoleExistsAsync(AdminRole.Name))
        {
            await Roles.CreateAsync(new IdentityRole(AdminRole.Name));
        }
        await Users.AddToRoleAsync(admin, AdminRole.Name);
        return admin;
    }

    private static LogBufferSink Sink(int capacity, params (LogEventLevel Level, string Message)[] entries)
    {
        var sink = new LogBufferSink(capacity);
        var parser = new MessageTemplateParser();
        foreach (var (level, message) in entries)
        {
            sink.Emit(new LogEvent(DateTimeOffset.UtcNow, level, null, parser.Parse(message), []));
        }
        return sink;
    }

    [Fact]
    public async Task rolsuz_kullanici_loglari_goremez()
    {
        var caller = await NewUserAsync("siradan-logs@ornek.test");
        var sink = Sink(10, (LogEventLevel.Information, "olay"));

        var result = await AdminEndpoints.ListLogs(sink, Users, TestHttp.For(caller));

        Assert.Equal(StatusCodes.Status403Forbidden, ResultAssert.Status(result));
    }

    [Fact]
    public async Task yeni_kayit_uste_siralanir()
    {
        var admin = await NewAdminAsync();
        var sink = new LogBufferSink(10);
        var parser = new MessageTemplateParser();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        sink.Emit(new LogEvent(t0, LogEventLevel.Information, null, parser.Parse("once"), []));
        sink.Emit(new LogEvent(t0.AddMinutes(5), LogEventLevel.Information, null, parser.Parse("sonra"), []));

        var page = ResultAssert.Value<LogPage>(await AdminEndpoints.ListLogs(sink, Users, TestHttp.For(admin)));

        Assert.Equal("sonra", page.Items[0].Message);
        Assert.Equal("once", page.Items[1].Message);
    }

    [Fact]
    public async Task level_warning_information_satirlarini_disarida_birakir()
    {
        var admin = await NewAdminAsync();
        var sink = Sink(10,
            (LogEventLevel.Information, "bilgi"),
            (LogEventLevel.Warning, "uyari"),
            (LogEventLevel.Error, "hata"));

        var page = ResultAssert.Value<LogPage>(
            await AdminEndpoints.ListLogs(sink, Users, TestHttp.For(admin), level: "warning"));

        Assert.Equal(2, page.Items.Count);
        Assert.DoesNotContain(page.Items, i => i.Message == "bilgi");
    }

    [Fact]
    public async Task level_error_yalniz_error_ve_ustunu_dondurur()
    {
        var admin = await NewAdminAsync();
        var sink = Sink(10,
            (LogEventLevel.Information, "bilgi"),
            (LogEventLevel.Warning, "uyari"),
            (LogEventLevel.Error, "hata"));

        var page = ResultAssert.Value<LogPage>(
            await AdminEndpoints.ListLogs(sink, Users, TestHttp.For(admin), level: "error"));

        var row = Assert.Single(page.Items);
        Assert.Equal("hata", row.Message);
    }

    [Fact]
    public async Task q_mesaj_icinde_buyuk_kucuk_harf_duyarsiz_arar()
    {
        var admin = await NewAdminAsync();
        var sink = Sink(10,
            (LogEventLevel.Information, "Rapor üretildi"),
            (LogEventLevel.Information, "Kayıt silindi"));

        var page = ResultAssert.Value<LogPage>(
            await AdminEndpoints.ListLogs(sink, Users, TestHttp.For(admin), q: "rapor"));

        var row = Assert.Single(page.Items);
        Assert.Equal("Rapor üretildi", row.Message);
    }

    // Sınır SUNUCUDA: ListAudit/ListUsers'la aynı gerekçe — take=100000
    // tamponun tamamından fazlasını isteyemez, tavan sink'in kapasitesidir.
    [Fact]
    public async Task asiri_take_tampon_kapasitesine_kirpilir()
    {
        var admin = await NewAdminAsync();
        var sink = new LogBufferSink(capacity: 5);
        var parser = new MessageTemplateParser();
        for (var i = 0; i < 5; i++)
        {
            sink.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Information, null, parser.Parse($"olay-{i}"), []));
        }

        var page = ResultAssert.Value<LogPage>(
            await AdminEndpoints.ListLogs(sink, Users, TestHttp.For(admin), take: 100_000));

        Assert.Equal(5, page.Capacity);
        Assert.Equal(5, page.Items.Count);
    }

    public void Dispose()
    {
        scope.Dispose();
        provider.Dispose();
        db.Dispose();
        GC.SuppressFinalize(this);
    }
}

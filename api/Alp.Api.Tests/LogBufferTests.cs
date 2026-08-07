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
        string? requestId = null,
        Exception? exception = null,
        DateTimeOffset? at = null,
        IEnumerable<LogEventProperty>? extraProperties = null)
    {
        var props = new List<LogEventProperty>();
        if (sourceContext is not null) props.Add(new LogEventProperty("SourceContext", new ScalarValue(sourceContext)));
        if (requestPath is not null) props.Add(new LogEventProperty("RequestPath", new ScalarValue(requestPath)));
        if (userId is not null) props.Add(new LogEventProperty("UserId", new ScalarValue(userId)));
        if (requestId is not null) props.Add(new LogEventProperty("RequestId", new ScalarValue(requestId)));
        if (extraProperties is not null) props.AddRange(extraProperties);

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

    // Brif 12'nin "yalnız ilk satır" kararının F2'de (docs/brifler/
    // 14-loglama-altyapi.md §3) BİLİNÇLİ revizyonu: karta artık tam metin
    // gerekiyor (tam yığın izi de dahil, "Görüntüle" gerçekten detaylı
    // olsun diye), yalnız TAVANLI — sınırsız değil, bkz. aşağıdaki kısaltma
    // testi.
    [Fact]
    public void istisnanin_artik_tam_metni_saklanir()
    {
        var sink = new LogBufferSink(capacity: 10);
        var ex = new InvalidOperationException("dış hata\nikinci satır");

        sink.Emit(MakeEvent(LogEventLevel.Error, "patladi", exception: ex));

        var entry = Assert.Single(sink.Snapshot());
        Assert.NotNull(entry.Exception);
        Assert.Contains('\n', entry.Exception);
        Assert.Contains("ikinci satır", entry.Exception);
        Assert.DoesNotContain("kısaltıldı", entry.Exception);
    }

    [Fact]
    public void istisna_4000_karakteri_asinca_kisaltilip_isaretlenir()
    {
        var sink = new LogBufferSink(capacity: 10);
        var ex = new InvalidOperationException(new string('x', 5000));

        sink.Emit(MakeEvent(LogEventLevel.Error, "patladi", exception: ex));

        var entry = Assert.Single(sink.Snapshot());
        Assert.NotNull(entry.Exception);
        Assert.EndsWith("… (kısaltıldı)", entry.Exception);
        // Tavan var: 4000 + işaret, sınırsız büyümüyor.
        Assert.True(entry.Exception.Length < 4100, $"beklenenden uzun: {entry.Exception.Length}");
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
        Assert.Null(entry.RequestId);
        Assert.Null(entry.Exception);
        Assert.Empty(entry.Properties);
        Assert.Equal(0, entry.TruncatedPropertyCount);
    }

    // F2 — ayrıntı zenginleştirme (docs/brifler/14-loglama-altyapi.md §3).
    // İstek-tamamlama olayının property'leri (RequestMethod/StatusCode/
    // Elapsed/ClientIp) ZATEN üretiliyordu, sink onları atıyordu — artık
    // tavanlı bir kopyaya giriyorlar.
    [Fact]
    public void bilinmeyen_ozellikler_sozluge_girer()
    {
        var sink = new LogBufferSink(capacity: 10);
        sink.Emit(MakeEvent(LogEventLevel.Information, "istek tamamlandi", extraProperties:
        [
            new LogEventProperty("RequestMethod", new ScalarValue("GET")),
            new LogEventProperty("StatusCode", new ScalarValue(200)),
            new LogEventProperty("Elapsed", new ScalarValue(12.5)),
        ]));

        var entry = Assert.Single(sink.Snapshot());
        Assert.Equal("GET", entry.Properties["RequestMethod"]);
        Assert.Equal("200", entry.Properties["StatusCode"]);
        Assert.Equal("12.5", entry.Properties["Elapsed"]);
        // Tırnaksız: ScalarValue.ToString() Serilog biçimlendiricisinden
        // geçseydi "GET" tırnaklı çıkardı — burada ham CLR değeri.
        Assert.DoesNotContain('"', entry.Properties["RequestMethod"]);
    }

    // Zaten özel kolonu olan dört alan (SourceContext/RequestPath/UserId/
    // RequestId) sözlüğe İKİNCİ KEZ girmez — çift gösterim olmasın.
    [Fact]
    public void ozel_alanlar_sozlukte_tekrar_etmez()
    {
        var sink = new LogBufferSink(capacity: 10);
        sink.Emit(MakeEvent(
            LogEventLevel.Information, "istek tamamlandi",
            sourceContext: "Kaynak", requestPath: "/api/x", userId: "u1",
            requestId: "0123456789abcdef0123456789abcdef"));

        var entry = Assert.Single(sink.Snapshot());
        Assert.DoesNotContain("SourceContext", entry.Properties.Keys);
        Assert.DoesNotContain("RequestPath", entry.Properties.Keys);
        Assert.DoesNotContain("UserId", entry.Properties.Keys);
        Assert.DoesNotContain("RequestId", entry.Properties.Keys);
    }

    [Fact]
    public void yirmi_dorttan_fazla_ozellik_kirpilir_sayac_dogru()
    {
        var sink = new LogBufferSink(capacity: 10);
        var extra = Enumerable.Range(0, 30)
            .Select(i => new LogEventProperty($"P{i:D2}", new ScalarValue(i)));

        sink.Emit(MakeEvent(LogEventLevel.Information, "cok ozellikli", extraProperties: extra));

        var entry = Assert.Single(sink.Snapshot());
        Assert.Equal(24, entry.Properties.Count);
        Assert.Equal(6, entry.TruncatedPropertyCount);
    }

    // Review bulgusu: `IFormattable` invariant biçimi DateTime/DateTimeOffset
    // için varsayılan "G" — ay/gün sırası BELİRSİZ (08/07 mi 7 Ağustos mu
    // 8 Temmuz mu). Round-trip "O" (ISO 8601) tektip ve sıralanabilir.
    [Fact]
    public void tarih_ozelligi_iso8601_biciminde_yazilir()
    {
        var sink = new LogBufferSink(capacity: 10);
        var at = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        sink.Emit(MakeEvent(LogEventLevel.Information, "tarihli ozellik", extraProperties:
        [
            new LogEventProperty("BaslangicZamani", new ScalarValue(at)),
        ]));

        var entry = Assert.Single(sink.Snapshot());
        Assert.StartsWith("2026-03-04T05:06:07", entry.Properties["BaslangicZamani"]);
    }

    // Canlı turda yakalandı (docs/brifler/14-loglama-altyapi.md doğrulaması):
    // `double`nin tam ("G") yazımı "Süre (ms)" gibi bir alanda 6 basamağa
    // kadar gürültü basıyordu ("1.167333"). 3 ondalığa kırpılır, gereksiz
    // sıfır basılmaz.
    [Fact]
    public void ondalik_ozellik_uc_basamaga_kirpilir()
    {
        var sink = new LogBufferSink(capacity: 10);
        sink.Emit(MakeEvent(LogEventLevel.Information, "sureli olay", extraProperties:
        [
            new LogEventProperty("Elapsed", new ScalarValue(1.1673334444d)),
            new LogEventProperty("TamSayiliOran", new ScalarValue(2.0d)),
        ]));

        var entry = Assert.Single(sink.Snapshot());
        Assert.Equal("1.167", entry.Properties["Elapsed"]);
        Assert.Equal("2", entry.Properties["TamSayiliOran"]); // gereksiz ".000" yok
    }

    [Fact]
    public void uzun_ozellik_degeri_256_karakterde_kirpilir()
    {
        var sink = new LogBufferSink(capacity: 10);
        var longValue = new string('a', 500);
        sink.Emit(MakeEvent(LogEventLevel.Information, "uzun deger", extraProperties:
        [
            new LogEventProperty("Uzun", new ScalarValue(longValue)),
        ]));

        var entry = Assert.Single(sink.Snapshot());
        var stored = entry.Properties["Uzun"];
        Assert.EndsWith("…", stored);
        Assert.True(stored.Length <= 257, $"beklenenden uzun: {stored.Length}");
    }

    // İstek korelasyonu (docs/brifler/14-loglama-altyapi.md §2) — kimlik
    // `RequestIdMiddleware`nin bastığı `LogContext` özelliğinden okunur,
    // diğer üç alanla (kaynak/yol/kullanıcı) AYNI `PropertyString` deseniyle.
    [Fact]
    public void istek_kimligi_ozelligi_okunur()
    {
        var sink = new LogBufferSink(capacity: 10);
        sink.Emit(MakeEvent(LogEventLevel.Information, "istek tamamlandi", requestId: "0123456789abcdef0123456789abcdef"));

        var entry = Assert.Single(sink.Snapshot());
        Assert.Equal("0123456789abcdef0123456789abcdef", entry.RequestId);
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

    // Admin panoya yapıştırdığı istek kimliğini yapıştırınca o isteğin
    // bütün satırlarını bulabilmesi — özelliğin asıl kullanım senaryosu
    // (docs/brifler/14-loglama-altyapi.md §2).
    [Fact]
    public async Task q_istek_kimliginde_de_arar()
    {
        var admin = await NewAdminAsync();
        var sink = new LogBufferSink(10);
        var parser = new MessageTemplateParser();
        sink.Emit(new LogEvent(
            DateTimeOffset.UtcNow, LogEventLevel.Information, null, parser.Parse("eslesen"),
            [new LogEventProperty("RequestId", new ScalarValue("0123456789abcdef0123456789abcdef"))]));
        sink.Emit(new LogEvent(
            DateTimeOffset.UtcNow, LogEventLevel.Information, null, parser.Parse("eslesmeyen"),
            [new LogEventProperty("RequestId", new ScalarValue("ffffffffffffffffffffffffffffffff"))]));

        var page = ResultAssert.Value<LogPage>(
            await AdminEndpoints.ListLogs(sink, Users, TestHttp.For(admin), q: "0123456789abcdef"));

        var row = Assert.Single(page.Items);
        Assert.Equal("eslesen", row.Message);
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

    // Review bulgusu: `LogEntryRow` artık 10 parametreli, ardışık altısı
    // aynı tip (`string?`) — pozisyonel bir karışıklık (ör. RequestId ile
    // Properties'in yeri değişmesi) DERLENIR ama sessizce yanlış veri
    // gösterir. Her alana FARKLI bir değer vererek AdminEndpoints.ListLogs'un
    // gerçek eşleme sırasını (LogBufferEntry alanı → LogEntryRow slotu)
    // uçtan uca doğrular — yalnız "null değil" değil, "doğru SLOTTA".
    [Fact]
    public async Task her_alan_dogru_slota_eslenir()
    {
        var admin = await NewAdminAsync();
        var sink = new LogBufferSink(10);
        var parser = new MessageTemplateParser();
        var ex = new InvalidOperationException("hata-metni");
        sink.Emit(new LogEvent(
            DateTimeOffset.UtcNow, LogEventLevel.Error, ex, parser.Parse("mesaj-degeri"),
            [
                new LogEventProperty("SourceContext", new ScalarValue("kaynak-degeri")),
                new LogEventProperty("RequestPath", new ScalarValue("/yol-degeri")),
                new LogEventProperty("UserId", new ScalarValue("kullanici-degeri")),
                new LogEventProperty("RequestId", new ScalarValue("istek-kimligi-degeri")),
                new LogEventProperty("OzelAlan", new ScalarValue("ozel-deger")),
            ]));

        var page = ResultAssert.Value<LogPage>(await AdminEndpoints.ListLogs(sink, Users, TestHttp.For(admin)));

        var row = Assert.Single(page.Items);
        Assert.Equal("mesaj-degeri", row.Message);
        Assert.Equal("Error", row.Level);
        Assert.Equal("kaynak-degeri", row.SourceContext);
        Assert.Equal("/yol-degeri", row.RequestPath);
        Assert.Equal("kullanici-degeri", row.UserId);
        Assert.Equal("istek-kimligi-degeri", row.RequestId);
        Assert.Contains("hata-metni", row.Exception);
        Assert.Equal("ozel-deger", row.Properties["OzelAlan"]);
        Assert.Equal(0, row.TruncatedPropertyCount);
    }

    // F2 — özellik sözlüğü sink'ten uca kadar aynı satırda kalıyor mu
    // (LogBufferEntry.Properties → LogEntryRow.Properties, pozisyonel
    // record'da yer değiştirme riskine karşı bkz. review notu).
    [Fact]
    public async Task ozellik_sozlugu_uca_kadar_tasinir()
    {
        var admin = await NewAdminAsync();
        var sink = new LogBufferSink(10);
        var parser = new MessageTemplateParser();
        sink.Emit(new LogEvent(
            DateTimeOffset.UtcNow, LogEventLevel.Information, null, parser.Parse("istek tamamlandi"),
            [new LogEventProperty("StatusCode", new ScalarValue(500))]));

        var page = ResultAssert.Value<LogPage>(await AdminEndpoints.ListLogs(sink, Users, TestHttp.For(admin)));

        var row = Assert.Single(page.Items);
        Assert.Equal("500", row.Properties["StatusCode"]);
        Assert.Equal(0, row.TruncatedPropertyCount);
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

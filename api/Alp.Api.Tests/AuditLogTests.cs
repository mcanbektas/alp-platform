using System.Text.Json;
using Alp.Api.Auth;
using Alp.Data;
using Alp.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Alp.Api.Tests;

// Denetim izi çekirdeği (docs/brifler/11-loglama.md §3). İki dayanıklılık
// yolu ayrı ayrı sınanır:
//   - admin.user-deleted: KRİTİK, silme transaction'ının içinde yazılır —
//     iz yazılamıyorsa silme de olmaz.
//   - auth.lockout: BEST-EFFORT, kilitlenme eşiğini AŞAN denemede düşer.
public class AuditLogTests : IDisposable
{
    private readonly TestDb db = new();
    private readonly ServiceProvider provider;
    private readonly IServiceScope scope;

    public AuditLogTests()
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
        services.AddScoped<AuditLog>();

        provider = services.BuildServiceProvider();
        scope = provider.CreateScope();
    }

    private UserManager<ApplicationUser> Users =>
        scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

    private AppDbContext Scoped => scope.ServiceProvider.GetRequiredService<AppDbContext>();

    private AuditLog Audit => scope.ServiceProvider.GetRequiredService<AuditLog>();

    private RoleManager<IdentityRole> Roles =>
        scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();

    private const string Password = "Parola-12345!";

    private async Task<ApplicationUser> NewAdminAsync(string email = "yonetici-audit@ornek.test")
    {
        var admin = await NewUserAsync(email);
        if (!await Roles.RoleExistsAsync(AdminRole.Name))
        {
            await Roles.CreateAsync(new IdentityRole(AdminRole.Name));
        }
        await Users.AddToRoleAsync(admin, AdminRole.Name);
        return admin;
    }

    private void SeedAudit(
        string @event, string? actorEmail, string? targetEmail, DateTimeOffset occurredAt, string? detailJson = null)
    {
        using var seed = db.NewContext();
        seed.AuditEvents.Add(new AuditEvent
        {
            Event = @event, ActorEmail = actorEmail, TargetEmail = targetEmail,
            OccurredAt = occurredAt, DetailJson = detailJson,
        });
        seed.SaveChanges();
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

    private sealed class NoopEmailSender : IEmailSender
    {
        public Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private static readonly IEmailSender Mail = new NoopEmailSender();

    private static IConfiguration ConfigWithAdmins(params string[] emails) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [AdminSeeder.ConfigKey] = string.Join(",", emails) })
            .Build();

    // Parolası olan gerçek kullanıcı — AdminEndpointsTests ile aynı gerekçe:
    // giriş/silme akışları UserManager'dan doğan bir kullanıcı ister.
    private async Task<ApplicationUser> NewUserAsync(string email, string password = Password)
    {
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = email,
            Email = email,
            DisplayName = email,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var result = await Users.CreateAsync(user, password);
        Assert.True(result.Succeeded, string.Join(",", result.Errors.Select(e => e.Code)));
        return user;
    }

    // ---- admin.user-deleted: kritik, transaction içi ----

    [Fact]
    public async Task silme_admin_user_deleted_izini_actor_target_snapshotuyla_yazar()
    {
        var admin = await NewUserAsync("yonetici@ornek.test");
        var target = await NewUserAsync("silinecek@ornek.test");

        using (var seed = db.NewContext())
        {
            var project = new Project
            {
                Id = Guid.NewGuid(), UserId = target.Id, Name = "Proje",
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            };
            seed.Projects.Add(project);
            seed.Reports.Add(new Report
            {
                Id = Guid.NewGuid(), UserId = target.Id, ProjectId = project.Id,
                Title = "Rapor", PreparedBy = "Ad", Company = "Firma",
                Format = ReportFormat.Pdf, Revision = 1, SchemaVersion = 1,
                FileSize = 10, GeneratedAt = DateTimeOffset.UtcNow,
            });
            seed.SaveChanges();
        }

        var result = await AccountDeletion.DeleteAsync(Users, Scoped, target, Audit, admin, TestHttp.Anonymous());
        Assert.True(result.Succeeded);

        using var after = db.NewContext();
        var row = Assert.Single(after.AuditEvents.Where(a => a.Event == AuditEventCodes.AdminUserDeleted));
        Assert.Equal(admin.Id, row.ActorUserId);
        Assert.Equal("yonetici@ornek.test", row.ActorEmail);
        Assert.Equal(target.Id, row.TargetUserId);
        Assert.Equal("silinecek@ornek.test", row.TargetEmail);

        using var doc = JsonDocument.Parse(row.DetailJson!);
        Assert.Equal(1, doc.RootElement.GetProperty("projectCount").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("reportCount").GetInt32());
    }

    // DetailJson yapısal ve dilsizdir — cümle değil alan taşır. Alan kümesi
    // TAM olarak beklenen ikiliye eşit ve her değer sayı olmalı; serbest
    // metin sızarsa (ör. bir "message" alanı) bu test düşer.
    [Fact]
    public async Task detail_json_yapisal_alanlar_tasir_serbest_metin_tasimaz()
    {
        var admin = await NewUserAsync("yonetici2@ornek.test");
        var target = await NewUserAsync("silinecek2@ornek.test");

        await AccountDeletion.DeleteAsync(Users, Scoped, target, Audit, admin, TestHttp.Anonymous());

        using var after = db.NewContext();
        var row = after.AuditEvents.Single(a => a.Event == AuditEventCodes.AdminUserDeleted);

        using var doc = JsonDocument.Parse(row.DetailJson!);
        var propertyNames = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(["projectCount", "reportCount"], propertyNames);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            Assert.Equal(JsonValueKind.Number, prop.Value.ValueKind);
        }
    }

    // Silme transaction'ının hangi adımı patlarsa patlasın (burada: audit
    // yazımı) sonuç aynı olmalı: HİÇBİR ŞEY kalıcı olmaz. Kritik yol
    // (WriteCriticalAsync) hatayı yutmaz, yukarı fırlatır — sarmalayan
    // transaction bunu görüp geri sarar.
    private sealed class ThrowOnSecondSave : SaveChangesInterceptor
    {
        private int calls;

        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData, InterceptionResult<int> result)
        {
            if (++calls == 2) throw new InvalidOperationException("simüle edilmiş audit yazım hatası");
            return result;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (++calls == 2) throw new InvalidOperationException("simüle edilmiş audit yazım hatası");
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task audit_yazimi_patlarsa_silme_de_geri_alinir()
    {
        var admin = db.AddUser("yonetici3@ornek.test");
        var target = db.AddUser("silinecek3@ornek.test");

        // Bu test kendi servis sağlayıcısını kurar: kırık bağlam yalnız BU
        // testin UserManager'ına ve AuditLog'una bağlanmalı, sınıfın paylaşılan
        // `Scoped` bağlamına DEĞİL — yoksa diğer testler de aynı hatayı görür.
        var brokenCtx = db.NewContext(new ThrowOnSecondSave());
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddScoped(_ => brokenCtx);
        services.AddIdentityCore<ApplicationUser>(opt =>
        {
            opt.SignIn.RequireConfirmedEmail = true;
            opt.User.RequireUniqueEmail = true;
        })
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<AppDbContext>()
            .AddDefaultTokenProviders();
        services.AddScoped<AuditLog>();
        using var brokenProvider = services.BuildServiceProvider();
        using var brokenScope = brokenProvider.CreateScope();

        var brokenUsers = brokenScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var brokenAudit = brokenScope.ServiceProvider.GetRequiredService<AuditLog>();
        var trackedTarget = await brokenUsers.FindByIdAsync(target.Id);
        Assert.NotNull(trackedTarget);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AccountDeletion.DeleteAsync(
                brokenUsers, brokenCtx, trackedTarget!, brokenAudit, admin, TestHttp.Anonymous()));

        using var after = db.NewContext();
        Assert.NotEmpty(after.Users.Where(u => u.Id == target.Id));
        Assert.Empty(after.AuditEvents.Where(a => a.TargetUserId == target.Id));
    }

    // ---- auth.lockout: best-effort, yalnız eşik geçişinde ----

    [Fact]
    public async Task kilitlenme_esiginde_auth_lockout_izi_dusuyor()
    {
        var user = await NewUserAsync("kilitlenecek@ornek.test");
        var req = new LoginRequest(user.Email!, "Yanlis-Parola-1!");
        var threshold = Users.Options.Lockout.MaxFailedAccessAttempts;

        IResult? last = null;
        for (var i = 0; i < threshold; i++)
        {
            last = await AuthEndpoints.Login(req, Users, Tokens, Scoped, Audit, TestHttp.Anonymous(), new FakeEnv());
        }

        Assert.Equal(StatusCodes.Status401Unauthorized, ResultAssert.Status(last!));

        using var after = db.NewContext();
        var row = Assert.Single(after.AuditEvents.Where(a => a.Event == AuditEventCodes.AuthLockout));
        Assert.Null(row.ActorUserId);
        Assert.Equal(user.Id, row.TargetUserId);

        using var doc = JsonDocument.Parse(row.DetailJson!);
        Assert.Equal(threshold, doc.RootElement.GetProperty("failedCount").GetInt32());
    }

    // Eşiğin BİR ALTINDA hiç iz düşmemeli — her başarısız deneme değil,
    // yalnız kilitlemeyi tetikleyen geçiş kaydedilir (brif §3: "yazılmayanlar").
    [Fact]
    public async Task esik_altindaki_denemeler_iz_birakmaz()
    {
        var user = await NewUserAsync("kilitlenmeyecek@ornek.test");
        var req = new LoginRequest(user.Email!, "Yanlis-Parola-1!");
        var threshold = Users.Options.Lockout.MaxFailedAccessAttempts;

        for (var i = 0; i < threshold - 1; i++)
        {
            await AuthEndpoints.Login(req, Users, Tokens, Scoped, Audit, TestHttp.Anonymous(), new FakeEnv());
        }

        using var after = db.NewContext();
        Assert.Empty(after.AuditEvents.Where(a => a.Event == AuditEventCodes.AuthLockout));
    }

    // Kilitliyken gelen SONRAKİ denemeler erken dönüşe takılır (parola hiç
    // değerlendirilmez) — eşik geçişi yalnız BİR kez, tekrar tekrar değil.
    [Fact]
    public async Task kilitliyken_sonraki_denemeler_ikinci_bir_iz_birakmaz()
    {
        var user = await NewUserAsync("tekrar-kilitli@ornek.test");
        var req = new LoginRequest(user.Email!, "Yanlis-Parola-1!");
        var threshold = Users.Options.Lockout.MaxFailedAccessAttempts;

        for (var i = 0; i < threshold + 2; i++)
        {
            await AuthEndpoints.Login(req, Users, Tokens, Scoped, Audit, TestHttp.Anonymous(), new FakeEnv());
        }

        using var after = db.NewContext();
        Assert.Single(after.AuditEvents.Where(a => a.Event == AuditEventCodes.AuthLockout));
    }

    // ---- kendine ait olaylar: account.registered, account.email-confirmed,
    //      auth.password-changed, auth.password-reset ----

    [Fact]
    public async Task kayit_account_registered_izini_yazar()
    {
        var result = await AuthEndpoints.Register(
            new RegisterRequest("yeni@ornek.test", Password, "Yeni Kişi"),
            Users, Mail, Audit, ConfigWithAdmins());

        Assert.Equal(StatusCodes.Status201Created, ResultAssert.Status(result));

        using var after = db.NewContext();
        var row = Assert.Single(after.AuditEvents.Where(a => a.Event == AuditEventCodes.AccountRegistered));
        Assert.Equal("yeni@ornek.test", row.ActorEmail);
        Assert.Null(row.TargetUserId);
    }

    [Fact]
    public async Task eposta_dogrulama_account_email_confirmed_izini_yazar()
    {
        var user = await NewUserAsync("dogrulanacak@ornek.test");
        var token = await Users.GenerateEmailConfirmationTokenAsync(user);

        var result = await AuthEndpoints.ConfirmEmail(user.Id, token, Users, Audit);
        Assert.Equal(StatusCodes.Status200OK, ResultAssert.Status(result));

        using var after = db.NewContext();
        var row = Assert.Single(after.AuditEvents.Where(a => a.Event == AuditEventCodes.AccountEmailConfirmed));
        Assert.Equal(user.Id, row.ActorUserId);
        Assert.Null(row.TargetUserId);
    }

    [Fact]
    public async Task parola_degistirme_auth_password_changed_izini_yazar()
    {
        var user = await NewUserAsync("parolasini-degistirecek@ornek.test");

        var result = await AuthEndpoints.ChangePassword(
            new ChangePasswordRequest(Password, "Yeni-Parola-999!"),
            Users, Tokens, Scoped, Audit, TestHttp.For(user), new FakeEnv());

        Assert.Equal(StatusCodes.Status200OK, ResultAssert.Status(result));

        using var after = db.NewContext();
        var row = Assert.Single(after.AuditEvents.Where(a => a.Event == AuditEventCodes.AuthPasswordChanged));
        Assert.Equal(user.Id, row.ActorUserId);
        Assert.Null(row.TargetUserId);
    }

    [Fact]
    public async Task parola_sifirlama_auth_password_reset_izini_yazar()
    {
        var user = await NewUserAsync("parolasini-sifirlayacak@ornek.test");
        var token = await Users.GeneratePasswordResetTokenAsync(user);

        var result = await AuthEndpoints.ResetPassword(
            new ResetPasswordRequest(user.Email!, token, "Yeni-Parola-999!"), Users, Scoped, Audit);

        Assert.Equal(StatusCodes.Status200OK, ResultAssert.Status(result));

        using var after = db.NewContext();
        var row = Assert.Single(after.AuditEvents.Where(a => a.Event == AuditEventCodes.AuthPasswordReset));
        Assert.Equal(user.Id, row.ActorUserId);
        Assert.Null(row.TargetUserId);
    }

    // ---- sistem (seed) olayları: admin.role-granted, admin.role-revoked ----

    [Fact]
    public async Task senkronizasyon_yetki_verince_admin_role_granted_izini_yazar()
    {
        var user = await NewUserAsync("yeni-yonetici@ornek.test");

        await AdminSeeder.SyncAsync(scope.ServiceProvider, ConfigWithAdmins(user.Email!), NullLogger.Instance);

        using var after = db.NewContext();
        var row = Assert.Single(after.AuditEvents.Where(a => a.Event == AuditEventCodes.AdminRoleGranted));
        Assert.Null(row.ActorUserId);
        Assert.Equal(user.Id, row.TargetUserId);
    }

    [Fact]
    public async Task senkronizasyon_yetki_alinca_admin_role_revoked_izini_yazar()
    {
        var user = await NewUserAsync("eski-yonetici@ornek.test");
        // Önce yetki verilir (listede), sonra listeden çıkarılıp tekrar
        // koşulur — yalnız GEÇİŞ anında iz düşer.
        await AdminSeeder.SyncAsync(scope.ServiceProvider, ConfigWithAdmins(user.Email!), NullLogger.Instance);

        await AdminSeeder.SyncAsync(scope.ServiceProvider, ConfigWithAdmins(), NullLogger.Instance);

        using var after = db.NewContext();
        var row = Assert.Single(after.AuditEvents.Where(a => a.Event == AuditEventCodes.AdminRoleRevoked));
        Assert.Null(row.ActorUserId);
        Assert.Equal(user.Id, row.TargetUserId);
    }

    // Kayıt anında listedeki bir e-postayla açılan hesap İKİ iz bırakır:
    // kendi kaydı VE sistemin verdiği yetki — ikisi ayrı olgu.
    [Fact]
    public async Task kayit_aninda_yetki_verilirse_admin_role_granted_izini_de_yazar()
    {
        // Rol normalde açılıştaki AdminSeeder.SyncAsync ile kurulur; burada
        // Register'ı tek başına sınadığımız için elle kuruyoruz.
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        await roles.CreateAsync(new IdentityRole(AdminRole.Name));

        var result = await AuthEndpoints.Register(
            new RegisterRequest("baslangicta-yonetici@ornek.test", Password, "Baştan Yönetici"),
            Users, Mail, Audit, ConfigWithAdmins("baslangicta-yonetici@ornek.test"));

        Assert.Equal(StatusCodes.Status201Created, ResultAssert.Status(result));

        using var after = db.NewContext();
        Assert.Single(after.AuditEvents.Where(a => a.Event == AuditEventCodes.AccountRegistered));
        var granted = Assert.Single(after.AuditEvents.Where(a => a.Event == AuditEventCodes.AdminRoleGranted));
        Assert.Equal("baslangicta-yonetici@ornek.test", granted.TargetEmail);
    }

    // ---- GET /api/admin/audit ----

    [Fact]
    public async Task rolsuz_kullanici_gunlugu_goremez()
    {
        var caller = await NewUserAsync("siradan-audit@ornek.test");

        var result = await AdminEndpoints.ListAudit(Users, Scoped, TestHttp.For(caller));

        Assert.Equal(StatusCodes.Status403Forbidden, ResultAssert.Status(result));
    }

    [Fact]
    public async Task gunluk_en_yeni_olay_ustte_siralanir()
    {
        var admin = await NewAdminAsync();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        SeedAudit(AuditEventCodes.AccountRegistered, "once@ornek.test", null, t0);
        SeedAudit(AuditEventCodes.AccountRegistered, "sonra@ornek.test", null, t0.AddMinutes(5));

        var page = ResultAssert.Value<AuditPage>(await AdminEndpoints.ListAudit(Users, Scoped, TestHttp.For(admin)));

        Assert.Equal("sonra@ornek.test", page.Items[0].ActorEmail);
        Assert.Equal("once@ornek.test", page.Items[1].ActorEmail);
    }

    [Fact]
    public async Task olay_turune_gore_suzer()
    {
        var admin = await NewAdminAsync();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        SeedAudit(AuditEventCodes.AccountRegistered, "a@ornek.test", null, t0);
        SeedAudit(AuditEventCodes.AuthLockout, null, "b@ornek.test", t0.AddMinutes(1));

        var page = ResultAssert.Value<AuditPage>(await AdminEndpoints.ListAudit(
            Users, Scoped, TestHttp.For(admin), @event: AuditEventCodes.AuthLockout));

        var row = Assert.Single(page.Items);
        Assert.Equal(AuditEventCodes.AuthLockout, row.Event);
    }

    // ListUsers'taki aramayla AYNI iğne: Türkçe katlama + büyük/küçük harf
    // duyarsız, hem aktör hem hedef e-postasında arar.
    [Fact]
    public async Task q_aktor_ve_hedef_epostada_turkce_katlamayla_arar()
    {
        var admin = await NewAdminAsync();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        SeedAudit(AuditEventCodes.AdminUserDeleted, "yonetici@ornek.test", "silinen@ornek.test", t0);
        SeedAudit(AuditEventCodes.AccountRegistered, "baska@ornek.test", null, t0.AddMinutes(1));

        var page = ResultAssert.Value<AuditPage>(await AdminEndpoints.ListAudit(
            Users, Scoped, TestHttp.For(admin), q: "SİLİNEN"));

        var row = Assert.Single(page.Items);
        Assert.Equal("silinen@ornek.test", row.TargetEmail);
    }

    [Fact]
    public async Task from_to_tarih_araligina_gore_suzer()
    {
        var admin = await NewAdminAsync();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        SeedAudit(AuditEventCodes.AccountRegistered, "erken@ornek.test", null, t0);
        SeedAudit(AuditEventCodes.AccountRegistered, "aralikta@ornek.test", null, t0.AddDays(2));
        SeedAudit(AuditEventCodes.AccountRegistered, "gec@ornek.test", null, t0.AddDays(10));

        var page = ResultAssert.Value<AuditPage>(await AdminEndpoints.ListAudit(
            Users, Scoped, TestHttp.For(admin), from: t0.AddDays(1), to: t0.AddDays(5)));

        var row = Assert.Single(page.Items);
        Assert.Equal("aralikta@ornek.test", row.ActorEmail);
    }

    [Fact]
    public async Task sayfalama_toplami_ve_sayfa_boyutunu_bildirir()
    {
        var admin = await NewAdminAsync();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 3; i++)
        {
            SeedAudit(AuditEventCodes.AccountRegistered, $"kullanici{i}@ornek.test", null, t0.AddMinutes(i));
        }

        var page = ResultAssert.Value<AuditPage>(await AdminEndpoints.ListAudit(
            Users, Scoped, TestHttp.For(admin), page: 1, pageSize: 2));

        Assert.Equal(3, page.Total);
        Assert.Equal(2, page.Items.Count);
    }

    // Sınır SUNUCUDA: ListUsers'la aynı gerekçe, `pageSize=100000` bütün
    // tabloyu tek yanıtta dışarı vermesin.
    [Fact]
    public async Task asiri_sayfa_boyutu_varsayilana_duser()
    {
        var admin = await NewAdminAsync();

        var page = ResultAssert.Value<AuditPage>(await AdminEndpoints.ListAudit(
            Users, Scoped, TestHttp.For(admin), pageSize: 100_000));

        Assert.Equal(25, page.PageSize);
    }

    public void Dispose()
    {
        scope.Dispose();
        provider.Dispose();
        db.Dispose();
        GC.SuppressFinalize(this);
    }
}

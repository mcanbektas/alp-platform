using System.Text.Json;
using Alp.Api.Auth;
using Alp.Api.Common;
using Alp.Data;
using Alp.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Alp.Api.Tests;

// Yönetim paneli: kullanıcı listesi ve hesap silme.
//
// Silme iddiasının asıl konusu "uç 204 döndü" değil, GERİDE HİÇBİR SATIR
// KALMADIĞI. Silme veritabanı cascade'ine bırakılmıyor çünkü iki yerde
// tutmuyor: `ThicknessRecords`in foreign key'i hiç yok (kullanıcı gidince satır
// kalır), `ReportSnapshotSections → SectionBlobs` bağı ise `Restrict` ve bugün
// yalnız FK'ların oluşturulma sırası sayesinde çalışıyor. İkisi de derlemeden
// ve öteki testlerden kaçar; tek yakalayan şey tablo tablo saymaktır.
//
// İkinci konu YETKİ: bu uçların kapısı `RequireAdmin`dir ve rol her istekte
// veritabanından okunur. Kapı açık kalırsa sıradan bir oturum bütün kullanıcı
// tablosunu okuyup silebilir.
public class AdminEndpointsTests : IDisposable
{
    private readonly TestDb db = new();
    private readonly ServiceProvider provider;
    private readonly IServiceScope scope;

    // AuthEmailLanguageTests ile aynı kurulum: parola özeti ve doğrulaması
    // gerçek UserManager'dan gelmeli, elle kurulan bir örnek veremez.
    // Farkı `AddRoles` — rol deposu olmadan `IsInRoleAsync` çözülemez.
    public AdminEndpointsTests()
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

    private RoleManager<IdentityRole> Roles =>
        scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();

    private AuditLog Audit => scope.ServiceProvider.GetRequiredService<AuditLog>();

    // Uç, `AppDbContext`i de UserManager'ı da AYNI istek kapsamından alır, yani
    // üretimde ikisi tek örnektir. Testte ayrı bir bağlam verilirse işlem bir
    // bağlantıda, silme ötekinde açılır ve SQLite kilitlenir (ölçüldü: test
    // 30 sn sonra DbUpdateException ile düşüyordu). Kapsamlı örnek kullanılır.
    private AppDbContext Scoped => scope.ServiceProvider.GetRequiredService<AppDbContext>();

    private const string Password = "Parola-12345!";
    private const string AdminPassword = "Yonetici-12345!";

    // Parolası olan gerçek kullanıcı — `TestDb.AddUser` özet yazmaz, bu uçta
    // parola doğrulaması sınandığı için kullanıcı UserManager'dan doğar.
    private async Task<ApplicationUser> NewUserAsync(
        string email = "silinecek@ornek.test", string password = Password)
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

    private async Task<ApplicationUser> NewAdminAsync(string email = "yonetici@ornek.test")
    {
        var admin = await NewUserAsync(email, AdminPassword);
        if (!await Roles.RoleExistsAsync(AdminRole.Name))
        {
            await Roles.CreateAsync(new IdentityRole(AdminRole.Name));
        }
        await Users.AddToRoleAsync(admin, AdminRole.Name);
        return admin;
    }

    // Kullanıcıya bağlı HER tablodan birer satır. Silme sonrası hepsi
    // sıfırlanmalı; biri kalırsa geride kişisel veri kalmış demektir.
    private void SeedEverything(ApplicationUser user)
    {
        using var seed = db.NewContext();
        var project = new Project
        {
            Id = Guid.NewGuid(), UserId = user.Id, Name = "Proje",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        seed.Projects.Add(project);
        seed.Calculations.Add(new Calculation
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, ToolKey = "trace-width",
            SortOrder = 0, EngineVersion = "test", SchemaVersion = 1,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        var report = new Report
        {
            Id = Guid.NewGuid(), UserId = user.Id, ProjectId = project.Id,
            Title = "Rapor", PreparedBy = "Ad", Company = "Firma",
            Format = ReportFormat.Pdf, Revision = 1, SchemaVersion = 1,
            FileSize = 10, GeneratedAt = DateTimeOffset.UtcNow,
        };
        seed.Reports.Add(report);
        seed.SectionBlobs.Add(new SectionBlob
        {
            UserId = user.Id, Hash = "hash-1", Content = "{}", Length = 2,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        seed.ReportSnapshotSections.Add(new ReportSnapshotSection
        {
            ReportId = report.Id, UserId = user.Id,
            Hash = "hash-1", SortOrder = 0,
        });

        seed.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(), UserId = user.Id, // Özet KÜRESEL olarak benzersiz (ürün kuralı), kullanıcı başına ayrışmalı.
            TokenHash = $"ozet-{user.Id}",
            CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            CreatedByIp = "127.0.0.1", Persistent = true,
        });
        seed.ThicknessRecords.Add(new ThicknessRecord
        {
            Id = Guid.NewGuid(), UserId = user.Id, Name = "Kayıt",
            NameKey = "kayit", DataJson = "{}", SchemaVersion = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        seed.SaveChanges();
    }

    private Task<IResult> Delete(
        ApplicationUser caller, string targetId, string password) =>
        AdminEndpoints.DeleteUser(
            targetId, new AdminDeleteUserRequest(password), Users, Scoped, Audit, TestHttp.For(caller));

    private Task<IResult> ChangePlan(
        ApplicationUser caller, string targetId, string plan) =>
        AdminEndpoints.ChangePlan(
            targetId, new AdminChangePlanRequest(plan), Users, Audit, TestHttp.For(caller));

    // ---- Silme ----

    [Fact]
    public async Task yonetici_dogru_parolayla_hesabi_ve_bagli_her_kaydi_siler()
    {
        var admin = await NewAdminAsync();
        var user = await NewUserAsync();
        SeedEverything(user);

        var result = await Delete(admin, user.Id, AdminPassword);
        Assert.Equal(StatusCodes.Status204NoContent, ResultAssert.Status(result));

        using var after = db.NewContext();
        Assert.Empty(after.Users.Where(u => u.Id == user.Id));
        Assert.Empty(after.Projects.Where(p => p.UserId == user.Id));
        Assert.Empty(after.Calculations.Where(c => c.Project!.UserId == user.Id));
        Assert.Empty(after.Reports.Where(r => r.UserId == user.Id));
        Assert.Empty(after.SectionBlobs.Where(b => b.UserId == user.Id));
        Assert.Empty(after.ReportSnapshotSections.Where(s => s.UserId == user.Id));
        Assert.Empty(after.RefreshTokens.Where(t => t.UserId == user.Id));
        // FK'si olmayan tek tablo: cascade bunu ASLA silmez, elle silinmezse kalır.
        Assert.Empty(after.ThicknessRecords.Where(r => r.UserId == user.Id));
    }

    // Parola İSTEYENİN parolasıdır, hedefinki değil. Yönetici hedefin
    // parolasını bilmez; sınanan şey yöneticinin kendi kimliğidir.
    [Fact]
    public async Task yoneticinin_yanlis_parolasi_hesabi_silmez()
    {
        var admin = await NewAdminAsync();
        var user = await NewUserAsync();
        SeedEverything(user);

        var result = await Delete(admin, user.Id, "Yanlis-Parola-1!");

        // 400, 401 DEĞİL: 401 istemcide oturum düştü sanılıp sessiz yenilemeye
        // ve çıkışa yol açardı. Oturum geçerli, yanlış olan parola.
        Assert.Equal(StatusCodes.Status400BadRequest, ResultAssert.Status(result));
        Assert.Equal("INVALID_CREDENTIALS", ResultAssert.Value<ApiError>(result).Error);

        using var after = db.NewContext();
        Assert.NotEmpty(after.Users.Where(u => u.Id == user.Id));
        Assert.NotEmpty(after.Projects.Where(p => p.UserId == user.Id));
        Assert.NotEmpty(after.ThicknessRecords.Where(r => r.UserId == user.Id));
    }

    // Hedefin kendi parolası da işe yaramamalı: uç yalnız çağıranın parolasını
    // doğrular, hedefinkini bilmesi gerekmez ve kabul de etmez.
    [Fact]
    public async Task hedefin_parolasi_kabul_edilmez()
    {
        var admin = await NewAdminAsync();
        var user = await NewUserAsync();

        var result = await Delete(admin, user.Id, Password);

        Assert.Equal(StatusCodes.Status400BadRequest, ResultAssert.Status(result));
        Assert.Equal("INVALID_CREDENTIALS", ResultAssert.Value<ApiError>(result).Error);
        using var after = db.NewContext();
        Assert.NotEmpty(after.Users.Where(u => u.Id == user.Id));
    }

    [Fact]
    public async Task bos_parola_reddedilir()
    {
        var admin = await NewAdminAsync();
        var user = await NewUserAsync();

        var result = await Delete(admin, user.Id, "   ");

        Assert.Equal(StatusCodes.Status400BadRequest, ResultAssert.Status(result));
        Assert.Equal("MISSING_FIELDS", ResultAssert.Value<ApiError>(result).Error);
        using var after = db.NewContext();
        Assert.NotEmpty(after.Users.Where(u => u.Id == user.Id));
    }

    // ---- Yetki ----

    [Fact]
    public async Task rolsuz_kullanici_silemez()
    {
        var caller = await NewUserAsync("siradan@ornek.test");
        var target = await NewUserAsync();
        SeedEverything(target);

        var result = await Delete(caller, target.Id, Password);

        // 403, 401 DEĞİL: oturum geçerli, eksik olan yetki. 401 istemcide
        // sessiz yenilemeyi tetikler ve kullanıcıyı çıkışa atardı.
        Assert.Equal(StatusCodes.Status403Forbidden, ResultAssert.Status(result));
        Assert.Equal("FORBIDDEN", ResultAssert.Value<ApiError>(result).Error);
        using var after = db.NewContext();
        Assert.NotEmpty(after.Users.Where(u => u.Id == target.Id));
    }

    [Fact]
    public async Task rolsuz_kullanici_listeyi_goremez()
    {
        var caller = await NewUserAsync("siradan@ornek.test");

        var result = await AdminEndpoints.ListUsers(Users, Scoped, TestHttp.For(caller));

        Assert.Equal(StatusCodes.Status403Forbidden, ResultAssert.Status(result));
        Assert.Equal("FORBIDDEN", ResultAssert.Value<ApiError>(result).Error);
    }

    [Fact]
    public async Task kimliksiz_istek_401_doner()
    {
        var target = await NewUserAsync();

        var result = await AdminEndpoints.DeleteUser(
            target.Id, new AdminDeleteUserRequest(Password), Users, Scoped, Audit, TestHttp.Anonymous());

        Assert.Equal(StatusCodes.Status401Unauthorized, ResultAssert.Status(result));
        Assert.Equal("UNAUTHORIZED", ResultAssert.Value<ApiError>(result).Error);
    }

    // Yönetici kendi hesabını silemez: panel sahipsiz kalır ve o an açık olan
    // oturum ortasından kopar.
    [Fact]
    public async Task yonetici_kendini_silemez()
    {
        var admin = await NewAdminAsync();

        var result = await Delete(admin, admin.Id, AdminPassword);

        Assert.Equal(StatusCodes.Status400BadRequest, ResultAssert.Status(result));
        Assert.Equal("SELF_DELETE", ResultAssert.Value<ApiError>(result).Error);
        using var after = db.NewContext();
        Assert.NotEmpty(after.Users.Where(u => u.Id == admin.Id));
    }

    // Yönetici yöneticiyi de silemez. Yetkinin kaynağı yapılandırmadır; bir
    // yöneticiyi silmek için önce `App:AdminEmails` listesinden çıkarılır.
    [Fact]
    public async Task yonetici_baska_yoneticiyi_silemez()
    {
        var admin = await NewAdminAsync();
        var other = await NewAdminAsync("ikinci@ornek.test");

        var result = await Delete(admin, other.Id, AdminPassword);

        Assert.Equal(StatusCodes.Status400BadRequest, ResultAssert.Status(result));
        Assert.Equal("ADMIN_TARGET", ResultAssert.Value<ApiError>(result).Error);
        using var after = db.NewContext();
        Assert.NotEmpty(after.Users.Where(u => u.Id == other.Id));
    }

    [Fact]
    public async Task olmayan_hesap_404_doner()
    {
        var admin = await NewAdminAsync();

        var result = await Delete(admin, Guid.NewGuid().ToString(), AdminPassword);

        Assert.Equal(StatusCodes.Status404NotFound, ResultAssert.Status(result));
        Assert.Equal("NOT_FOUND", ResultAssert.Value<ApiError>(result).Error);
    }

    // Silme yalnız hedefin kaydını kapsamalı. Aynı blob HASH'i iki kullanıcıda
    // da bulunabilir (içerik adresli depo, anahtar `(UserId, Hash)`) ve birinin
    // silinmesi ötekinin satırını götürmemeli.
    [Fact]
    public async Task baska_kullanicinin_verisine_dokunmaz()
    {
        var admin = await NewAdminAsync();
        var user = await NewUserAsync();
        var other = await NewUserAsync("kalan@ornek.test");
        SeedEverything(user);
        SeedEverything(other);

        await Delete(admin, user.Id, AdminPassword);

        using var after = db.NewContext();
        Assert.NotEmpty(after.Users.Where(u => u.Id == other.Id));
        Assert.NotEmpty(after.Projects.Where(p => p.UserId == other.Id));
        Assert.NotEmpty(after.Reports.Where(r => r.UserId == other.Id));
        Assert.NotEmpty(after.SectionBlobs.Where(b => b.UserId == other.Id));
        Assert.NotEmpty(after.ReportSnapshotSections.Where(s => s.UserId == other.Id));
        Assert.NotEmpty(after.RefreshTokens.Where(t => t.UserId == other.Id));
        Assert.NotEmpty(after.ThicknessRecords.Where(r => r.UserId == other.Id));
    }

    // Silinen kullanıcının e-postası yeniden kayda AÇIK kalmalı: benzersizlik
    // indeksinde ölü bir satır kalsaydı kullanıcı aynı adresle geri dönemezdi.
    [Fact]
    public async Task ayni_eposta_ile_yeniden_kayit_acilabilir()
    {
        var admin = await NewAdminAsync();
        var user = await NewUserAsync();
        await Delete(admin, user.Id, AdminPassword);

        var again = await NewUserAsync();
        Assert.NotEqual(user.Id, again.Id);
    }

    // ---- Plan ----

    [Fact]
    public async Task yonetici_plani_free_dan_pro_ya_degistirir()
    {
        var admin = await NewAdminAsync();
        var user = await NewUserAsync();
        Assert.Equal("free", user.Plan);

        var result = await ChangePlan(admin, user.Id, "pro");
        Assert.Equal(StatusCodes.Status204NoContent, ResultAssert.Status(result));

        using var after = db.NewContext();
        Assert.Equal("pro", after.Users.Single(u => u.Id == user.Id).Plan);

        var row = Assert.Single(after.AuditEvents.Where(a => a.Event == AuditEventCodes.AdminPlanChanged));
        Assert.Equal(admin.Id, row.ActorUserId);
        Assert.Equal(user.Id, row.TargetUserId);
        using var doc = JsonDocument.Parse(row.DetailJson!);
        Assert.Equal("free", doc.RootElement.GetProperty("fromPlan").GetString());
        Assert.Equal("pro", doc.RootElement.GetProperty("toPlan").GetString());
    }

    [Fact]
    public async Task yonetici_plani_pro_dan_free_e_degistirir()
    {
        var admin = await NewAdminAsync();
        var user = await NewUserAsync();
        await ChangePlan(admin, user.Id, "pro");

        var result = await ChangePlan(admin, user.Id, "free");
        Assert.Equal(StatusCodes.Status204NoContent, ResultAssert.Status(result));

        using var after = db.NewContext();
        Assert.Equal("free", after.Users.Single(u => u.Id == user.Id).Plan);
    }

    // Değer zaten aynıysa uç yine 204 döner ama iz YAZILMAZ: hiçbir şey
    // değişmedi, iz bırakmak gürültüden başka bir şey olmazdı.
    [Fact]
    public async Task ayni_plan_degeri_iz_birakmaz()
    {
        var admin = await NewAdminAsync();
        var user = await NewUserAsync();

        var result = await ChangePlan(admin, user.Id, "free");
        Assert.Equal(StatusCodes.Status204NoContent, ResultAssert.Status(result));

        using var after = db.NewContext();
        Assert.Empty(after.AuditEvents.Where(a => a.Event == AuditEventCodes.AdminPlanChanged));
    }

    [Fact]
    public async Task gecersiz_plan_degeri_reddedilir()
    {
        var admin = await NewAdminAsync();
        var user = await NewUserAsync();

        var result = await ChangePlan(admin, user.Id, "enterprise");

        Assert.Equal(StatusCodes.Status400BadRequest, ResultAssert.Status(result));
        Assert.Equal("INVALID_PLAN", ResultAssert.Value<ApiError>(result).Error);
        using var after = db.NewContext();
        Assert.Equal("free", after.Users.Single(u => u.Id == user.Id).Plan);
    }

    [Fact]
    public async Task olmayan_hesabin_plani_404_doner()
    {
        var admin = await NewAdminAsync();

        var result = await ChangePlan(admin, Guid.NewGuid().ToString(), "pro");

        Assert.Equal(StatusCodes.Status404NotFound, ResultAssert.Status(result));
        Assert.Equal("NOT_FOUND", ResultAssert.Value<ApiError>(result).Error);
    }

    [Fact]
    public async Task rolsuz_kullanici_plan_degistiremez()
    {
        var caller = await NewUserAsync("siradan-plan@ornek.test");
        var target = await NewUserAsync("hedef-plan@ornek.test");

        var result = await ChangePlan(caller, target.Id, "pro");

        Assert.Equal(StatusCodes.Status403Forbidden, ResultAssert.Status(result));
        using var after = db.NewContext();
        Assert.Equal("free", after.Users.Single(u => u.Id == target.Id).Plan);
    }

    [Fact]
    public async Task kimliksiz_istek_plan_degistiremez()
    {
        var target = await NewUserAsync();

        var result = await AdminEndpoints.ChangePlan(
            target.Id, new AdminChangePlanRequest("pro"), Users, Audit, TestHttp.Anonymous());

        Assert.Equal(StatusCodes.Status401Unauthorized, ResultAssert.Status(result));
    }

    // Silmedeki self-delete/admin-target kısıtları BURADA yok — farklı risk
    // sınıfı: plan değişimi geri alınabilir, admin kendi planını da değiştirebilir.
    [Fact]
    public async Task yonetici_kendi_planini_degistirebilir()
    {
        var admin = await NewAdminAsync();

        var result = await ChangePlan(admin, admin.Id, "pro");

        Assert.Equal(StatusCodes.Status204NoContent, ResultAssert.Status(result));
        using var after = db.NewContext();
        Assert.Equal("pro", after.Users.Single(u => u.Id == admin.Id).Plan);
    }

    [Fact]
    public async Task yonetici_baska_yoneticinin_planini_degistirebilir()
    {
        var admin = await NewAdminAsync();
        var other = await NewAdminAsync("ikinci-plan@ornek.test");

        var result = await ChangePlan(admin, other.Id, "pro");

        Assert.Equal(StatusCodes.Status204NoContent, ResultAssert.Status(result));
        using var after = db.NewContext();
        Assert.Equal("pro", after.Users.Single(u => u.Id == other.Id).Plan);
    }

    // Parola İSTENMEZ (DeleteUser'ın aksine): `AdminChangePlanRequest` hiç
    // parola alanı taşımaz, bu yüzden yanlış/boş parola senaryosu YOK — uç
    // yalnız admin oturumuna bakar.

    // ---- Liste ----

    [Fact]
    public async Task liste_sayfalanir_ve_toplami_bildirir()
    {
        var admin = await NewAdminAsync();
        for (var i = 0; i < 3; i++) await NewUserAsync($"kullanici{i}@ornek.test");

        var result = await AdminEndpoints.ListUsers(Users, Scoped, TestHttp.For(admin), page: 1, pageSize: 2);

        var page = ResultAssert.Value<AdminUserPage>(result);
        Assert.Equal(4, page.Total);          // 3 kullanıcı + yönetici
        Assert.Equal(2, page.Items.Count);
        Assert.Equal(1, page.Page);
        Assert.Equal(2, page.PageSize);
    }

    // Sayfa boyutunun sınırı SUNUCUDA: `pageSize=100000` bütün tabloyu tek
    // yanıtta dışarı verirdi.
    [Fact]
    public async Task asiri_sayfa_boyutu_varsayilana_duser()
    {
        var admin = await NewAdminAsync();

        var result = await AdminEndpoints.ListUsers(Users, Scoped, TestHttp.For(admin), pageSize: 100_000);

        Assert.Equal(25, ResultAssert.Value<AdminUserPage>(result).PageSize);
    }

    [Fact]
    public async Task arama_eposta_ve_adda_calisir()
    {
        var admin = await NewAdminAsync();
        await NewUserAsync("ahmet@ornek.test");
        await NewUserAsync("mehmet@baska.test");

        var byEmail = ResultAssert.Value<AdminUserPage>(
            await AdminEndpoints.ListUsers(Users, Scoped, TestHttp.For(admin), q: "baska"));
        Assert.Single(byEmail.Items);
        Assert.Equal("mehmet@baska.test", byEmail.Items[0].Email);

        // Görünen ad testte e-postayla aynı; arama iki alanda da çalışmalı.
        var byName = ResultAssert.Value<AdminUserPage>(
            await AdminEndpoints.ListUsers(Users, Scoped, TestHttp.For(admin), q: "AHMET"));
        Assert.Single(byName.Items);
    }

    // Türkçe yazım aramayı düşürmemeli. Kayıtlar çoğunlukla ASCII
    // ("Yonetici") ama arayan doğal yazımı yazar ("yönetici", "YÖNETİCİ").
    // Katlama yokken bu aramalar sıfır dönüyordu ve kullanıcıya "arama
    // çalışmıyor" olarak görünüyordu. 'İ' ayrıca özel: invariant küçültme onu
    // "i + birleşen nokta" yapar, eşleme küçültmeden önce yapılmazsa büyük
    // harfli Türkçe arama yine boş döner.
    [Theory]
    [InlineData("yönetici")]
    [InlineData("YÖNETİCİ")]
    [InlineData("Yönetİci")]
    public async Task turkce_yazim_ascii_kaydi_bulur(string aranan)
    {
        var admin = await NewAdminAsync(); // yonetici@ornek.test / "yonetici@ornek.test"

        var page = ResultAssert.Value<AdminUserPage>(
            await AdminEndpoints.ListUsers(Users, Scoped, TestHttp.For(admin), q: aranan));

        Assert.Single(page.Items);
        Assert.Equal("yonetici@ornek.test", page.Items[0].Email);
    }

    // Katlama iğneye uygulanır, kayda değil: aksansız arama aksanlı kaydı
    // bulamaz (bilinçli sınır — sütun tarafı katlaması DB'ye özgü unaccent
    // ister, testlerin SQLite'ında yok). Bu test sınırı BELGELEMEK için var;
    // davranış bilerek değiştirilirse test de bilerek güncellenir.
    [Fact]
    public async Task fold_turkish_yalniz_igneyi_katlar()
    {
        Assert.Equal("cgiosu", AdminEndpoints.FoldTurkish("çğıöşü"));
        Assert.Equal("yonetici", AdminEndpoints.FoldTurkish("yönetici"));
        Assert.Equal("ascii-degismez", AdminEndpoints.FoldTurkish("ascii-degismez"));
    }

    // Liste kimlik alanı taşımaz ve yönetici satırını işaretler — panel silme
    // düğmesini buna bakarak gizler.
    [Fact]
    public async Task liste_yonetici_satirini_isaretler()
    {
        var admin = await NewAdminAsync();
        await NewUserAsync();

        var page = ResultAssert.Value<AdminUserPage>(
            await AdminEndpoints.ListUsers(Users, Scoped, TestHttp.For(admin)));

        Assert.True(page.Items.Single(r => r.Id == admin.Id).IsAdmin);
        Assert.False(page.Items.Single(r => r.Id != admin.Id).IsAdmin);
    }

    // Kilit ham alan olarak taşınır (`LockoutEnd`), türetilmiş bir bool DEĞİL —
    // panel "ne zaman açılacak"ı da göstermek istiyor. Kilitli olmayan hesapta
    // alan null kalmalı: sessizce geçmiş bir tarih basmak "az önce açıldı" gibi
    // yanlış okunurdu.
    [Fact]
    public async Task liste_kilitli_hesabin_lockoutEnd_alanini_tasir()
    {
        var admin = await NewAdminAsync();
        var locked = await NewUserAsync("kilitli@ornek.test");
        var free = await NewUserAsync("kilitsiz@ornek.test");
        var until = DateTimeOffset.UtcNow.AddMinutes(5);
        await Users.SetLockoutEndDateAsync(locked, until);

        var page = ResultAssert.Value<AdminUserPage>(
            await AdminEndpoints.ListUsers(Users, Scoped, TestHttp.For(admin)));

        // SQLite saniyenin altını tam tik hassasiyetiyle taşımıyor — saniyeye
        // yuvarlanmış karşılaştırma, alanın GERÇEKTEN veritabanından geldiğini
        // kanıtlamaya yeter.
        var got = page.Items.Single(r => r.Id == locked.Id).LockoutEnd;
        Assert.NotNull(got);
        Assert.True(
            Math.Abs((until - got!.Value).TotalSeconds) < 1,
            $"beklenen {until:o}, gelen {got:o}");
        Assert.Null(page.Items.Single(r => r.Id == free.Id).LockoutEnd);
    }

    [Fact]
    public async Task liste_proje_ve_rapor_sayisini_verir()
    {
        var admin = await NewAdminAsync();
        var user = await NewUserAsync();
        SeedEverything(user);

        var page = ResultAssert.Value<AdminUserPage>(
            await AdminEndpoints.ListUsers(Users, Scoped, TestHttp.For(admin), q: "silinecek"));

        var row = Assert.Single(page.Items);
        Assert.Equal(1, row.ProjectCount);
        Assert.Equal(1, row.ReportCount);
    }

    public void Dispose()
    {
        scope.Dispose();
        provider.Dispose();
        db.Dispose();
        GC.SuppressFinalize(this);
    }
}

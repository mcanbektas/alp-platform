using Alp.Api.Auth;
using Alp.Api.Common;
using Alp.Data;
using Alp.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Alp.Api.Tests;

// Hesap silme (KVKK m.7 / m.11).
//
// Buradaki asıl iddia "uç 204 döndü" değil, GERİDE HİÇBİR SATIR KALMADIĞI.
// Silme veritabanı cascade'ine bırakılmıyor çünkü iki yerde tutmuyor:
// `ThicknessRecords`in foreign key'i hiç yok (kullanıcı gidince satır kalır),
// `ReportSnapshotSections → SectionBlobs` bağı ise `Restrict` ve bugün yalnız
// FK'ların oluşturulma sırası sayesinde çalışıyor. İkisi de derlemeden ve
// öteki testlerden kaçar; tek yakalayan şey tablo tablo saymaktır.
public class DeleteAccountTests : IDisposable
{
    private readonly TestDb db = new();
    private readonly ServiceProvider provider;
    private readonly IServiceScope scope;

    // AuthEmailLanguageTests ile aynı kurulum: parola özeti ve doğrulaması
    // gerçek UserManager'dan gelmeli, elle kurulan bir örnek veremez.
    public DeleteAccountTests()
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
            .AddEntityFrameworkStores<AppDbContext>()
            .AddDefaultTokenProviders();

        provider = services.BuildServiceProvider();
        scope = provider.CreateScope();
    }

    private UserManager<ApplicationUser> Users =>
        scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

    // Uç, `AppDbContext`i de UserManager'ı da AYNI istek kapsamından alır, yani
    // üretimde ikisi tek örnektir. Testte ayrı bir bağlam verilirse işlem bir
    // bağlantıda, silme ötekinde açılır ve SQLite kilitlenir (ölçüldü: test
    // 30 sn sonra DbUpdateException ile düşüyordu). Kapsamlı örnek kullanılır.
    private AppDbContext Scoped => scope.ServiceProvider.GetRequiredService<AppDbContext>();

    private const string Password = "Parola-12345!";

    // Parolası olan gerçek kullanıcı — `TestDb.AddUser` özet yazmaz, bu uçta
    // parola doğrulaması sınandığı için kullanıcı UserManager'dan doğar.
    private async Task<ApplicationUser> NewUserAsync(string email = "silinecek@ornek.test")
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

    private Task<IResult> Delete(ApplicationUser user, string password, HttpContext? http = null) =>
        AuthEndpoints.DeleteMe(
            new DeleteAccountRequest(password), Users, Scoped, http ?? TestHttp.For(user));

    [Fact]
    public async Task dogru_parolayla_hesap_ve_bagli_her_kayit_silinir()
    {
        var user = await NewUserAsync();
        SeedEverything(user);

        var result = await Delete(user, Password);
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

    [Fact]
    public async Task yenileme_cerezi_dusurulur()
    {
        var user = await NewUserAsync();
        var http = TestHttp.For(user);

        await Delete(user, Password, http);

        // Silme başlığı çerezi geçmişe kurar; tarayıcı bunu silmek olarak okur.
        var setCookie = http.Response.Headers.SetCookie.ToString();
        Assert.Contains("alp_rt=", setCookie);
        Assert.Contains("expires=Thu, 01 Jan 1970", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task yanlis_parola_hesabi_silmez()
    {
        var user = await NewUserAsync();
        SeedEverything(user);

        var result = await Delete(user, "Yanlis-Parola-1!");

        // 400, 401 DEĞİL: 401 istemcide oturum düştü sanılıp sessiz yenilemeye
        // ve çıkışa yol açardı. Oturum geçerli, yanlış olan parola.
        Assert.Equal(StatusCodes.Status400BadRequest, ResultAssert.Status(result));
        Assert.Equal("INVALID_CREDENTIALS", ResultAssert.Value<ApiError>(result).Error);

        using var after = db.NewContext();
        Assert.NotEmpty(after.Users.Where(u => u.Id == user.Id));
        Assert.NotEmpty(after.Projects.Where(p => p.UserId == user.Id));
        Assert.NotEmpty(after.ThicknessRecords.Where(r => r.UserId == user.Id));
    }

    [Fact]
    public async Task bos_parola_reddedilir()
    {
        var user = await NewUserAsync();

        var result = await Delete(user, "   ");

        Assert.Equal(StatusCodes.Status400BadRequest, ResultAssert.Status(result));
        Assert.Equal("MISSING_FIELDS", ResultAssert.Value<ApiError>(result).Error);
        using var after = db.NewContext();
        Assert.NotEmpty(after.Users.Where(u => u.Id == user.Id));
    }

    [Fact]
    public async Task kimliksiz_istek_401_doner()
    {
        var result = await AuthEndpoints.DeleteMe(
            new DeleteAccountRequest(Password), Users, Scoped, TestHttp.Anonymous());

        Assert.Equal(StatusCodes.Status401Unauthorized, ResultAssert.Status(result));
        Assert.Equal("UNAUTHORIZED", ResultAssert.Value<ApiError>(result).Error);
    }

    // Silme yalnız çağıranın kaydını kapsamalı. Aynı blob HASH'i iki
    // kullanıcıda da bulunabilir (içerik adresli depo, anahtar `(UserId, Hash)`)
    // ve bir kullanıcının silinmesi ötekinin satırını götürmemeli.
    [Fact]
    public async Task baska_kullanicinin_verisine_dokunmaz()
    {
        var user = await NewUserAsync();
        var other = await NewUserAsync("kalan@ornek.test");
        SeedEverything(user);
        SeedEverything(other);

        await Delete(user, Password);

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
        var user = await NewUserAsync();
        await Delete(user, Password);

        var again = await NewUserAsync();
        Assert.NotEqual(user.Id, again.Id);
    }

    public void Dispose()
    {
        scope.Dispose();
        provider.Dispose();
        db.Dispose();
        GC.SuppressFinalize(this);
    }
}

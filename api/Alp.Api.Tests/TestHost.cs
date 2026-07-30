using System.Security.Claims;
using Alp.Data;
using Alp.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Alp.Api.Tests;

// Veritabanına dokunan testlerin ortak zemini.
//
// Sağlayıcı SQLite (bellek içi): şema modelden kurulur, yani `(UserId, NameKey)`
// benzersiz dizini de kurulur ve gerçekten zorlanır. `InMemory` sağlayıcısı dizin
// ZORLAMAZ — ad tekliğini onunla sınamak, sınanan kuralı atlamak olurdu.
//
// Veritabanı adlı ve paylaşımlı önbellekli: her bağlam KENDİ bağlantısını açar,
// üretimdeki gibi. `DataSource=:memory:` tek bağlantıya bağlı olurdu ve iki
// bağlamın aynı satırı görmesi gereken yarış senaryosu kurulamazdı. Adı taşıyan
// bir bağlantı test boyunca açık tutulur, yoksa son bağlantı kapanınca veritabanı
// yok olur.
internal sealed class TestDb : IDisposable
{
    private readonly SqliteConnection keeper;
    private readonly string connectionString;
    private readonly List<AppDbContext> contexts = [];

    public TestDb()
    {
        connectionString = $"DataSource=file:alp-{Guid.NewGuid():N}?mode=memory&cache=shared";
        keeper = new SqliteConnection(connectionString);
        keeper.Open();
        Db = NewContext();
        Db.Database.EnsureCreated();
    }

    public AppDbContext Db { get; }

    public AppDbContext NewContext(IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString);
        if (interceptor is not null) options.AddInterceptors(interceptor);
        var ctx = new AppDbContext(options.Options);
        contexts.Add(ctx);
        return ctx;
    }

    // Kullanıcı satırı gerçekten açılır: Project → AspNetUsers yabancı anahtarı
    // var, uydurma bir kimlikle proje eklenemez.
    public ApplicationUser AddUser(string email)
    {
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            DisplayName = email,
            CreatedAt = DateTimeOffset.UtcNow,
            SecurityStamp = Guid.NewGuid().ToString(),
        };
        Db.Users.Add(user);
        Db.SaveChanges();
        return user;
    }

    public Project AddProject(ApplicationUser owner, string name = "Proje")
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            UserId = owner.Id,
            Name = name,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        Db.Projects.Add(project);
        Db.SaveChanges();
        return project;
    }

    public Calculation AddCalculation(Project project, string toolKey = "trace-width", string? reportJson = null)
    {
        var calculation = new Calculation
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            ToolKey = toolKey,
            SortOrder = 0,
            EngineVersion = "test",
            SchemaVersion = 1,
            ReportJson = reportJson,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        Db.Calculations.Add(calculation);
        Db.SaveChanges();
        return calculation;
    }

    public void Dispose()
    {
        foreach (var ctx in contexts) ctx.Dispose();
        keeper.Dispose();
    }
}

// İlk kaydetmenin HEMEN ÖNCESİNDE araya girer. Yarış senaryosunu deterministik
// kılar: uç "bu ad yok" diye karar verdikten sonra, kendi yazması gerçekleşmeden
// başka bir istek aynı adı yazmış olur.
internal sealed class BeforeFirstSave(Action inject) : SaveChangesInterceptor
{
    private bool fired;

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (!fired)
        {
            fired = true;
            inject();
        }
        return ValueTask.FromResult(result);
    }
}

internal static class TestHttp
{
    // Uç işleyicileri kullanıcıyı `sub` isteminden okur (JWT istem eşlemesi
    // kapalı, bkz. Program.cs). Test de aynı istemi kurar; `ClaimTypes.
    // NameIdentifier` yazmak sessizce yetkisiz bir bağlam üretirdi.
    public static HttpContext For(ApplicationUser user) => For(user.Id);

    public static HttpContext For(string userId)
    {
        var identity = new ClaimsIdentity([new Claim("sub", userId)], "test");
        return new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
    }

    public static HttpContext Anonymous() => new DefaultHttpContext();
}

// `IResult` üzerinden durum kodu ve yük okuma. Minimal API sonuçları bu iki
// arayüzü uygular, yani testin HTTP yığınını kurmasına gerek yok.
internal static class ResultAssert
{
    public static int Status(IResult result) =>
        (result as IStatusCodeHttpResult)?.StatusCode
        ?? throw new InvalidOperationException($"durum kodu taşımayan sonuç: {result.GetType().Name}");

    public static T Value<T>(IResult result) =>
        (result as IValueHttpResult)?.Value is T value
            ? value
            : throw new InvalidOperationException($"{typeof(T).Name} taşımayan sonuç: {result.GetType().Name}");
}

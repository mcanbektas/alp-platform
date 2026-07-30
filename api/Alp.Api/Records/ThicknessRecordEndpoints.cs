using Alp.Api.Common;
using Alp.Api.Http;
using Alp.Data;
using Alp.Domain;
using Microsoft.EntityFrameworkCore;

namespace Alp.Api.Records;

// Bakır kalınlığı kayıtlarının hesaba bağlı hâli (Faz 7).
//
// Kayıtlar bugüne kadar yalnız tarayıcıda duruyordu; oradan taşındıklarında
// kullanıcı aynı listeyi ikinci bir bilgisayarda da görüyor. Girişsiz kullanım
// bozulmadı: oturum açılmamışken ekran hâlâ tarayıcı deposunu kullanır, ilk
// girişte yereldeki kayıtlar buraya bir kez kopyalanır (bkz. useSavedThickness).
//
// `DataJson` sunucu için OPAK dizedir — şemayı `web/src/lib/thicknessRecords.js`
// tanımlar ve doğrular. Sunucu hiçbir aracın içeriğini bilmez; burada yalnız
// ada göre teklik, sayı sınırı ve sahiplik vardır.
public static class ThicknessRecordEndpoints
{
    // Tek kayıt küçüktür (bir avuç sayı); sınır kaza eseri şişmiş bir gövdeyi
    // baştan kesmek için.
    private const long RecordBodyLimitBytes = 32 * 1024;

    // `thicknessRecords.js` içindeki NAME_MAX / RECORD_MAX ile aynı sayılar.
    // İstemci zaten uyguluyor; sunucu kendi sınırını ayrıca uygular çünkü uç
    // doğrudan da çağrılabilir.
    private const int NameMax = 60;
    private const int RecordMax = 50;

    public static void MapThicknessRecordEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/thickness-records").RequireAuthorization();

        group.MapGet("/", ListRecords);
        group.MapPost("/", SaveRecord).RequireRateLimiting("writes").LimitBodySize(RecordBodyLimitBytes);
        group.MapDelete("/{id:guid}", DeleteRecord).RequireRateLimiting("writes");
    }

    private static async Task<IResult> ListRecords(AppDbContext db, HttpContext http)
    {
        var userId = CurrentUserId(http);
        if (userId is null) return Results.Unauthorized();

        var records = await db.ThicknessRecords
            .Where(r => r.UserId == userId)
            .OrderBy(r => r.Name)
            .Select(r => new ThicknessRecordDto(r.Id, r.Name, r.SchemaVersion, r.DataJson, r.CreatedAt))
            .ToListAsync();

        return Results.Ok(new ThicknessRecordListResponse(records));
    }

    // Aynı ad = aynı kayıt: ikinci kayıt üzerine yazar, çoğaltmaz. Kural saf
    // katmandan (`thicknessRecords.js` → `recordId`) geliyor ve burada da
    // uygulanır, yoksa aynı ad iki cihazdan iki satır olurdu.
    //
    // Karşılaştırma Türkçe kurallarına göre küçük harfe indirgenmiş ad üzerinden
    // yapılır — istemcideki `recordId` ile aynı: "Üst Katman" ile "üst katman"
    // aynı kayıt, "Ust katman" ayrı kayıt.
    private static async Task<IResult> SaveRecord(
        SaveThicknessRecordRequest req, AppDbContext db, HttpContext http)
    {
        var userId = CurrentUserId(http);
        if (userId is null) return Results.Unauthorized();

        var name = NormalizeName(req.Name);
        if (name.Length == 0) return Results.BadRequest(new ApiError("MISSING_FIELDS", new { field = "name" }));
        if (name.Length > NameMax) return Results.BadRequest(new ApiError("TOO_LONG", new { field = "name", max = NameMax }));
        if (req.SchemaVersion < 1) return Results.BadRequest(new ApiError("MISSING_FIELDS", new { field = "schemaVersion" }));
        if (string.IsNullOrWhiteSpace(req.DataJson)) return Results.BadRequest(new ApiError("MISSING_FIELDS", new { field = "dataJson" }));

        var key = RecordKey(name);

        // Var olanı ararken `DataJson` ÇEKİLMEZ: 50 kaydın zarfını yalnızca
        // "bu ad duruyor mu" sorusu için belleğe almak gereksiz.
        var match = await db.ThicknessRecords
            .FirstOrDefaultAsync(r => r.UserId == userId && r.NameKey == key);

        if (match is null)
        {
            var stored = await db.ThicknessRecords.CountAsync(r => r.UserId == userId);
            if (stored >= RecordMax)
            {
                // Sessizce en eskiyi atmak yerine açık hata: kullanıcı hangi
                // kaydı sileceğine kendisi karar verir.
                return Results.Conflict(new ApiError("RECORD_LIMIT", new { limit = RecordMax, stored }));
            }

            match = new ThicknessRecord
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.ThicknessRecords.Add(match);
        }

        match.Name = name;
        match.NameKey = key;
        match.SchemaVersion = req.SchemaVersion;
        match.DataJson = req.DataJson;

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException) when (match.Id != Guid.Empty && db.Entry(match).State == EntityState.Added)
        {
            // Benzersiz dizin çakıştı: aynı adla eşzamanlı ikinci bir istek
            // araya girdi. Bu bir hata değil — "aynı ad = aynı kayıt" kuralının
            // ta kendisi. Eklemeyi bırakıp o satırın üzerine yazılır.
            db.Entry(match).State = EntityState.Detached;

            var winner = await db.ThicknessRecords
                .FirstOrDefaultAsync(r => r.UserId == userId && r.NameKey == key);
            if (winner is null) throw; // çakışma başka bir nedenden — yut­ma

            winner.Name = name;
            winner.SchemaVersion = req.SchemaVersion;
            winner.DataJson = req.DataJson;
            await db.SaveChangesAsync();
            match = winner;
        }

        return Results.Ok(new ThicknessRecordDto(match.Id, match.Name, match.SchemaVersion, match.DataJson, match.CreatedAt));
    }

    private static async Task<IResult> DeleteRecord(Guid id, AppDbContext db, HttpContext http)
    {
        var userId = CurrentUserId(http);
        if (userId is null) return Results.Unauthorized();

        var record = await db.ThicknessRecords.FirstOrDefaultAsync(r => r.Id == id);
        // Yok olan ve başkasına ait kayıt AYNI 404'ü verir — numaralandırmaya
        // kapalı (projelerdeki kuralın aynısı).
        if (record is null || record.UserId != userId) return Results.NotFound();

        db.ThicknessRecords.Remove(record);
        await db.SaveChangesAsync();

        return Results.NoContent();
    }

    private static string NormalizeName(string? name) =>
        string.Join(' ', (name ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string RecordKey(string name) =>
        NormalizeName(name).ToLower(System.Globalization.CultureInfo.GetCultureInfo("tr-TR"));

    private static string? CurrentUserId(HttpContext http) =>
        http.User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
}

public record ThicknessRecordDto(Guid Id, string Name, int SchemaVersion, string DataJson, DateTimeOffset CreatedAt);

public record ThicknessRecordListResponse(IReadOnlyList<ThicknessRecordDto> Records);

// `DataJson` istemcinin doğruladığı kayıt zarfıdır ve olduğu gibi saklanır.
public record SaveThicknessRecordRequest(string Name, int SchemaVersion, string DataJson);

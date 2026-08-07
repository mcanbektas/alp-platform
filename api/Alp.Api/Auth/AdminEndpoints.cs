using Alp.Api.Common;
using Alp.Api.Http;
using Alp.Api.Logging;
using Alp.Data;
using Alp.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Serilog.Events;

namespace Alp.Api.Auth;

// Yönetim yüzeyi. İki uç var ve ikisi de rolü KENDİLERİ doğrular: grup
// `RequireAuthorization()` ile yalnız oturumu şart koşar, rol kontrolü
// `RequireAdmin` içinde ve her istekte veritabanından yapılır.
//
// Rol token'a KONMADI. Konsaydı yetki alınan bir hesap, elindeki erişim
// token'ının ömrü boyunca (15 dk) admin kalmaya devam ederdi. Bedeli admin
// isteği başına bir rol sorgusudur — bu uçlar nadir çağrılıyor.
public static class AdminEndpoints
{
    private const long AdminBodyLimitBytes = 16 * 1024;

    // Sayfa boyutu istemciden gelir ama sınırı sunucu koyar: `pageSize=100000`
    // yazan bir istek bütün kullanıcı tablosunu tek yanıtta dışarı verirdi.
    private const int DefaultPageSize = 25;
    private const int MaxPageSize = 100;

    // Operasyonel log ekranının varsayılan pencere boyutu. Tavan yoktur —
    // ListLogs'ta gerçek tavan LogBufferSink.Capacity'den okunur, tampon
    // kapasitesi App:LogBufferSize ile değişebildiği için burada sabitlenmez.
    private const int DefaultLogTake = 200;

    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin").RequireAuthorization();

        admin.MapGet("/users", ListUsers);
        // Silme DELETE değil POST: isteyenin parolasını gövdede taşıyor ve
        // istemcinin `api.del()` yardımcısı gövde göndermiyor. Parola denemesi
        // içerdiği için sınırı parola değiştirmeyle aynı kovada.
        admin.MapPost("/users/{id}/delete", DeleteUser)
            .RequireRateLimiting("password").LimitBodySize(AdminBodyLimitBytes);
        // Parola istemez (bkz. ChangePlan yorumu) — "writes" kovası UpdateMe
        // ile aynı, "password" kovasına girmiyor çünkü kimlik doğrulaması yok.
        admin.MapPost("/users/{id}/plan", ChangePlan)
            .RequireRateLimiting("writes").LimitBodySize(AdminBodyLimitBytes);
        // Gövdesiz istek (bkz. UnlockUser yorumu) — DeleteProject/DeleteCalculation
        // gibi gövdesiz uçlarla aynı gerekçeyle `LimitBodySize` verilmez.
        admin.MapPost("/users/{id}/unlock", UnlockUser)
            .RequireRateLimiting("writes");
        admin.MapGet("/audit", ListAudit);
        admin.MapGet("/logs", ListLogs);
    }

    internal static async Task<IResult> ListUsers(
        UserManager<ApplicationUser> userManager,
        AppDbContext db,
        HttpContext http,
        [FromQuery] string? q = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = DefaultPageSize)
    {
        var (_, error) = await RequireAdmin(userManager, http);
        if (error is not null) return error;

        page = page < 1 ? 1 : page;
        pageSize = pageSize is < 1 or > MaxPageSize ? DefaultPageSize : pageSize;

        var query = db.Users.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(q))
        {
            // Arama e-postada VE görünen adda çalışır: yönetici genelde
            // e-postayı bilir ama kullanıcı adıyla da aratır.
            //
            // `ToLowerInvariant`, kültüre bağlı `ToLower` DEĞİL: sunucu Türkçe
            // yerel ayarla koşarsa "I" → "ı" (noktasız) olur, veritabanının
            // LOWER()'ı ise "i" üretir — büyük harfli arama sessizce boş
            // dönerdi. Sütun tarafını DB küçültür, iğneyi info-değişmez C#.
            //
            // 'İ' (U+0130) küçültmeden ÖNCE eşlenir: invariant küçültme onu
            // "i + birleşen nokta" (U+0069 U+0307) yapar ve Contains hiçbir
            // kayıtla eşleşmez — "YÖNETİCİ" araması sessizce boş dönerdi.
            var needle = q.Trim().Replace('İ', 'i').Replace('I', 'i').ToLowerInvariant();

            // İkinci iğne: Türkçe harfleri ASCII'ye katlanmış hâli. Kayıtların
            // çoğu ASCII ("Yonetici", "alptest") ama arayan doğal yazımı yazar
            // ("yönetici") — katlama olmadan bu arama sıfır sonuç veriyordu ve
            // "arama çalışmıyor" gibi görünüyordu. Ters yön (kayıt aksanlı,
            // arama ASCII) bilinçli kapsam dışı: onun için sütunun da DB
            // tarafında katlanması (unaccent) gerekir, testlerin SQLite'ında
            // yok.
            var folded = FoldTurkish(needle);

            query = needle == folded
                ? query.Where(u =>
                    (u.Email != null && u.Email.ToLower().Contains(needle))
                    || u.DisplayName.ToLower().Contains(needle))
                : query.Where(u =>
                    (u.Email != null && (u.Email.ToLower().Contains(needle)
                        || u.Email.ToLower().Contains(folded)))
                    || u.DisplayName.ToLower().Contains(needle)
                    || u.DisplayName.ToLower().Contains(folded));
        }

        var total = await query.CountAsync();

        // Sıralama en yeni kayıt üstte. Bağ (aynı saniye) durumunda Id ikinci
        // ölçüt: sırasız sayfalama aynı kaydı iki sayfada gösterebilir.
        var rows = await query
            .OrderByDescending(u => u.CreatedAt)
            .ThenBy(u => u.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(u => new
            {
                u.Id,
                u.Email,
                u.DisplayName,
                u.Company,
                u.Plan,
                u.EmailConfirmed,
                u.LockoutEnd,
                u.CreatedAt,
                ProjectCount = db.Projects.Count(p => p.UserId == u.Id),
                ReportCount = db.Reports.Count(r => r.UserId == u.Id),
            })
            .ToListAsync();

        // Admin kümesi tek sorguda: satır başına rol sorgusu sayfa boyutu kadar
        // gidiş-geliş demek olurdu. Liste zaten kısa (yapılandırmadan geliyor).
        var adminIds = (await userManager.GetUsersInRoleAsync(AdminRole.Name))
            .Select(u => u.Id)
            .ToHashSet(StringComparer.Ordinal);

        var items = rows
            .Select(r => new AdminUserRow(
                r.Id,
                r.Email ?? string.Empty,
                r.DisplayName,
                r.Company,
                r.Plan,
                r.EmailConfirmed,
                r.LockoutEnd,
                adminIds.Contains(r.Id),
                r.CreatedAt,
                r.ProjectCount,
                r.ReportCount))
            .ToList();

        return Results.Ok(new AdminUserPage(items, total, page, pageSize));
    }

    internal static async Task<IResult> DeleteUser(
        string id,
        AdminDeleteUserRequest req,
        UserManager<ApplicationUser> userManager,
        AppDbContext db,
        AuditLog auditLog,
        HttpContext http)
    {
        var (admin, error) = await RequireAdmin(userManager, http);
        if (error is not null) return error;

        if (string.IsNullOrWhiteSpace(req.CurrentPassword))
        {
            return Results.BadRequest(new ApiError("MISSING_FIELDS"));
        }

        // Yanlış parola 401 DEĞİL 400 döner. 401 olsaydı istemci bunu oturumun
        // düşmesi sanıp sessiz yenileme deneyip kullanıcıyı çıkışa atardı
        // (web/src/lib/api.js). Oturum geçerli, yanlış olan parola.
        if (!await userManager.CheckPasswordAsync(admin!, req.CurrentPassword))
        {
            return Results.BadRequest(new ApiError("INVALID_CREDENTIALS"));
        }

        var target = await userManager.FindByIdAsync(id);
        if (target is null) return Results.NotFound(new ApiError("NOT_FOUND"));

        // Kendini silme: yöneticinin kendi hesabını silmesi paneli sahipsiz
        // bırakır ve o an açık olan oturumu ortasından koparır.
        if (string.Equals(target.Id, admin!.Id, StringComparison.Ordinal))
        {
            return Results.BadRequest(new ApiError("SELF_DELETE"));
        }

        // Admin admini silemez. Yetkinin kaynağı yapılandırmadır: bir yöneticiyi
        // gerçekten silmek gerekiyorsa önce `App:AdminEmails` listesinden
        // çıkarılır ve uygulama yeniden başlatılır (AdminSeeder yetkiyi alır),
        // sonra sıradan bir kullanıcı olarak silinir. Bu adım olmadan panel,
        // yöneticilerin birbirini tasfiye edebildiği bir yüzey olurdu.
        if (await userManager.IsInRoleAsync(target, AdminRole.Name))
        {
            return Results.BadRequest(new ApiError("ADMIN_TARGET"));
        }

        var deleted = await AccountDeletion.DeleteAsync(userManager, db, target, auditLog, admin!, http);
        if (!deleted.Succeeded)
        {
            return Results.BadRequest(new ApiError("IDENTITY_ERROR", new { codes = deleted.Errors.Select(e => e.Code) }));
        }

        // Silinen kullanıcının elindeki erişim token'ı için ayrıca bir şey
        // gerekmez: her istekte kullanıcı `sub` ile yeniden okunuyor ve artık
        // bulunamıyor (Program.cs → OnTokenValidated). Yenileme token'ları
        // yukarıdaki silmeyle birlikte gitti.
        return Results.NoContent();
    }

    // Plan değişimi (free ↔ pro). Ödeme sistemi yok, admin elle yönetiyor —
    // bugüne kadar psql ile yapılıyordu, artık panelden. DeleteUser'ın
    // aksine mevcut parola İSTEMEZ: plan değişimi geri alınabilir, düşük
    // riskli bir işlem, ekstra sürtünme gerekmiyor (yalnız admin oturumu
    // yeterli). Silmedeki self-delete/admin-target kısıtları da BURADA YOK —
    // admin kendi planını değiştirebilir, farklı risk sınıfı.
    internal static async Task<IResult> ChangePlan(
        string id,
        AdminChangePlanRequest req,
        UserManager<ApplicationUser> userManager,
        AuditLog auditLog,
        HttpContext http)
    {
        var (admin, error) = await RequireAdmin(userManager, http);
        if (error is not null) return error;

        if (req.Plan is not ("free" or "pro"))
        {
            return Results.BadRequest(new ApiError("INVALID_PLAN"));
        }

        var target = await userManager.FindByIdAsync(id);
        if (target is null) return Results.NotFound(new ApiError("NOT_FOUND"));

        // Değer zaten aynıysa yine 204 döner ama audit YAZILMAZ: hiçbir şey
        // değişmedi, iz bırakmak gürültüden başka bir şey olmazdı.
        if (string.Equals(target.Plan, req.Plan, StringComparison.Ordinal))
        {
            return Results.NoContent();
        }

        var fromPlan = target.Plan;
        target.Plan = req.Plan;

        var updated = await userManager.UpdateAsync(target);
        if (!updated.Succeeded)
        {
            return Results.BadRequest(new ApiError("IDENTITY_ERROR", new { codes = updated.Errors.Select(e => e.Code) }));
        }

        // Best-effort (WriteAsync): plan zaten değişti, iz yazımı patlarsa
        // işlemi geri almanın bedeli faydasından ağır — DeleteUser'daki
        // transaction-içi WriteCriticalAsync'in aksine burada kritik değil.
        await auditLog.WriteAsync(
            AuditEventCodes.AdminPlanChanged, admin, target, http,
            new { fromPlan, toPlan = req.Plan });

        return Results.NoContent();
    }

    // Yanlış kilitlenmeyi elle düzeltme (AuthEndpoints.Login → AccessFailedAsync
    // eşiği aşınca kilitliyor, bkz. oradaki yorum). ChangePlan ile AYNI risk
    // sınıfı: parola İSTEMEZ, geri alınabilir, düşük riskli — silmedeki
    // sürtünmeye gerek yok, admin oturumu yeterli.
    internal static async Task<IResult> UnlockUser(
        string id,
        UserManager<ApplicationUser> userManager,
        AuditLog auditLog,
        HttpContext http)
    {
        var (admin, error) = await RequireAdmin(userManager, http);
        if (error is not null) return error;

        var target = await userManager.FindByIdAsync(id);
        if (target is null) return Results.NotFound(new ApiError("NOT_FOUND"));

        // Zaten kilitli DEĞİLSE (LockoutEnd null ya da geçmişte) no-op: 204
        // döner ama audit YAZILMAZ — ChangePlan'daki "değer zaten aynıysa iz
        // bırakma" kararıyla aynı gerekçe, hiçbir şey değişmedi. Ölçüt Login'in
        // kendi kilit kontrolüyle BİREBİR aynı (`IsLockedOutAsync`), panelin
        // rozet koşuluyla (`lockoutEnd > şu an`) da eşdeğer.
        if (!userManager.SupportsUserLockout || !await userManager.IsLockedOutAsync(target))
        {
            return Results.NoContent();
        }

        var previousLockoutEnd = target.LockoutEnd;

        var unlocked = await userManager.SetLockoutEndDateAsync(target, null);
        if (!unlocked.Succeeded)
        {
            return Results.BadRequest(new ApiError("IDENTITY_ERROR", new { codes = unlocked.Errors.Select(e => e.Code) }));
        }

        // AccessFailedCount'u AYRICA sıfırlar. Identity normalde bunu zaten
        // AccessFailedAsync İÇİNDE yapar — eşik aşılıp kilit oluştuğu anda
        // sayaç kendi içinde 0'a döner (AuthEndpoints.Login → Login yorumu,
        // `store.ResetAccessFailedCountAsync`), yani standart yoldan kilitlenmiş
        // bir hesabın sayacı buraya gelmeden önce teorik olarak zaten sıfırdır.
        // Yine de burada AYRICA çağrılır: bu davranış Identity'nin iç akışına
        // bağlı bir yan etkidir, sözleşme değil — LockoutEnd'i doğrudan
        // veritabanından elle set eden bir yol (script, göç, ileride başka bir
        // uç) sayacı sıfırlamadan bırakabilir. O durumda kilidi açtıktan hemen
        // sonra TEK bir yanlış parola denemesi hesabı yeniden kilitlerdi — admin
        // "açtım" derken kullanıcı yine dışarıda kalırdı. Çağrı ucuz ve
        // zaten-sıfırsa etkisiz; riski almaya değmez.
        await userManager.ResetAccessFailedCountAsync(target);

        // Best-effort (WriteAsync): kilit zaten açıldı, iz yazımı patlarsa
        // işlemi geri almanın bedeli faydasından ağır — ChangePlan'daki
        // gerekçenin aynısı.
        await auditLog.WriteAsync(
            AuditEventCodes.AdminLockoutCleared, admin, target, http,
            new { previousLockoutEnd });

        return Results.NoContent();
    }

    // Denetim izi listesi (docs/brifler/11-loglama.md §4). Satırda ham `Event`
    // kodu gider — cümleyi istemci sözlüğü kurar, iki dillilik sunucuya taşınmaz.
    internal static async Task<IResult> ListAudit(
        UserManager<ApplicationUser> userManager,
        AppDbContext db,
        HttpContext http,
        [FromQuery] string? @event = null,
        [FromQuery] string? q = null,
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = DefaultPageSize)
    {
        var (_, error) = await RequireAdmin(userManager, http);
        if (error is not null) return error;

        page = page < 1 ? 1 : page;
        pageSize = pageSize is < 1 or > MaxPageSize ? DefaultPageSize : pageSize;

        var query = db.AuditEvents.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(@event))
        {
            query = query.Where(a => a.Event == @event);
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            // ListUsers'taki arama iğnesiyle AYNI yol: aktör VE hedef e-postada
            // arar, Türkçe katlama uygulanır.
            var needle = q.Trim().Replace('İ', 'i').Replace('I', 'i').ToLowerInvariant();
            var folded = FoldTurkish(needle);

            query = needle == folded
                ? query.Where(a =>
                    (a.ActorEmail != null && a.ActorEmail.ToLower().Contains(needle))
                    || (a.TargetEmail != null && a.TargetEmail.ToLower().Contains(needle)))
                : query.Where(a =>
                    (a.ActorEmail != null && (a.ActorEmail.ToLower().Contains(needle)
                        || a.ActorEmail.ToLower().Contains(folded)))
                    || (a.TargetEmail != null && (a.TargetEmail.ToLower().Contains(needle)
                        || a.TargetEmail.ToLower().Contains(folded))));
        }

        if (from is not null) query = query.Where(a => a.OccurredAt >= from.Value);
        if (to is not null) query = query.Where(a => a.OccurredAt <= to.Value);

        var total = await query.CountAsync();

        // En yeni olay üstte; bağ (aynı an) durumunda Id ikinci ölçüt —
        // ListUsers'taki sıralama gerekçesiyle aynı.
        var items = await query
            .OrderByDescending(a => a.OccurredAt)
            .ThenByDescending(a => a.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new AuditEventRow(
                a.Id, a.OccurredAt, a.Event, a.ActorEmail, a.TargetEmail, a.DetailJson))
            .ToListAsync();

        return Results.Ok(new AuditPage(items, total, page, pageSize));
    }

    // Operasyonel log listesi — bellek içi halka tampon (docs/brifler/
    // 12-loglama-ekrani.md §3-4). Denetim iziyle (ListAudit) KARIŞTIRILMAZ:
    // burası kalıcı değildir, yeniden başlatmada uçar; "şu an ne oluyor"
    // sorusuna bakar, "kim ne yaptı" sorusuna değil.
    internal static async Task<IResult> ListLogs(
        LogBufferSink sink,
        UserManager<ApplicationUser> userManager,
        HttpContext http,
        [FromQuery] string? level = null,
        [FromQuery] string? q = null,
        [FromQuery] int take = DefaultLogTake)
    {
        var (_, error) = await RequireAdmin(userManager, http);
        if (error is not null) return error;

        take = take < 1 ? DefaultLogTake : Math.Min(take, sink.Capacity);

        IEnumerable<LogBufferEntry> items = sink.Snapshot();

        // Tampon zaten Information ve üstünü tutar (LogBufferSink.Emit); bu
        // süzme yalnız Warning/Error eşiğini KULLANICI isteğine göre daraltır.
        if (level is "warning")
        {
            items = items.Where(e => Enum.Parse<LogEventLevel>(e.Level) >= LogEventLevel.Warning);
        }
        else if (level is "error")
        {
            items = items.Where(e => Enum.Parse<LogEventLevel>(e.Level) >= LogEventLevel.Error);
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            // Denetim izi/kullanıcı aramasındaki Türkçe katlama BİLEREK yok:
            // operasyonel mesajlar ve kaynak adları zaten İngilizce/teknik.
            var needle = q.Trim();
            items = items.Where(e =>
                e.Message.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || (e.SourceContext?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false)
                || (e.RequestPath?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false)
                // İstek kimliği tam eşleşme değil `Contains` — admin panoya
                // yapıştırdığı kimliği kısmen de yazsa bulur (docs/brifler/
                // 14-loglama-altyapi.md §2, asıl kullanım senaryosu budur).
                || (e.RequestId?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        var rows = items
            .OrderByDescending(e => e.OccurredAt)
            .Take(take)
            .Select(e => new LogEntryRow(
                e.OccurredAt, e.Level, e.Message, e.Exception, e.SourceContext, e.RequestPath, e.UserId,
                e.RequestId, e.Properties, e.TruncatedPropertyCount))
            .ToList();

        return Results.Ok(new LogPage(rows, sink.Capacity));
    }

    // Küçük harfli girdideki Türkçe harfleri ASCII karşılığına indirir.
    // Yalnız arama iğnesine uygulanır, saklanan veriye değil.
    internal static string FoldTurkish(string lowered) =>
        lowered
            .Replace('ç', 'c').Replace('ğ', 'g').Replace('ı', 'i')
            .Replace('ö', 'o').Replace('ş', 's').Replace('ü', 'u');

    // Oturum + rol. Rol yoksa 403 döner, 401 değil: 401 istemciye "oturumun
    // düştü" der ve sessiz yenilemeyi tetikler (web/src/lib/api.js) — oturum
    // geçerli, eksik olan yetki.
    private static async Task<(ApplicationUser? User, IResult? Error)> RequireAdmin(
        UserManager<ApplicationUser> userManager, HttpContext http)
    {
        var user = await AuthEndpoints.CurrentUser(userManager, http);
        if (user is null)
        {
            return (null, Results.Json(new ApiError("UNAUTHORIZED"), statusCode: StatusCodes.Status401Unauthorized));
        }

        if (!await userManager.IsInRoleAsync(user, AdminRole.Name))
        {
            return (null, Results.Json(new ApiError("FORBIDDEN"), statusCode: StatusCodes.Status403Forbidden));
        }

        return (user, null);
    }
}

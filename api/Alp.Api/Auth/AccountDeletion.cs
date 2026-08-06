using Alp.Data;
using Alp.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Alp.Api.Auth;

// Bir hesabın ve ona bağlı bütün kayıtların silinmesi. Tek kopya olması şart:
// tablo listesi eksik kalırsa silinmiş bir kullanıcının verisi geride kalır ve
// bunu hiçbir test yakalamaz — yalnız yabancı anahtarı olan tablolar patlar,
// olmayanlar sessizce birikir.
internal static class AccountDeletion
{
    internal static async Task<IdentityResult> DeleteAsync(
        UserManager<ApplicationUser> userManager, AppDbContext db, ApplicationUser user,
        AuditLog auditLog, ApplicationUser actor, HttpContext http)
    {
        var userId = user.Id;

        // Yürütme stratejisi üzerinden: bağlam `EnableRetryOnFailure` ile
        // kurulu (Program.cs) ve yeniden deneyen bir strateji, elle açılmış
        // işlemi doğrudan kabul etmez — sarmalanmazsa üretimde InvalidOperation
        // ile patlar, testlerin SQLite'ında ise fark edilmezdi.
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();

            // Sıra bağlayıcıdır: manifest, blob'a `Restrict` ile bağlı.
            await db.ReportSnapshotSections.Where(s => s.UserId == userId).ExecuteDeleteAsync();
            await db.SectionBlobs.Where(b => b.UserId == userId).ExecuteDeleteAsync();
            var reportCount = await db.Reports.Where(r => r.UserId == userId).ExecuteDeleteAsync();
            await db.Calculations.Where(c => c.Project!.UserId == userId).ExecuteDeleteAsync();
            var projectCount = await db.Projects.Where(p => p.UserId == userId).ExecuteDeleteAsync();
            await db.RefreshTokens.Where(t => t.UserId == userId).ExecuteDeleteAsync();
            // FK'si olmayan tek tablo — bunu silen başka hiçbir şey yok.
            await db.ThicknessRecords.Where(r => r.UserId == userId).ExecuteDeleteAsync();

            // Kullanıcı satırı en sonda. Identity'nin kendi yan tabloları
            // (AspNetUserClaims/Logins/Roles/Tokens) buradan cascade ile gider.
            var result = await userManager.DeleteAsync(user);
            if (!result.Succeeded)
            {
                await tx.RollbackAsync();
                return result;
            }

            // Commit'ten ÖNCE, aynı transaction içinde: iz yazılamıyorsa silme
            // de olmasın (docs/brifler/11-loglama.md §3, dayanıklılık sözleşmesi).
            await auditLog.WriteCriticalAsync(
                AuditEventCodes.AdminUserDeleted, actor, user, http,
                new { projectCount, reportCount });

            await tx.CommitAsync();
            return result;
        });
    }
}

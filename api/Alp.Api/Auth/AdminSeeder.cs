using Alp.Domain;
using Microsoft.AspNetCore.Identity;

namespace Alp.Api.Auth;

// Yönetim yetkisinin TEK kaynağı yapılandırmadır: `App:AdminEmails`. Kayıt
// ekranından, panelden ya da başka bir uçtan admin yapılamaz — yetki vermek
// sunucunun ortam değişkenini değiştirip yeniden başlatmayı gerektirir.
//
// Gerekçe: yetki yükseltme uçtan yapılabilseydi, ele geçirilmiş tek bir admin
// oturumu kalıcı ikinci bir admin açabilirdi. Ortam değişkeni, uygulamanın
// içinden erişilemeyen bir yerdir.
public static class AdminRole
{
    public const string Name = "Admin";
}

internal static class AdminSeeder
{
    internal const string ConfigKey = "App:AdminEmails";

    // Virgül ya da noktalı virgülle ayrılır. Boşluklar atılır, karşılaştırma
    // büyük/küçük harf duyarsızdır — kullanıcı e-postasını "Ali@..." yazıp
    // yapılandırmaya "ali@..." koyduğunda yetki sessizce düşmesin.
    internal static IReadOnlyList<string> ParseEmails(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        return raw
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    // Açılışta bir kez: rolü kurar, listedeki hesaplara verir, listeden çıkmış
    // hesaplardan ALIR. Son adım şart — yoksa `.env`ten silinen bir e-posta
    // veritabanında admin kalmaya devam ederdi ve yetkiyi geri almanın tek yolu
    // elle SQL olurdu.
    internal static async Task SyncAsync(IServiceProvider services, IConfiguration config, ILogger logger)
    {
        var wanted = ParseEmails(config[ConfigKey]);

        var roleManager = services.GetRequiredService<RoleManager<IdentityRole>>();
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        var auditLog = services.GetRequiredService<AuditLog>();

        if (!await roleManager.RoleExistsAsync(AdminRole.Name))
        {
            // Liste boşken bile rol kurulur: panelin yetki kontrolü rolün
            // varlığına bakar, yoksa her istekte Identity'den hata döner.
            var created = await roleManager.CreateAsync(new IdentityRole(AdminRole.Name));
            if (!created.Succeeded)
            {
                logger.LogError(
                    "Admin rolü kurulamadı: {Codes}",
                    string.Join(", ", created.Errors.Select(e => e.Code)));
                return;
            }
        }

        foreach (var email in wanted)
        {
            var user = await userManager.FindByEmailAsync(email);
            if (user is null)
            {
                // Yetki verilecek hesap henüz açılmamış olabilir. Kayıt anında
                // ikinci bir kontrol var (`GrantIfListedAsync`), o yüzden bu
                // ölümcül değil — ama sessiz de kalmamalı: en sık yapılan hata
                // e-postayı yanlış yazmaktır.
                logger.LogWarning(
                    "{Key} içindeki {Email} adresine sahip bir hesap yok; kayıt olduğunda yetki verilecek.",
                    ConfigKey, email);
                continue;
            }

            if (!await userManager.IsInRoleAsync(user, AdminRole.Name))
            {
                await userManager.AddToRoleAsync(user, AdminRole.Name);
                logger.LogInformation("Yönetim yetkisi verildi: {Email}", email);
                await auditLog.WriteAsync(AuditEventCodes.AdminRoleGranted, actor: null, target: user);
            }
        }

        foreach (var user in await userManager.GetUsersInRoleAsync(AdminRole.Name))
        {
            var email = user.Email?.ToLowerInvariant();
            if (email is not null && wanted.Contains(email, StringComparer.Ordinal)) continue;

            await userManager.RemoveFromRoleAsync(user, AdminRole.Name);
            logger.LogInformation("Yönetim yetkisi alındı (listede yok): {Email}", user.Email);
            await auditLog.WriteAsync(AuditEventCodes.AdminRoleRevoked, actor: null, target: user);
        }
    }

    // Kayıt anında: açılışta hesabı bulunamayan bir admin e-postası sonradan
    // kayıt olursa yetkiyi bir sonraki yeniden başlatmayı beklemeden alır.
    internal static async Task GrantIfListedAsync(
        UserManager<ApplicationUser> userManager, IConfiguration config, ApplicationUser user, AuditLog auditLog)
    {
        var email = user.Email?.ToLowerInvariant();
        if (email is null) return;
        if (!ParseEmails(config[ConfigKey]).Contains(email, StringComparer.Ordinal)) return;

        await userManager.AddToRoleAsync(user, AdminRole.Name);
        await auditLog.WriteAsync(AuditEventCodes.AdminRoleGranted, actor: null, target: user);
    }
}

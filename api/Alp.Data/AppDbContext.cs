using Alp.Domain;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Alp.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Calculation> Calculations => Set<Calculation>();
    public DbSet<Report> Reports => Set<Report>();
    public DbSet<ThicknessRecord> ThicknessRecords => Set<ThicknessRecord>();

    // Kullanıcı silinince Project → Calculation/Report zinciri de silinir
    // (Cascade). Bilinçli karar: bir hesap silme ucu eklendiğinde bu, kalıntı
    // veri bırakmayan gerçek bir silme sağlar (KVKK/GDPR "unutulma hakkı"
    // beklentisiyle uyumlu). `Restrict`e çevirmek daha güvenli GÖRÜNÜR ama
    // aslında hesap silmeyi FK ihlaliyle kilitler — o zaman zaten uygulama
    // kodunun elle aynı zinciri silmesi gerekir. Hesap silme ucu eklenirken
    // (Faz 5+) kullanıcıya onay adımı orada sorulur, şemada değil.
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<RefreshToken>(e =>
        {
            e.HasIndex(t => t.TokenHash).IsUnique();
            e.HasIndex(t => t.UserId);
            e.HasOne(t => t.User).WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Project>(e =>
        {
            e.HasIndex(p => p.UserId);
            e.HasOne(p => p.User).WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Calculation>(e =>
        {
            e.HasIndex(c => c.ProjectId);
            e.HasOne(c => c.Project).WithMany(p => p.Calculations)
                .HasForeignKey(c => c.ProjectId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Report>(e =>
        {
            e.HasIndex(r => r.ProjectId);
            e.HasIndex(r => r.UserId);
            // Proje silinirse rapor kaydı silinmez, yalnızca bağı kopar: kütük
            // (kim, ne zaman, hangi biçim) kalır. Belgenin kendisi saklanmadığı
            // için o rapor artık YENİDEN ÜRETİLEMEZ — indirme ucu bu durumda
            // `REPORT_NOT_REPRODUCIBLE` döner, sessizce boş dosya vermez.
            // Hesap silinirse (User->Report ayrı bir ilişki değil, aşağıdaki
            // AspNetUsers FK'sı) gerçek silme uygulanır, bkz. sınıf üstü not.
            e.HasOne(r => r.Project).WithMany().HasForeignKey(r => r.ProjectId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ThicknessRecord>(e =>
        {
            e.HasIndex(t => t.UserId);
        });
    }
}

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
            // SHA-256 hex 64 karakterdir; IP en uzun IPv6 gösterimiyle 45.
            // Bu kolonlara kullanıcı verisi girmez ama sınırsız `text` da
            // olmamalı — sınır, şemanın kendini belgelemesidir.
            e.Property(t => t.TokenHash).HasMaxLength(64);
            e.Property(t => t.ReplacedByHash).HasMaxLength(64);
            e.Property(t => t.CreatedByIp).HasMaxLength(45);
            e.HasIndex(t => t.TokenHash).IsUnique();
            e.HasIndex(t => t.UserId);
            e.HasOne(t => t.User).WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Project>(e =>
        {
            // Sınırlar uç doğrulamasıyla aynı sabitlerden gelir (Alp.Domain).
            // Uç TOO_LONG ile erken döner; buradaki sınır, doğrulamayı atlayan
            // bir yol kalırsa son savunmadır.
            e.Property(p => p.Name).HasMaxLength(Project.NameMaxLength);
            e.Property(p => p.Description).HasMaxLength(Project.DescriptionMaxLength);
            e.HasIndex(p => p.UserId);
            e.HasOne(p => p.User).WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Calculation>(e =>
        {
            e.Property(c => c.ToolKey).HasMaxLength(Calculation.ToolKeyMaxLength);
            e.Property(c => c.ToolMode).HasMaxLength(Calculation.ToolModeMaxLength);
            e.Property(c => c.EngineVersion).HasMaxLength(Calculation.EngineVersionMaxLength);
            e.HasIndex(c => c.ProjectId);
            e.HasOne(c => c.Project).WithMany(p => p.Calculations)
                .HasForeignKey(c => c.ProjectId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Report>(e =>
        {
            e.Property(r => r.Title).HasMaxLength(Report.TitleMaxLength);
            e.Property(r => r.PreparedBy).HasMaxLength(Report.PreparedByMaxLength);
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
            // Özellik kaldırıldı, tablo duruyor — gerekçe Alp.Domain/
            // ThicknessRecord.cs üstünde. Dizinler şema geçmişiyle tutarlılık
            // için yerinde bırakıldı.
            e.HasIndex(t => new { t.UserId, t.NameKey }).IsUnique();
        });
    }
}

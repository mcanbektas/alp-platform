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
    public DbSet<SectionBlob> SectionBlobs => Set<SectionBlob>();
    public DbSet<ReportSnapshotSection> ReportSnapshotSections => Set<ReportSnapshotSection>();
    public DbSet<ThicknessRecord> ThicknessRecords => Set<ThicknessRecord>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<CommProject> CommProjects => Set<CommProject>();
    public DbSet<ProtocolSchema> ProtocolSchemas => Set<ProtocolSchema>();

    // SQLite `DateTimeOffset`i ORDER BY'da REDDEDER (NotSupportedException) —
    // değeri metin olarak sakladığı için metin sıralaması yanlış sonuç verirdi.
    // Postgres'te böyle bir sınır yok (`timestamptz` doğal olarak sıralanır).
    //
    // Bu, üretim davranışını testlere göre eğmek DEĞİLDİR: dönüşüm yalnızca
    // SQLite'ta devreye girer ve orada sıralamayı DOĞRU hâle getirir. Olmasaydı
    // `CreatedAt`e göre sıralayan her sorgu (yönetim panelindeki kullanıcı
    // listesi) testte patlar, üretimde çalışırdı — yani test, ürünün sınandığı
    // yol olmaktan çıkardı.
    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        base.ConfigureConventions(builder);

        if (Database.ProviderName?.Contains("Sqlite", StringComparison.Ordinal) == true)
        {
            builder.Properties<DateTimeOffset>()
                .HaveConversion<Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter>();
        }
    }

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
            // Künyedeki firma artık kütükte de duruyor; sınır profil ucundaki
            // ile AYNI kaynaktan gelir, yoksa profile sığmayan bir ad rapor
            // yoluyla kütüğe kaçardı.
            e.Property(r => r.Company).HasMaxLength(ApplicationUser.CompanyMaxLength);
        });

        // ---- Rapor anlık görüntüsü (docs/rapor-snapshot-karari.md) ----
        //
        // Bölüm içerikleri İÇERİK ADRESLİ tek bir tabloda, raporlar onlara
        // manifest üzerinden bakar. Şemanın taşıdığı üç kural:
        //   1. Dedup kullanıcı sınırındadır — anahtar (UserId, Hash).
        //   2. Rapor silinince manifest gider, blob kalır; gerçekten sahipsiz
        //      kalan blob'u temizlik turu toplar (sayaç tutulmaz).
        //   3. Hesap silinince ikisi de gider (Cascade).
        builder.Entity<SectionBlob>(e =>
        {
            e.HasKey(b => new { b.UserId, b.Hash });
            e.Property(b => b.Hash).HasMaxLength(SectionBlob.HashLength);
            e.HasOne(b => b.User).WithMany().HasForeignKey(b => b.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ReportSnapshotSection>(e =>
        {
            // Aynı bölüm aynı raporda iki kez yer alabilir (iki hesap birebir
            // aynı kaydı taşıyorsa), bu yüzden anahtar sıraya da bakar.
            e.HasKey(s => new { s.ReportId, s.SortOrder });
            e.Property(s => s.Hash).HasMaxLength(SectionBlob.HashLength);
            e.HasOne(s => s.Report).WithMany(r => r.SnapshotSections)
                .HasForeignKey(s => s.ReportId).OnDelete(DeleteBehavior.Cascade);
            // Blob'a giden FK'da `Restrict`: manifest dururken içeriği silmek
            // raporu sessizce yarım bırakırdı. Silme sırası tersinedir —
            // manifest gider, sonra sahipsiz blob toplanır.
            e.HasOne(s => s.Blob).WithMany()
                .HasForeignKey(s => new { s.UserId, s.Hash }).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<ThicknessRecord>(e =>
        {
            e.HasIndex(t => t.UserId);
            // Özellik kaldırıldı, tablo duruyor — gerekçe Alp.Domain/
            // ThicknessRecord.cs üstünde. Dizinler şema geçmişiyle tutarlılık
            // için yerinde bırakıldı.
            e.HasIndex(t => new { t.UserId, t.NameKey }).IsUnique();
        });

        // ---- Denetim izi (docs/brifler/11-loglama.md §3) ----
        // FK yok — yukarıdaki AuditEvent.cs gerekçesiyle aynı. Actor/Target
        // kullanıcı kimliği düz metin olarak durur, AspNetUsers'a bağlanmaz.
        builder.Entity<AuditEvent>(e =>
        {
            e.Property(a => a.Event).HasMaxLength(64);
            e.Property(a => a.Ip).HasMaxLength(45);
            e.HasIndex(a => a.OccurredAt);
            e.HasIndex(a => a.Event);
        });

        // ---- Comm modülü (CLAUDE.md "Ürün modülü kuralları") ----
        // İlk şema-ayrılmış modül: kendi tabloları `comm` şemasında yaşar.
        // AspNetUsers'a giden FK istisnadır (Identity ortak kabul edilir,
        // Project/Calculation de aynı şekilde bağlanır) — modüller arası FK
        // yasağı bunu kapsamaz. Identity/Project/Report tabloları kendileri
        // henüz ayrı bir `platform` şemasına taşınmadı (bu depodaki İLK
        // şema-ayrımı budur); o taşıma ayrı, geniş kapsamlı bir migrasyon
        // ister ve bu fazın kapsamında değil.
        builder.Entity<CommProject>(e =>
        {
            e.ToTable("comm_projects", schema: "comm");
            e.Property(p => p.Name).HasMaxLength(CommProject.NameMaxLength);
            e.Property(p => p.Description).HasMaxLength(CommProject.DescriptionMaxLength);
            e.HasIndex(p => p.UserId);
            e.HasOne(p => p.User).WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ProtocolSchema>(e =>
        {
            e.ToTable("comm_protocol_schemas", schema: "comm");
            e.Property(s => s.Name).HasMaxLength(ProtocolSchema.NameMaxLength);
            e.Property(s => s.Version).HasMaxLength(ProtocolSchema.VersionMaxLength);
            // jsonb yalnızca Postgres'te anlamlıdır; SQLite (testler) düz
            // metin olarak saklar — DateTimeOffset dönüşümündeki kalıpla aynı
            // sağlayıcı-koşullu yaklaşım.
            if (Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true)
            {
                e.Property(s => s.DefinitionJson).HasColumnType("jsonb");
            }
            e.HasIndex(s => new { s.CommProjectId, s.Name, s.Version }).IsUnique();
            e.HasOne(s => s.CommProject).WithMany(p => p.ProtocolSchemas)
                .HasForeignKey(s => s.CommProjectId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}

namespace Alp.Domain;

// Denetim izi satırı — kim ne yaptı, az ve kalıcı. Operasyonel günlükten
// (stdout, uçucu, konteyner silinince gider) ayrı kavram:
// docs/brifler/11-loglama.md §2.
//
// FK YOK, bilinçli: kullanıcı silinince kendi izi cascade ile gitmesin.
// ActorEmail/TargetEmail bu yüzden SNAPSHOT'tır — kullanıcı sonradan silinse
// de iz o anki e-postayla okunur.
public class AuditEvent
{
    public long Id { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string Event { get; set; } = string.Empty;
    public string? ActorUserId { get; set; }
    public string? ActorEmail { get; set; }
    public string? TargetUserId { get; set; }
    public string? TargetEmail { get; set; }
    public string? Ip { get; set; }

    // Yapısal ve DİLSİZ: cümle taşımaz, alan taşır (hata yükü kuralıyla aynı
    // ruh). Cümleyi panel kurar — bkz. F2.
    public string? DetailJson { get; set; }
}

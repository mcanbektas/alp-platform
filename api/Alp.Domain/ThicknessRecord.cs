namespace Alp.Domain;

// localStorage'daki bakır kalınlığı kayıtlarının hesaba taşınmış hâli.
// src/lib/thicknessRecords.js ile aynı şema; DataJson o dosyadaki
// doğrulanmış kayıt yapısını olduğu gibi taşır.
public class ThicknessRecord
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    // Adın kimlik hâli: boşlukları sadeleştirilmiş ve Türkçe kurallarına göre
    // küçük harfe indirilmiş sürüm ("Üst Katman" ve "üst katman" aynı kayıt,
    // "Ust katman" ayrı). İstemcideki `recordId` ile aynı kural.
    //
    // Ayrı bir sütun çünkü teklik VERİTABANINDA zorlanır: yalnız uygulama
    // kodunda "önce ara, yoksa ekle" yapmak yarış altında kopya satır üretiyor
    // — aynı adla beş eşzamanlı istek beş satır açtı (ölçüldü). Benzersiz
    // dizin (UserId, NameKey) bunu imkânsız kılar.
    public string NameKey { get; set; } = string.Empty;

    public int SchemaVersion { get; set; }
    public string DataJson { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; }
}

using System.Security.Cryptography;
using System.Text;
using Alp.Data;
using Alp.Domain;
using Microsoft.EntityFrameworkCore;

namespace Alp.Api.Reports;

// Rapor anlık görüntüsünün yazılması, okunması ve kota bakımı.
// Karar ve elenen seçenekler: docs/rapor-snapshot-karari.md.
//
// Saklanan şey, raporun üretildiği andaki bölüm kayıtlarının HAM dizeleridir;
// belge baytları değil. Böylece indirme anında dil hâlâ seçilebilir ve dizgi
// düzeltmeleri geçmiş raporlara da uygulanır — donmuş olan İÇERİKTİR.
//
// Sunucu burada da hiçbir aracın ne hesapladığını bilmez: dizeler okunmadan,
// yalnızca özetlenip saklanır; okuma tarafında da tanınan tek şey
// `StoredSection`ın zaten bildiği bölüm şemasıdır.
internal static class ReportSnapshot
{
    // Aynı içerik aynı satırı paylaşsın diye SHA-256; çakışma riski, iki farklı
    // bölümün aynı belgeye girmesi anlamına gelirdi ve pratikte yoktur.
    // Maliyet: 25 KB'lık bir bölümde mikrosaniyeler — belge dizgisinin yanında
    // ölçülemez.
    public static string ComputeHash(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    // Manifest + eksik blob'lar. Bölüm sırası çağıranın verdiği sıradır ve
    // rapora DONAR: proje sonradan yeniden sıralansa bile bu rapor o günkü
    // düzeni korur.
    public static async Task WriteAsync(
        AppDbContext db, string userId, Guid reportId, IReadOnlyList<string> rawSections,
        CancellationToken ct = default)
    {
        if (rawSections.Count == 0) return;

        var hashes = new string[rawSections.Count];
        for (var i = 0; i < rawSections.Count; i++) hashes[i] = ComputeHash(rawSections[i]);

        var distinct = hashes.Distinct().ToArray();
        var existing = await db.SectionBlobs
            .Where(b => b.UserId == userId && distinct.Contains(b.Hash))
            .Select(b => b.Hash)
            .ToListAsync(ct);

        var missing = distinct.Except(existing).ToHashSet();
        if (missing.Count > 0)
        {
            var now = DateTimeOffset.UtcNow;
            for (var i = 0; i < rawSections.Count; i++)
            {
                if (!missing.Remove(hashes[i])) continue;
                db.SectionBlobs.Add(new SectionBlob
                {
                    UserId = userId,
                    Hash = hashes[i],
                    Content = rawSections[i],
                    Length = rawSections[i].Length,
                    CreatedAt = now,
                });
            }

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Aynı kullanıcının iki raporu aynı anda AYNI yeni bölümü
                // yazmaya çalışırsa ikinci ekleme birincil anahtardan döner.
                // Yarış zararsızdır — içerik zaten aynıdır, tek yapılacak şey
                // eklemeyi bırakmaktır. Manifest aşağıda yine yazılır.
                foreach (var entry in db.ChangeTracker.Entries<SectionBlob>().ToList())
                {
                    entry.State = EntityState.Detached;
                }
            }
        }

        for (var i = 0; i < rawSections.Count; i++)
        {
            db.ReportSnapshotSections.Add(new ReportSnapshotSection
            {
                ReportId = reportId,
                UserId = userId,
                Hash = hashes[i],
                SortOrder = i,
            });
        }

        await db.SaveChangesAsync(ct);
    }

    // Raporun donmuş bölümleri, üretim sırasında. Boş liste "bu rapor
    // snapshot'sız" demektir ve çağıran eski davranışa (projeden yeniden
    // üretim) düşer.
    public static Task<List<string>> ReadRawAsync(AppDbContext db, Guid reportId, CancellationToken ct = default) =>
        db.ReportSnapshotSections
            .Where(s => s.ReportId == reportId)
            .OrderBy(s => s.SortOrder)
            .Join(db.SectionBlobs,
                s => new { s.UserId, s.Hash },
                b => new { b.UserId, b.Hash },
                (s, b) => new { s.SortOrder, b.Content })
            .OrderBy(x => x.SortOrder)
            .Select(x => x.Content)
            .ToListAsync(ct);

    // Kota aşıldığında rapor REDDEDİLMEZ: en eski snapshot'lar düşürülür ve o
    // raporlar "güncelden üret" davranışına geriler (karar §2). Kütük satırı
    // hiç silinmez.
    //
    // En yeni snapshot'lı rapor asla düşürülmez — tek başına kotayı aşan bir
    // rapor varsa döngü onu bırakıp durur, yoksa az önce üretilen belge daha
    // kaydedilmeden geri alınırdı.
    public static async Task EnforceQuotaAsync(
        AppDbContext db, string userId, long quotaBytes, CancellationToken ct = default)
    {
        var total = await TotalBytesAsync(db, userId, ct);
        if (total <= quotaBytes) return;

        // Sıralama BELLEKTE yapılır: SQLite `DateTimeOffset` üzerinde ORDER BY
        // desteklemiyor ve testler o sağlayıcıyı kullanıyor (bkz. TestHost).
        // Liste kotayla sınırlı olduğu için taşınan satır sayısı küçüktür.
        var candidates = (await db.Reports
                .Where(r => r.UserId == userId && r.SnapshotSections.Count > 0)
                .Select(r => new { r.Id, r.GeneratedAt })
                .ToListAsync(ct))
            .OrderBy(r => r.GeneratedAt)
            .Select(r => r.Id)
            .ToList();

        for (var i = 0; i < candidates.Count - 1 && total > quotaBytes; i++)
        {
            await db.ReportSnapshotSections.Where(s => s.ReportId == candidates[i]).ExecuteDeleteAsync(ct);
            // Boşalan yer ancak sahipsiz blob'lar toplandığında geri gelir;
            // toplama arka plan turuna bırakılsaydı bu döngü aynı kotayı
            // defalarca aşılmış görüp bütün geçmişi düşürürdü.
            total -= await CollectOrphansAsync(db, userId, ct);
        }
    }

    // Hiçbir manifestin göstermediği blob'lar. Referans SAYACI tutulmaz:
    // sayaç ikinci bir doğruluk kaynağı ve sessizce kayabilecek bir sayı
    // olurdu; manifest gerçek bir join tablosu olduğu için anti-join yeter.
    // Dönen değer boşalan bayt sayısıdır.
    public static async Task<long> CollectOrphansAsync(
        AppDbContext db, string userId, CancellationToken ct = default)
    {
        var orphans = db.SectionBlobs
            .Where(b => b.UserId == userId
                && !db.ReportSnapshotSections.Any(s => s.UserId == b.UserId && s.Hash == b.Hash));

        var freed = await orphans.SumAsync(b => (long)b.Length, ct);
        if (freed > 0) await orphans.ExecuteDeleteAsync(ct);
        return freed;
    }

    public static Task<long> TotalBytesAsync(AppDbContext db, string userId, CancellationToken ct = default) =>
        db.SectionBlobs.Where(b => b.UserId == userId).SumAsync(b => (long)b.Length, ct);
}

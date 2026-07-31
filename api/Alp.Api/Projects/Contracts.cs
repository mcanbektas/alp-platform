using Alp.Reports;
namespace Alp.Api.Projects;

// Proje özeti — liste ve oluşturma/güncelleme yanıtlarında aynı şekil kullanılır.
public record ProjectSummary(
    Guid Id,
    string Name,
    string? Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int CalculationCount
);

public record ProjectListResponse(IReadOnlyList<ProjectSummary> Projects);

public record CreateProjectRequest(string Name, string? Description);

// `Name`/`Description` null ya da atlanmışsa değişmez; `Description` boş dize
// olarak GÖNDERİLMİŞSE alan temizlenir (null'a döner). `Name` gönderilmişse
// trim sonrası boş olamaz.
public record UpdateProjectRequest(string? Name, string? Description);

// InputsJson/ResultJson sunucu için opak dizedir — burada asla
// ayrıştırılmaz/yeniden gömülmez. Sunucu hiçbir aracın içeriğini bilmez.
//
// `ReportJson` yanıtlarda HİÇ dönmez (yazma yönünde hâlâ kabul edilir): içinde
// satır içi SVG taşır ve proje detayı yanıtının ~%92'sini o kaplıyordu
// (60 hesapta 955 KB). Ekranın ondan ihtiyaç duyduğu tek şey birkaç önizleme
// satırıydı; o satırlar artık sunucuda türetilip `Preview` alanında geliyor.
public record CalculationDto(
    Guid Id,
    string ToolKey,
    string? ToolMode,
    int SortOrder,
    string InputsJson,
    string ResultJson,
    string EngineVersion,
    int SchemaVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

// Rapor bölümünden türetilmiş tek önizleme satırı. Yalnızca `results`
// dizisinin etiket/değer/birim alanları taşınır; SVG alanlarına dokunulmaz,
// dolayısıyla buradan ekrana geri yazılabilecek bir işaretleme çıkmaz.
public record PreviewField(string Label, string Value, string? Unit, bool Emphasis);

// Proje detayındaki hesap satırı. Liste yanıtında N tane döndüğü için yalnız
// satırın GÖSTERDİĞİ alanları taşır: ham `InputsJson`/`ResultJson` da dışarıda
// kalır — onları yalnızca tekil hesap ucu (araç ekranına geri yükleme) verir.
//
// `HasReport`: satırda "rapor bölümü yok" çipini bu belirler. Bölümün kendisi
// dönmediği için varlığı ayrı bir bayrak olarak taşınmak zorunda.
public record CalculationSummaryDto(
    Guid Id,
    string ToolKey,
    string? ToolMode,
    int SortOrder,
    IReadOnlyList<PreviewField> Preview,
    string? PreviewMode,
    bool HasReport,
    string EngineVersion,
    int SchemaVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

// Tekil hesap yanıtı. Araç ekranı kaydı geri yüklerken hangi projeye ait
// olduğunu da göstermek ister; ikinci bir proje isteği attırmamak için üst
// projenin kimliği ve adı yanıta katılır.
public record CalculationDetailResponse(
    CalculationDto Calculation,
    Guid ProjectId,
    string ProjectName
);

public record ProjectDetailResponse(
    Guid Id,
    string Name,
    string? Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<CalculationSummaryDto> Calculations
);

// Proje raporu isteği. Bölümler gövdede GELMEZ — sunucu onları kaydedilmiş
// hesaplardan kurar (bkz. ReportEndpoints, proje raporu ucu). Gövdede yalnızca
// belgenin kendi künyesi durur; firma adı kullanıcı kaydından okunur.
// `Labels`: belgenin çerçeve metni. Bölümleri sunucu kendi kaydından toplar ama
// başlıkları toplayamaz — sunucuda kullanıcı metni yoktur (§5.1). Karşılığı
// `web/src/data/reportText.js` → `reportLabels(lang)`.
// `Lang`: METİN DEĞİL, ANAHTAR. Kayıt bölümü her dilde taşıyor; sunucu hangi
// dalı okuyacağını buradan öğrenir. Kullanıcı metni yine sunucuya girmez.
// `Company`: belgenin künyesindeki firma adı. ÜÇ DURUM ayrı anlam taşır ve
// karıştırılmamalı:
//   - alan hiç gönderilmemiş (`null`) → kullanıcının PROFİLİNDEKİ firma yazılır
//     (eski istemciler ve profilin varsayılan davranışı);
//   - boş dize → o belgede firma YAZILMAZ (kullanıcı alanı bilerek boşalttı);
//   - dolu → tek seferlik olarak o değer yazılır, profil DEĞİŞMEZ.
// Boşaltmayı `null` saysaydık firmayı kaldırmak isteyen kullanıcı sessizce
// profildekini alırdı.
public record ProjectReportRequest(
    string Title, string PreparedBy, string? Company, string Date, ReportLabels Labels, string? Lang);

public record CreateCalculationRequest(
    string ToolKey,
    string? ToolMode,
    string InputsJson,
    string ResultJson,
    string? ReportJson,
    string EngineVersion,
    int SchemaVersion
);

// Sağlanan her alan TAM DEĞİŞİM'dir (birleştirme değil); null/atlanmış alan
// değişmeden kalır.
public record UpdateCalculationRequest(
    string? ToolMode,
    string? InputsJson,
    string? ResultJson,
    string? ReportJson,
    string? EngineVersion,
    int? SchemaVersion
);

public record ReorderCalculationsRequest(IReadOnlyList<Guid> OrderedIds);

public record OkResponse(bool Ok);

namespace Alp.Domain;

// Bir araç ekranındaki tek hesap. InputsJson + EngineVersion birlikte
// saklanır ki bir motorda hata bulunup düzeltilirse eski raporun hangi
// sürümle üretildiği bilinsin ve gerekirse yeniden hesaplanabilsin.
// docs/uyelik-ve-rapor-plani.md §4.2
public class Calculation
{
    // Bkz. Project'teki not: şema ve uç doğrulaması tek kaynaktan okur.
    public const int ToolKeyMaxLength = 100;
    public const int ToolModeMaxLength = 100;
    public const int EngineVersionMaxLength = 50;

    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Project? Project { get; set; }

    public string ToolKey { get; set; } = string.Empty;
    public string? ToolMode { get; set; }
    public int SortOrder { get; set; }

    public string InputsJson { get; set; } = "{}";
    public string ResultJson { get; set; } = "{}";
    public string? ReportJson { get; set; }

    // Yazma anında ReportJson'dan türetilen önizleme, dil başına — bkz.
    // Alp.Api/Projects/ReportPreview.cs (Write/ReadStored). GetProject artık
    // ReportJson'ın tamamını değil bu küçük kolonu okur; PreviewJson null olan
    // (göç etmemiş) satırlarda eski yola düşülür.
    public string? PreviewJson { get; set; }

    public string EngineVersion { get; set; } = string.Empty;
    public int SchemaVersion { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

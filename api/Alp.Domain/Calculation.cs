namespace Alp.Domain;

// Bir araç ekranındaki tek hesap. InputsJson + EngineVersion birlikte
// saklanır ki bir motorda hata bulunup düzeltilirse eski raporun hangi
// sürümle üretildiği bilinsin ve gerekirse yeniden hesaplanabilsin.
// docs/uyelik-ve-rapor-plani.md §4.2
public class Calculation
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Project? Project { get; set; }

    public string ToolKey { get; set; } = string.Empty;
    public string? ToolMode { get; set; }
    public int SortOrder { get; set; }

    public string InputsJson { get; set; } = "{}";
    public string ResultJson { get; set; } = "{}";
    public string? ReportJson { get; set; }

    public string EngineVersion { get; set; } = string.Empty;
    public int SchemaVersion { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

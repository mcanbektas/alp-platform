namespace Alp.Reports;

// docs/uyelik-ve-rapor-plani.md §5.1 — sunucu 25 aracın hiçbirini tanımaz.
// Tarayıcı biçimlenmiş, dilsiz-olmayan (zaten metne çevrilmiş) bu yükü kurar;
// sunucu yalnızca dizer. Yeni araç eklendiğinde bu dosyada hiçbir şey değişmez.

public record ReportPayload(
    int SchemaVersion,
    string Title,
    string PreparedBy,
    string? Company,
    string Date,
    IReadOnlyList<ReportSection> Sections
);

public record ReportSection(
    string ToolName,
    string? Mode,
    IReadOnlyList<ReportField> Inputs,
    IReadOnlyList<string> Formula,
    IReadOnlyList<ReportField> Results,
    IReadOnlyList<ReportNote> Notes,
    string? SchematicSvg,
    string? SchematicCaption,
    ReportChart? Chart
);

// `Value` sayısal olabilecek bir dizedir ("0.62"); `Unit` ayrı tutulur —
// Excel çıktısında sayı gerçek sayı hücresine, birim ayrı sütuna gider (§5.4).
// PDF'te ikisi tek satırda birleştirilir. `Value` sayıya çevrilemiyorsa
// (örn. "Dış katman") Excel'e metin olarak yazılır — bkz. XlsxReportBuilder.
public record ReportField(string Label, string Value, string? Unit = null, bool Emphasis = false);

// Level: "ok" | "warn" | "danger" — ekrandaki mühendislik yorumuyla (bkz.
// CLAUDE.md "Durum çipi tek kuralı") aynı üç seviye, aynı işaret ve renk.
public record ReportNote(string Level, string Text);

public record ReportChart(string? Title, string? Svg, ReportTable? Table);

public record ReportTable(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows);

using Alp.Api.Projects;
using Alp.Data;
using Microsoft.EntityFrameworkCore;

namespace Alp.Api.Tests;

// Önizlemenin hesap YAZILIRKEN türetilip PreviewJson kolonuna konması ve
// GetProject'in bu kolonu okuması — bkz. docs/brifler/01-onizleme-kolonu.md.
public class CalculationPreviewTests
{
    private const string ReportJson = "{\"results\":[{\"label\":\"Genişlik\",\"value\":\"0.62\",\"unit\":\"mm\",\"emphasis\":true}]}";

    [Fact]
    public async Task Hesap_olustururken_previewjson_turetilir()
    {
        using var host = new TestDb();
        var owner = host.AddUser("a@alp.local");
        var project = host.AddProject(owner);

        var result = await ProjectEndpoints.CreateCalculation(
            project.Id,
            new CreateCalculationRequest("trace-width", null, "{}", "{}", ReportJson, "test", 1),
            host.Db, TestHttp.For(owner));

        var dto = ResultAssert.Value<CalculationDto>(result);
        var row = await host.NewContext().Calculations.SingleAsync(c => c.Id == dto.Id);

        Assert.NotNull(row.PreviewJson);
        var (rows, _) = ReportPreview.ReadStored(row.PreviewJson, "tr");
        Assert.Equal("Genişlik", Assert.Single(rows).Label);
    }

    [Fact]
    public async Task ReportJson_verilmezse_previewjson_de_bos_kalir()
    {
        using var host = new TestDb();
        var owner = host.AddUser("a@alp.local");
        var project = host.AddProject(owner);

        var result = await ProjectEndpoints.CreateCalculation(
            project.Id,
            new CreateCalculationRequest("trace-width", null, "{}", "{}", null, "test", 1),
            host.Db, TestHttp.For(owner));

        var dto = ResultAssert.Value<CalculationDto>(result);
        var row = await host.NewContext().Calculations.SingleAsync(c => c.Id == dto.Id);

        Assert.Null(row.PreviewJson);
    }

    [Fact]
    public async Task Hesap_guncellenince_previewjson_yeniden_turetilir()
    {
        using var host = new TestDb();
        var owner = host.AddUser("a@alp.local");
        var calculation = host.AddCalculation(host.AddProject(owner));
        var yeniRapor = "{\"results\":[{\"label\":\"Akım\",\"value\":\"3\",\"unit\":\"A\",\"emphasis\":false}]}";

        await ProjectEndpoints.UpdateCalculation(
            calculation.Id,
            new UpdateCalculationRequest(null, null, null, yeniRapor, null, null),
            host.Db, TestHttp.For(owner));

        var row = await host.NewContext().Calculations.SingleAsync(c => c.Id == calculation.Id);
        var (rows, _) = ReportPreview.ReadStored(row.PreviewJson, "tr");
        Assert.Equal("Akım", Assert.Single(rows).Label);
    }

    [Fact]
    public async Task GetProject_previewjsonlu_satirda_reportjsonu_okumaz()
    {
        // PreviewJson elle, gerçek ReportJson'dan FARKLI bir etiketle kurulur.
        // GetProject yine de PreviewJson'daki (yanlış/farklı) etiketi dönerse
        // bu, ReportJson'ın hiç okunmadığının kanıtıdır — okunsaydı gerçek
        // etiket ("Gerçek") dönerdi.
        using var host = new TestDb();
        var owner = host.AddUser("a@alp.local");
        var project = host.AddProject(owner);
        var gercekRapor = "{\"results\":[{\"label\":\"Gerçek\",\"value\":\"1\",\"unit\":null,\"emphasis\":false}]}";
        var elleKurulanOnizleme = ReportPreview.Write(
            "{\"results\":[{\"label\":\"PreviewJson'dan\",\"value\":\"1\",\"unit\":null,\"emphasis\":false}]}");
        host.AddCalculation(project, reportJson: gercekRapor, previewJson: elleKurulanOnizleme);

        var result = await ProjectEndpoints.GetProject(project.Id, host.Db, TestHttp.For(owner));

        var detail = ResultAssert.Value<ProjectDetailResponse>(result);
        var preview = Assert.Single(Assert.Single(detail.Calculations).Preview);
        Assert.Equal("PreviewJson'dan", preview.Label);
    }

    [Fact]
    public async Task GetProject_eski_kayitta_previewjson_yoksa_reportjsona_duser()
    {
        // PreviewJson null (göç etmemiş eski satır) — okuma yolu pragmatik
        // geri düşüşle doğrudan ReportJson'dan türetir.
        using var host = new TestDb();
        var owner = host.AddUser("a@alp.local");
        var project = host.AddProject(owner);
        host.AddCalculation(project, reportJson: ReportJson, previewJson: null);

        var result = await ProjectEndpoints.GetProject(project.Id, host.Db, TestHttp.For(owner));

        var detail = ResultAssert.Value<ProjectDetailResponse>(result);
        var preview = Assert.Single(Assert.Single(detail.Calculations).Preview);
        Assert.Equal("Genişlik", preview.Label);
    }

    [Fact]
    public async Task GetProject_dil_secimi_previewjson_haritasindan_gelir()
    {
        using var host = new TestDb();
        var owner = host.AddUser("a@alp.local");
        var project = host.AddProject(owner);
        var ikiDilliRapor = "{\"tr\":{\"results\":[{\"label\":\"Genişlik\",\"value\":\"1\",\"unit\":null,\"emphasis\":false}]}," +
                             "\"en\":{\"results\":[{\"label\":\"Width\",\"value\":\"1\",\"unit\":null,\"emphasis\":false}]}}";
        host.AddCalculation(project, reportJson: ikiDilliRapor, previewJson: ReportPreview.Write(ikiDilliRapor));

        var tr = ResultAssert.Value<ProjectDetailResponse>(
            await ProjectEndpoints.GetProject(project.Id, host.Db, TestHttp.For(owner)));
        var en = ResultAssert.Value<ProjectDetailResponse>(
            await ProjectEndpoints.GetProject(project.Id, host.Db, TestHttp.For(owner), "en"));

        Assert.Equal("Genişlik", Assert.Single(tr.Calculations[0].Preview).Label);
        Assert.Equal("Width", Assert.Single(en.Calculations[0].Preview).Label);
    }
}

using Alp.Api.Common;
using Alp.Api.Projects;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Alp.Api.Tests;

// Sahiplik: başkasının projesi/hesabı YOK gibi davranır.
//
// Kural iki katmanlı: proje kendi `UserId`si üzerinden, hesap ise üst projesi
// üzerinden doğrulanır (Calculation'ın kendi kullanıcısı yok). İkisi de "var ama
// senin değil" ile "hiç yok" arasında ayrım yapmaz — aynı 404 döner, yoksa yanıt
// başkasının kayıt kimliklerini numaralandırmaya yarardı.
public class OwnershipTests
{
    [Fact]
    public async Task Baskasinin_projesi_okunamaz()
    {
        using var host = new TestDb();
        var owner = host.AddUser("a@alp.local");
        var other = host.AddUser("b@alp.local");
        var project = host.AddProject(owner);

        var baskasi = await ProjectEndpoints.GetProject(project.Id, host.Db, TestHttp.For(other));
        var olmayan = await ProjectEndpoints.GetProject(Guid.NewGuid(), host.Db, TestHttp.For(other));

        Assert.Equal(StatusCodes.Status404NotFound, ResultAssert.Status(baskasi));
        Assert.Equal("PROJECT_NOT_FOUND", ResultAssert.Value<ApiError>(baskasi).Error);
        // İki durum birbirinden ayırt edilemez: aynı kod, aynı yük.
        Assert.Equal(ResultAssert.Status(olmayan), ResultAssert.Status(baskasi));
        Assert.Equal(
            ResultAssert.Value<ApiError>(olmayan).Error,
            ResultAssert.Value<ApiError>(baskasi).Error);
    }

    [Fact]
    public async Task Sahibi_projesini_okuyabilir()
    {
        using var host = new TestDb();
        var owner = host.AddUser("a@alp.local");
        var project = host.AddProject(owner, "Kart A");

        var result = await ProjectEndpoints.GetProject(project.Id, host.Db, TestHttp.For(owner));

        Assert.Equal(StatusCodes.Status200OK, ResultAssert.Status(result));
        Assert.Equal("Kart A", ResultAssert.Value<ProjectDetailResponse>(result).Name);
    }

    [Fact]
    public async Task Baskasinin_projesi_guncellenemez_ve_veri_degismez()
    {
        using var host = new TestDb();
        var owner = host.AddUser("a@alp.local");
        var other = host.AddUser("b@alp.local");
        var project = host.AddProject(owner, "Kart A");

        var result = await ProjectEndpoints.UpdateProject(
            project.Id, new UpdateProjectRequest("Ele geçirildi", null), host.Db, TestHttp.For(other));

        Assert.Equal(StatusCodes.Status404NotFound, ResultAssert.Status(result));
        Assert.Equal("Kart A", (await host.NewContext().Projects.SingleAsync()).Name);
    }

    [Fact]
    public async Task Baskasinin_projesi_silinemez_ve_satir_kalir()
    {
        using var host = new TestDb();
        var owner = host.AddUser("a@alp.local");
        var other = host.AddUser("b@alp.local");
        var project = host.AddProject(owner);

        var result = await ProjectEndpoints.DeleteProject(project.Id, host.Db, TestHttp.For(other));

        Assert.Equal(StatusCodes.Status404NotFound, ResultAssert.Status(result));
        Assert.Equal(1, await host.NewContext().Projects.CountAsync());
    }

    [Fact]
    public async Task Baskasinin_projesine_hesap_eklenemez()
    {
        using var host = new TestDb();
        var owner = host.AddUser("a@alp.local");
        var other = host.AddUser("b@alp.local");
        var project = host.AddProject(owner);

        var result = await ProjectEndpoints.CreateCalculation(
            project.Id,
            new CreateCalculationRequest("trace-width", null, "{}", "{}", null, "test", 1),
            host.Db, TestHttp.For(other));

        Assert.Equal(StatusCodes.Status404NotFound, ResultAssert.Status(result));
        Assert.Equal(0, await host.NewContext().Calculations.CountAsync());
    }

    [Fact]
    public async Task Baskasinin_hesabi_okunamaz()
    {
        using var host = new TestDb();
        var owner = host.AddUser("a@alp.local");
        var other = host.AddUser("b@alp.local");
        var calculation = host.AddCalculation(host.AddProject(owner));

        var baskasi = await ProjectEndpoints.GetCalculation(calculation.Id, host.Db, TestHttp.For(other));
        var olmayan = await ProjectEndpoints.GetCalculation(Guid.NewGuid(), host.Db, TestHttp.For(other));

        Assert.Equal(StatusCodes.Status404NotFound, ResultAssert.Status(baskasi));
        Assert.Equal("CALCULATION_NOT_FOUND", ResultAssert.Value<ApiError>(baskasi).Error);
        Assert.Equal(
            ResultAssert.Value<ApiError>(olmayan).Error,
            ResultAssert.Value<ApiError>(baskasi).Error);
    }

    [Fact]
    public async Task Baskasinin_hesabi_guncellenemez()
    {
        using var host = new TestDb();
        var owner = host.AddUser("a@alp.local");
        var other = host.AddUser("b@alp.local");
        var calculation = host.AddCalculation(host.AddProject(owner));

        var result = await ProjectEndpoints.UpdateCalculation(
            calculation.Id,
            new UpdateCalculationRequest(null, "{\"ele\":\"geçirildi\"}", null, null, null, null),
            host.Db, TestHttp.For(other));

        Assert.Equal(StatusCodes.Status404NotFound, ResultAssert.Status(result));
        Assert.Equal("{}", (await host.NewContext().Calculations.SingleAsync()).InputsJson);
    }

    [Fact]
    public async Task Baskasinin_hesabi_silinemez()
    {
        using var host = new TestDb();
        var owner = host.AddUser("a@alp.local");
        var other = host.AddUser("b@alp.local");
        var calculation = host.AddCalculation(host.AddProject(owner));

        var result = await ProjectEndpoints.DeleteCalculation(calculation.Id, host.Db, TestHttp.For(other));

        Assert.Equal(StatusCodes.Status404NotFound, ResultAssert.Status(result));
        Assert.Equal(1, await host.NewContext().Calculations.CountAsync());
    }

    [Fact]
    public async Task Sahibi_hesabini_yonetebilir()
    {
        using var host = new TestDb();
        var owner = host.AddUser("a@alp.local");
        var http = TestHttp.For(owner);
        var calculation = host.AddCalculation(host.AddProject(owner));

        var okundu = await ProjectEndpoints.GetCalculation(calculation.Id, host.Db, http);
        var silindi = await ProjectEndpoints.DeleteCalculation(calculation.Id, host.Db, http);

        Assert.Equal(StatusCodes.Status200OK, ResultAssert.Status(okundu));
        Assert.Equal(StatusCodes.Status204NoContent, ResultAssert.Status(silindi));
        Assert.Equal(0, await host.NewContext().Calculations.CountAsync());
    }

    [Fact]
    public async Task Oturumsuz_istek_401_verir()
    {
        using var host = new TestDb();

        Assert.Equal(StatusCodes.Status401Unauthorized,
            ResultAssert.Status(await ProjectEndpoints.GetProject(Guid.NewGuid(), host.Db, TestHttp.Anonymous())));
        Assert.Equal(StatusCodes.Status401Unauthorized,
            ResultAssert.Status(await ProjectEndpoints.DeleteProject(Guid.NewGuid(), host.Db, TestHttp.Anonymous())));
        Assert.Equal(StatusCodes.Status401Unauthorized,
            ResultAssert.Status(await ProjectEndpoints.GetCalculation(Guid.NewGuid(), host.Db, TestHttp.Anonymous())));
    }

    [Fact]
    public async Task Proje_detayi_rapor_bolumu_gondermez()
    {
        // Projeksiyon kararı (60 hesapta 906 KB → 26,5 KB): yanıt `ReportJson`
        // taşımaz, yalnız ondan türetilen önizleme satırlarını taşır. Satır içi
        // SVG sunucuda kalır.
        using var host = new TestDb();
        var owner = host.AddUser("a@alp.local");
        var project = host.AddProject(owner);
        var svg = $"<svg viewBox=\"0 0 10 10\">{new string('x', 4000)}</svg>";
        host.AddCalculation(project, reportJson:
            $"{{\"schematicSvg\":\"{svg.Replace("\"", "\\\"")}\",\"results\":[{{\"label\":\"Genişlik\",\"value\":\"0.62\",\"unit\":\"mm\",\"emphasis\":true}}]}}");

        var result = await ProjectEndpoints.GetProject(project.Id, host.Db, TestHttp.For(owner));

        var detail = ResultAssert.Value<ProjectDetailResponse>(result);
        var calculation = Assert.Single(detail.Calculations);
        Assert.True(calculation.HasReport);
        var preview = Assert.Single(calculation.Preview);
        Assert.Equal("Genişlik", preview.Label);
        // Yanıt nesnesinde rapor bölümünü taşıyan bir alan yok; olsaydı burada
        // 4 KB'lık SVG dizesi görünürdü.
        Assert.DoesNotContain("<svg", System.Text.Json.JsonSerializer.Serialize(detail));
    }
}

using Alp.Api.Comm;
using Alp.Api.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Alp.Api.Tests;

// CommEndpoints'in doğrulama ve iş kuralları — ProjectEndpoints'teki
// CreateCalculation/UpdateCalculation ile aynı desenler: zorunlu alan, uzunluk
// üst sınırı, geçersiz JSON, artı Comm'a özgü kural (proje içinde ad+sürüm
// tekliği).
public class CommEndpointsTests
{
    [Fact]
    public async Task Gecerli_sema_olusturulur_ve_proje_guncellenir_zamani_ilerler()
    {
        using var host = new TestDb();
        var owner = host.AddUser("a@alp.local");
        var project = host.AddCommProject(owner);
        var eskiGuncelleme = project.UpdatedAt;

        var result = await CommEndpoints.CreateProtocolSchema(
            project.Id, new CreateProtocolSchemaRequest("i2c", "1.0", """{"fields":[]}"""), host.Db, TestHttp.For(owner));

        Assert.Equal(StatusCodes.Status201Created, ResultAssert.Status(result));
        var dto = ResultAssert.Value<ProtocolSchemaDto>(result);
        Assert.Equal("i2c", dto.Name);
        Assert.Equal("1.0", dto.Version);

        var guncelProje = await host.NewContext().CommProjects.SingleAsync();
        Assert.True(guncelProje.UpdatedAt >= eskiGuncelleme);
    }

    [Fact]
    public async Task Ad_veya_surum_bos_ise_MISSING_FIELDS_doner()
    {
        using var host = new TestDb();
        var owner = host.AddUser("a@alp.local");
        var project = host.AddCommProject(owner);

        var result = await CommEndpoints.CreateProtocolSchema(
            project.Id, new CreateProtocolSchemaRequest("  ", "1.0", "{}"), host.Db, TestHttp.For(owner));

        Assert.Equal(StatusCodes.Status400BadRequest, ResultAssert.Status(result));
        Assert.Equal("MISSING_FIELDS", ResultAssert.Value<ApiError>(result).Error);
    }

    [Fact]
    public async Task Gecersiz_json_INVALID_JSON_doner()
    {
        using var host = new TestDb();
        var owner = host.AddUser("a@alp.local");
        var project = host.AddCommProject(owner);

        var result = await CommEndpoints.CreateProtocolSchema(
            project.Id, new CreateProtocolSchemaRequest("i2c", "1.0", "{bozuk"), host.Db, TestHttp.For(owner));

        Assert.Equal(StatusCodes.Status400BadRequest, ResultAssert.Status(result));
        Assert.Equal("INVALID_JSON", ResultAssert.Value<ApiError>(result).Error);
    }

    [Fact]
    public async Task Uzun_ad_TOO_LONG_doner()
    {
        using var host = new TestDb();
        var owner = host.AddUser("a@alp.local");
        var project = host.AddCommProject(owner);
        var uzunAd = new string('x', 201);

        var result = await CommEndpoints.CreateProtocolSchema(
            project.Id, new CreateProtocolSchemaRequest(uzunAd, "1.0", "{}"), host.Db, TestHttp.For(owner));

        Assert.Equal(StatusCodes.Status400BadRequest, ResultAssert.Status(result));
        Assert.Equal("TOO_LONG", ResultAssert.Value<ApiError>(result).Error);
    }

    // Aynı projede aynı ad+sürüm ikinci kez oluşturulamaz (AppDbContext
    // benzersiz dizini) — uç DbUpdateException'a düşmeden önden yakalar.
    [Fact]
    public async Task Ayni_ad_ve_surum_ikinci_kez_olusturulamaz()
    {
        using var host = new TestDb();
        var owner = host.AddUser("a@alp.local");
        var project = host.AddCommProject(owner);
        host.AddProtocolSchema(project, name: "i2c", version: "1.0");

        var result = await CommEndpoints.CreateProtocolSchema(
            project.Id, new CreateProtocolSchemaRequest("i2c", "1.0", "{}"), host.Db, TestHttp.For(owner));

        Assert.Equal(StatusCodes.Status400BadRequest, ResultAssert.Status(result));
        Assert.Equal("SCHEMA_VERSION_EXISTS", ResultAssert.Value<ApiError>(result).Error);
        Assert.Equal(1, await host.NewContext().ProtocolSchemas.CountAsync());
    }

    [Fact]
    public async Task Sema_guncellenirken_ayni_projede_baska_semayla_cakisan_ad_surum_reddedilir()
    {
        using var host = new TestDb();
        var owner = host.AddUser("a@alp.local");
        var project = host.AddCommProject(owner);
        host.AddProtocolSchema(project, name: "i2c", version: "1.0");
        var digeri = host.AddProtocolSchema(project, name: "spi", version: "1.0");

        var result = await CommEndpoints.UpdateProtocolSchema(
            digeri.Id, new UpdateProtocolSchemaRequest("i2c", null, null), host.Db, TestHttp.For(owner));

        Assert.Equal(StatusCodes.Status400BadRequest, ResultAssert.Status(result));
        Assert.Equal("SCHEMA_VERSION_EXISTS", ResultAssert.Value<ApiError>(result).Error);
    }

    [Fact]
    public async Task Proje_silinince_semalari_da_gider()
    {
        using var host = new TestDb();
        var owner = host.AddUser("a@alp.local");
        var project = host.AddCommProject(owner);
        host.AddProtocolSchema(project);
        host.AddProtocolSchema(project, name: "ikinci");

        var result = await CommEndpoints.DeleteCommProject(project.Id, host.Db, TestHttp.For(owner));

        Assert.Equal(StatusCodes.Status204NoContent, ResultAssert.Status(result));
        Assert.Equal(0, await host.NewContext().ProtocolSchemas.CountAsync());
    }

    [Fact]
    public async Task Proje_detayi_sema_ozetlerini_dondurur()
    {
        using var host = new TestDb();
        var owner = host.AddUser("a@alp.local");
        var project = host.AddCommProject(owner, "Kart A");
        host.AddProtocolSchema(project, name: "i2c", version: "1.0");
        host.AddProtocolSchema(project, name: "spi", version: "1.0");

        var result = await CommEndpoints.GetCommProject(project.Id, host.Db, TestHttp.For(owner));

        var detail = ResultAssert.Value<CommProjectDetailResponse>(result);
        Assert.Equal(2, detail.Schemas.Count);
    }

    [Fact]
    public async Task Aciklama_bos_dize_gonderilince_temizlenir()
    {
        using var host = new TestDb();
        var owner = host.AddUser("a@alp.local");
        var project = host.AddCommProject(owner);
        project.Description = "eski";
        host.Db.SaveChanges();

        var result = await CommEndpoints.UpdateCommProject(
            project.Id, new UpdateCommProjectRequest(null, ""), host.Db, TestHttp.For(owner));

        Assert.Equal(StatusCodes.Status200OK, ResultAssert.Status(result));
        Assert.Null((await host.NewContext().CommProjects.SingleAsync()).Description);
    }
}

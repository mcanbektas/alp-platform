using Alp.Api.Comm;
using Alp.Api.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Alp.Api.Tests;

// Sahiplik: başkasının Comm projesi/şeması YOK gibi davranır — OwnershipTests
// (Projects) ile birebir aynı kural, ayrı modül: CommProject kendi UserId'si
// üzerinden, ProtocolSchema ise üst projesi üzerinden doğrulanır.
public class CommOwnershipTests
{
    [Fact]
    public async Task Baskasinin_comm_projesi_okunamaz()
    {
        using var host = new TestDb();
        var owner = host.AddUser("a@alp.local");
        var other = host.AddUser("b@alp.local");
        var project = host.AddCommProject(owner);

        var baskasi = await CommEndpoints.GetCommProject(project.Id, host.Db, TestHttp.For(other));
        var olmayan = await CommEndpoints.GetCommProject(Guid.NewGuid(), host.Db, TestHttp.For(other));

        Assert.Equal(StatusCodes.Status404NotFound, ResultAssert.Status(baskasi));
        Assert.Equal("COMM_PROJECT_NOT_FOUND", ResultAssert.Value<ApiError>(baskasi).Error);
        Assert.Equal(ResultAssert.Status(olmayan), ResultAssert.Status(baskasi));
        Assert.Equal(
            ResultAssert.Value<ApiError>(olmayan).Error,
            ResultAssert.Value<ApiError>(baskasi).Error);
    }

    [Fact]
    public async Task Sahibi_comm_projesini_okuyabilir()
    {
        using var host = new TestDb();
        var owner = host.AddUser("a@alp.local");
        var project = host.AddCommProject(owner, "Kart A");

        var result = await CommEndpoints.GetCommProject(project.Id, host.Db, TestHttp.For(owner));

        Assert.Equal(StatusCodes.Status200OK, ResultAssert.Status(result));
        Assert.Equal("Kart A", ResultAssert.Value<CommProjectDetailResponse>(result).Name);
    }

    [Fact]
    public async Task Baskasinin_comm_projesi_guncellenemez_ve_veri_degismez()
    {
        using var host = new TestDb();
        var owner = host.AddUser("a@alp.local");
        var other = host.AddUser("b@alp.local");
        var project = host.AddCommProject(owner, "Kart A");

        var result = await CommEndpoints.UpdateCommProject(
            project.Id, new UpdateCommProjectRequest("Ele geçirildi", null), host.Db, TestHttp.For(other));

        Assert.Equal(StatusCodes.Status404NotFound, ResultAssert.Status(result));
        Assert.Equal("Kart A", (await host.NewContext().CommProjects.SingleAsync()).Name);
    }

    [Fact]
    public async Task Baskasinin_comm_projesi_silinemez_ve_satir_kalir()
    {
        using var host = new TestDb();
        var owner = host.AddUser("a@alp.local");
        var other = host.AddUser("b@alp.local");
        var project = host.AddCommProject(owner);

        var result = await CommEndpoints.DeleteCommProject(project.Id, host.Db, TestHttp.For(other));

        Assert.Equal(StatusCodes.Status404NotFound, ResultAssert.Status(result));
        Assert.Equal(1, await host.NewContext().CommProjects.CountAsync());
    }

    [Fact]
    public async Task Baskasinin_projesine_sema_eklenemez()
    {
        using var host = new TestDb();
        var owner = host.AddUser("a@alp.local");
        var other = host.AddUser("b@alp.local");
        var project = host.AddCommProject(owner);

        var result = await CommEndpoints.CreateProtocolSchema(
            project.Id, new CreateProtocolSchemaRequest("proto", "1.0", "{}"), host.Db, TestHttp.For(other));

        Assert.Equal(StatusCodes.Status404NotFound, ResultAssert.Status(result));
        Assert.Equal(0, await host.NewContext().ProtocolSchemas.CountAsync());
    }

    [Fact]
    public async Task Baskasinin_semasi_okunamaz()
    {
        using var host = new TestDb();
        var owner = host.AddUser("a@alp.local");
        var other = host.AddUser("b@alp.local");
        var schema = host.AddProtocolSchema(host.AddCommProject(owner));

        var baskasi = await CommEndpoints.GetProtocolSchema(schema.Id, host.Db, TestHttp.For(other));
        var olmayan = await CommEndpoints.GetProtocolSchema(Guid.NewGuid(), host.Db, TestHttp.For(other));

        Assert.Equal(StatusCodes.Status404NotFound, ResultAssert.Status(baskasi));
        Assert.Equal("SCHEMA_NOT_FOUND", ResultAssert.Value<ApiError>(baskasi).Error);
        Assert.Equal(
            ResultAssert.Value<ApiError>(olmayan).Error,
            ResultAssert.Value<ApiError>(baskasi).Error);
    }

    [Fact]
    public async Task Baskasinin_semasi_guncellenemez()
    {
        using var host = new TestDb();
        var owner = host.AddUser("a@alp.local");
        var other = host.AddUser("b@alp.local");
        var schema = host.AddProtocolSchema(host.AddCommProject(owner));

        var result = await CommEndpoints.UpdateProtocolSchema(
            schema.Id, new UpdateProtocolSchemaRequest(null, null, "{\"ele\":\"gecirildi\"}"), host.Db, TestHttp.For(other));

        Assert.Equal(StatusCodes.Status404NotFound, ResultAssert.Status(result));
        Assert.Equal("{}", (await host.NewContext().ProtocolSchemas.SingleAsync()).DefinitionJson);
    }

    [Fact]
    public async Task Baskasinin_semasi_silinemez()
    {
        using var host = new TestDb();
        var owner = host.AddUser("a@alp.local");
        var other = host.AddUser("b@alp.local");
        var schema = host.AddProtocolSchema(host.AddCommProject(owner));

        var result = await CommEndpoints.DeleteProtocolSchema(schema.Id, host.Db, TestHttp.For(other));

        Assert.Equal(StatusCodes.Status404NotFound, ResultAssert.Status(result));
        Assert.Equal(1, await host.NewContext().ProtocolSchemas.CountAsync());
    }

    [Fact]
    public async Task Sahibi_semasini_yonetebilir()
    {
        using var host = new TestDb();
        var owner = host.AddUser("a@alp.local");
        var http = TestHttp.For(owner);
        var schema = host.AddProtocolSchema(host.AddCommProject(owner));

        var okundu = await CommEndpoints.GetProtocolSchema(schema.Id, host.Db, http);
        var silindi = await CommEndpoints.DeleteProtocolSchema(schema.Id, host.Db, http);

        Assert.Equal(StatusCodes.Status200OK, ResultAssert.Status(okundu));
        Assert.Equal(StatusCodes.Status204NoContent, ResultAssert.Status(silindi));
        Assert.Equal(0, await host.NewContext().ProtocolSchemas.CountAsync());
    }

    [Fact]
    public async Task Oturumsuz_istek_401_verir()
    {
        using var host = new TestDb();

        Assert.Equal(StatusCodes.Status401Unauthorized,
            ResultAssert.Status(await CommEndpoints.GetCommProject(Guid.NewGuid(), host.Db, TestHttp.Anonymous())));
        Assert.Equal(StatusCodes.Status401Unauthorized,
            ResultAssert.Status(await CommEndpoints.DeleteCommProject(Guid.NewGuid(), host.Db, TestHttp.Anonymous())));
        Assert.Equal(StatusCodes.Status401Unauthorized,
            ResultAssert.Status(await CommEndpoints.GetProtocolSchema(Guid.NewGuid(), host.Db, TestHttp.Anonymous())));
    }
}

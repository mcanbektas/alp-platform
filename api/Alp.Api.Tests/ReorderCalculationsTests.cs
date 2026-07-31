using Alp.Api.Common;
using Alp.Api.Projects;
using Microsoft.AspNetCore.Http;

namespace Alp.Api.Tests;

// Sıralamanın küme-eşitliği kontrolü bir GÜVENLİK kuralıdır: verilen kimlik
// kümesi projenin mevcut hesap kümesiyle tam eşleşmeli — yabancı bir projenin
// hesap kimliği sızamaz, satır sessizce düşürülemez. Sahiplik başka her uçta
// test ediliyordu, bu kural değildi.
public class ReorderCalculationsTests
{
    [Fact]
    public async Task Gecerli_siralama_SortOrder_yazar()
    {
        using var db = new TestDb();
        var user = db.AddUser("sahip@test.local");
        var project = db.AddProject(user);
        var c1 = db.AddCalculation(project);
        var c2 = db.AddCalculation(project);

        var result = await ProjectEndpoints.ReorderCalculations(
            project.Id, new ReorderCalculationsRequest([c2.Id, c1.Id]), db.NewContext(), TestHttp.For(user));

        Assert.Equal(StatusCodes.Status200OK, ResultAssert.Status(result));
        var fresh = db.NewContext();
        Assert.Equal(0, fresh.Calculations.Single(c => c.Id == c2.Id).SortOrder);
        Assert.Equal(1, fresh.Calculations.Single(c => c.Id == c1.Id).SortOrder);
    }

    [Fact]
    public async Task Yabanci_hesap_kimligi_INVALID_ORDER()
    {
        using var db = new TestDb();
        var user = db.AddUser("sahip@test.local");
        var project = db.AddProject(user);
        var mine = db.AddCalculation(project);
        // Aynı kullanıcının BAŞKA projesindeki hesap bile sızamaz — kontrol
        // proje sınırındadır, kullanıcı sınırında değil.
        var otherProject = db.AddProject(user, "Öteki");
        var foreign = db.AddCalculation(otherProject);

        var result = await ProjectEndpoints.ReorderCalculations(
            project.Id, new ReorderCalculationsRequest([mine.Id, foreign.Id]), db.NewContext(), TestHttp.For(user));

        Assert.Equal(StatusCodes.Status400BadRequest, ResultAssert.Status(result));
        Assert.Equal("INVALID_ORDER", ResultAssert.Value<ApiError>(result).Error);
    }

    [Fact]
    public async Task Eksik_veya_tekrarli_kimlik_INVALID_ORDER()
    {
        using var db = new TestDb();
        var user = db.AddUser("sahip@test.local");
        var project = db.AddProject(user);
        var c1 = db.AddCalculation(project);
        var c2 = db.AddCalculation(project);

        var missing = await ProjectEndpoints.ReorderCalculations(
            project.Id, new ReorderCalculationsRequest([c1.Id]), db.NewContext(), TestHttp.For(user));
        var duplicated = await ProjectEndpoints.ReorderCalculations(
            project.Id, new ReorderCalculationsRequest([c1.Id, c1.Id]), db.NewContext(), TestHttp.For(user));

        Assert.Equal(StatusCodes.Status400BadRequest, ResultAssert.Status(missing));
        Assert.Equal(StatusCodes.Status400BadRequest, ResultAssert.Status(duplicated));
        // Sıra değişmemiş olmalı — c2 hâlâ eklendiği yerde.
        Assert.Equal(0, db.NewContext().Calculations.Single(c => c.Id == c2.Id).SortOrder);
    }

    [Fact]
    public async Task Baskasinin_projesi_ayni_404()
    {
        using var db = new TestDb();
        var owner = db.AddUser("sahip@test.local");
        var attacker = db.AddUser("saldirgan@test.local");
        var project = db.AddProject(owner);
        var calc = db.AddCalculation(project);

        var result = await ProjectEndpoints.ReorderCalculations(
            project.Id, new ReorderCalculationsRequest([calc.Id]), db.NewContext(), TestHttp.For(attacker));

        Assert.Equal(StatusCodes.Status404NotFound, ResultAssert.Status(result));
    }
}

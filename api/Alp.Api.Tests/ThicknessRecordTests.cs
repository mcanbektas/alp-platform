using Alp.Api.Common;
using Alp.Api.Records;
using Alp.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Alp.Api.Tests;

// Kalınlık kayıtlarının üç kuralı: aynı ad = aynı kayıt, kullanıcı başına 50
// kayıt, sahiplik. Üçü de yalnız elle doğrulanmıştı.
public class ThicknessRecordTests
{
    private static SaveThicknessRecordRequest Req(string name, string dataJson = "{\"v\":1}") =>
        new(name, 1, dataJson);

    private static ThicknessRecordDto Dto(IResult result)
    {
        Assert.Equal(StatusCodes.Status200OK, ResultAssert.Status(result));
        return ResultAssert.Value<ThicknessRecordDto>(result);
    }

    [Fact]
    public async Task Ayni_ad_ikinci_kez_kaydedilince_uzerine_yazar()
    {
        using var host = new TestDb();
        var user = host.AddUser("a@alp.local");
        var http = TestHttp.For(user);

        var first = Dto(await ThicknessRecordEndpoints.SaveRecord(Req("Üst katman", "{\"v\":1}"), host.Db, http));
        var second = Dto(await ThicknessRecordEndpoints.SaveRecord(Req("Üst katman", "{\"v\":2}"), host.Db, http));

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("{\"v\":2}", second.DataJson);
        Assert.Equal(1, await host.NewContext().ThicknessRecords.CountAsync());
    }

    [Theory]
    // Türkçe kurallarına göre küçük harf: büyük İ/I ayrımı korunur.
    [InlineData("Üst Katman", "üst katman", true)]
    [InlineData("İç Katman", "iç katman", true)]
    // Boşluk sadeleştirmesi de kimliğin parçası.
    [InlineData("Üst   katman", " Üst katman ", true)]
    // ASCII'ye indirgeme YOK: "Ust" ile "Üst" ayrı kayıttır.
    [InlineData("Üst katman", "Ust katman", false)]
    [InlineData("İç katman", "Ic katman", false)]
    public async Task Ad_kimligi_turkce_kurallarina_gore_kurulur(string first, string second, bool ayniKayit)
    {
        using var host = new TestDb();
        var user = host.AddUser("a@alp.local");
        var http = TestHttp.For(user);

        var firstDto = Dto(await ThicknessRecordEndpoints.SaveRecord(Req(first), host.Db, http));
        var secondDto = Dto(await ThicknessRecordEndpoints.SaveRecord(Req(second), host.Db, http));

        Assert.Equal(ayniKayit, firstDto.Id == secondDto.Id);
        Assert.Equal(ayniKayit ? 1 : 2, await host.NewContext().ThicknessRecords.CountAsync());
    }

    [Fact]
    public async Task Kaydedilen_ad_kullanicinin_yazdigi_gibi_kalir()
    {
        // Kimlik küçük harfe iner ama GÖSTERİLEN ad kullanıcının verdiğidir.
        using var host = new TestDb();
        var user = host.AddUser("a@alp.local");

        var dto = Dto(await ThicknessRecordEndpoints.SaveRecord(
            Req("  Üst   Katman  "), host.Db, TestHttp.For(user)));

        Assert.Equal("Üst Katman", dto.Name);
    }

    [Fact]
    public async Task Elli_birinci_ad_reddedilir()
    {
        using var host = new TestDb();
        var user = host.AddUser("a@alp.local");
        var http = TestHttp.For(user);

        for (var i = 1; i <= 50; i++)
        {
            var ok = await ThicknessRecordEndpoints.SaveRecord(Req($"Kayıt {i}"), host.Db, http);
            Assert.Equal(StatusCodes.Status200OK, ResultAssert.Status(ok));
        }

        var result = await ThicknessRecordEndpoints.SaveRecord(Req("Kayıt 51"), host.Db, http);

        Assert.Equal(StatusCodes.Status409Conflict, ResultAssert.Status(result));
        Assert.Equal("RECORD_LIMIT", ResultAssert.Value<ApiError>(result).Error);
        Assert.Equal(50, await host.NewContext().ThicknessRecords.CountAsync());
    }

    [Fact]
    public async Task Sinirdayken_var_olan_kaydin_uzerine_yazilabilir()
    {
        // Sınır YENİ kayıt içindir. Doluyken bile kullanıcı elindeki bir kaydı
        // güncelleyebilmeli, yoksa liste dolduğunda ekran kilitlenirdi.
        using var host = new TestDb();
        var user = host.AddUser("a@alp.local");
        var http = TestHttp.For(user);

        for (var i = 1; i <= 50; i++)
        {
            await ThicknessRecordEndpoints.SaveRecord(Req($"Kayıt {i}"), host.Db, http);
        }

        var result = await ThicknessRecordEndpoints.SaveRecord(Req("Kayıt 7", "{\"v\":9}"), host.Db, http);

        Assert.Equal(StatusCodes.Status200OK, ResultAssert.Status(result));
        Assert.Equal("{\"v\":9}", ResultAssert.Value<ThicknessRecordDto>(result).DataJson);
        Assert.Equal(50, await host.NewContext().ThicknessRecords.CountAsync());
    }

    [Fact]
    public async Task Sinir_kullanici_basinadir()
    {
        using var host = new TestDb();
        var first = host.AddUser("a@alp.local");
        var second = host.AddUser("b@alp.local");

        for (var i = 1; i <= 50; i++)
        {
            await ThicknessRecordEndpoints.SaveRecord(Req($"Kayıt {i}"), host.Db, TestHttp.For(first));
        }

        var result = await ThicknessRecordEndpoints.SaveRecord(Req("Kayıt 1"), host.Db, TestHttp.For(second));

        Assert.Equal(StatusCodes.Status200OK, ResultAssert.Status(result));
    }

    [Fact]
    public async Task Ayni_ad_iki_kullanicida_ayri_kayittir()
    {
        using var host = new TestDb();
        var first = host.AddUser("a@alp.local");
        var second = host.AddUser("b@alp.local");

        var a = await ThicknessRecordEndpoints.SaveRecord(Req("Üst katman"), host.Db, TestHttp.For(first));
        var b = await ThicknessRecordEndpoints.SaveRecord(Req("Üst katman"), host.Db, TestHttp.For(second));

        Assert.NotEqual(
            ResultAssert.Value<ThicknessRecordDto>(a).Id,
            ResultAssert.Value<ThicknessRecordDto>(b).Id);
        Assert.Equal(2, await host.NewContext().ThicknessRecords.CountAsync());
    }

    [Theory]
    [InlineData("", "MISSING_FIELDS")]
    [InlineData("   ", "MISSING_FIELDS")]
    public async Task Bos_ad_reddedilir(string name, string code)
    {
        using var host = new TestDb();
        var user = host.AddUser("a@alp.local");

        var result = await ThicknessRecordEndpoints.SaveRecord(Req(name), host.Db, TestHttp.For(user));

        Assert.Equal(StatusCodes.Status400BadRequest, ResultAssert.Status(result));
        Assert.Equal(code, ResultAssert.Value<ApiError>(result).Error);
    }

    [Fact]
    public async Task Altmis_karakterden_uzun_ad_reddedilir()
    {
        using var host = new TestDb();
        var user = host.AddUser("a@alp.local");
        var http = TestHttp.For(user);

        var tam = await ThicknessRecordEndpoints.SaveRecord(Req(new string('a', 60)), host.Db, http);
        var uzun = await ThicknessRecordEndpoints.SaveRecord(Req(new string('b', 61)), host.Db, http);

        // Sınır dahildir: 60 geçer, 61 geçmez.
        Assert.Equal(StatusCodes.Status200OK, ResultAssert.Status(tam));
        Assert.Equal(StatusCodes.Status400BadRequest, ResultAssert.Status(uzun));
        Assert.Equal("TOO_LONG", ResultAssert.Value<ApiError>(uzun).Error);
    }

    [Fact]
    public async Task Bos_dataJson_reddedilir()
    {
        using var host = new TestDb();
        var user = host.AddUser("a@alp.local");

        var result = await ThicknessRecordEndpoints.SaveRecord(
            new SaveThicknessRecordRequest("Kayıt", 1, "  "), host.Db, TestHttp.For(user));

        Assert.Equal(StatusCodes.Status400BadRequest, ResultAssert.Status(result));
        Assert.Equal("MISSING_FIELDS", ResultAssert.Value<ApiError>(result).Error);
    }

    [Fact]
    public async Task Sifir_schemaVersion_reddedilir()
    {
        using var host = new TestDb();
        var user = host.AddUser("a@alp.local");

        var result = await ThicknessRecordEndpoints.SaveRecord(
            new SaveThicknessRecordRequest("Kayıt", 0, "{}"), host.Db, TestHttp.For(user));

        Assert.Equal(StatusCodes.Status400BadRequest, ResultAssert.Status(result));
    }

    [Fact]
    public async Task Baskasinin_kaydi_silinemez_ve_ayni_404u_verir()
    {
        using var host = new TestDb();
        var owner = host.AddUser("a@alp.local");
        var other = host.AddUser("b@alp.local");

        var saved = await ThicknessRecordEndpoints.SaveRecord(Req("Üst katman"), host.Db, TestHttp.For(owner));
        var id = ResultAssert.Value<ThicknessRecordDto>(saved).Id;

        var baskasi = await ThicknessRecordEndpoints.DeleteRecord(id, host.Db, TestHttp.For(other));
        var olmayan = await ThicknessRecordEndpoints.DeleteRecord(Guid.NewGuid(), host.Db, TestHttp.For(other));

        // Var-ama-senin-değil ile hiç-yok aynı yanıtı verir: numaralandırmaya kapalı.
        Assert.Equal(StatusCodes.Status404NotFound, ResultAssert.Status(baskasi));
        Assert.Equal(StatusCodes.Status404NotFound, ResultAssert.Status(olmayan));
        Assert.Equal(1, await host.NewContext().ThicknessRecords.CountAsync());
    }

    [Fact]
    public async Task Sahibi_kaydi_silebilir()
    {
        using var host = new TestDb();
        var owner = host.AddUser("a@alp.local");
        var http = TestHttp.For(owner);

        var saved = await ThicknessRecordEndpoints.SaveRecord(Req("Üst katman"), host.Db, http);
        var result = await ThicknessRecordEndpoints.DeleteRecord(
            ResultAssert.Value<ThicknessRecordDto>(saved).Id, host.Db, http);

        Assert.Equal(StatusCodes.Status204NoContent, ResultAssert.Status(result));
        Assert.Equal(0, await host.NewContext().ThicknessRecords.CountAsync());
    }

    [Fact]
    public async Task Liste_yalniz_kendi_kayitlarini_ada_gore_verir()
    {
        using var host = new TestDb();
        var owner = host.AddUser("a@alp.local");
        var other = host.AddUser("b@alp.local");

        await ThicknessRecordEndpoints.SaveRecord(Req("Zirve"), host.Db, TestHttp.For(owner));
        await ThicknessRecordEndpoints.SaveRecord(Req("Alt"), host.Db, TestHttp.For(owner));
        await ThicknessRecordEndpoints.SaveRecord(Req("Başkasının kaydı"), host.Db, TestHttp.For(other));

        var result = await ThicknessRecordEndpoints.ListRecords(host.Db, TestHttp.For(owner));

        var list = ResultAssert.Value<ThicknessRecordListResponse>(result).Records;
        Assert.Equal(["Alt", "Zirve"], list.Select(r => r.Name));
    }

    [Fact]
    public async Task Oturumsuz_istek_401_verir()
    {
        using var host = new TestDb();

        Assert.Equal(StatusCodes.Status401Unauthorized,
            ResultAssert.Status(await ThicknessRecordEndpoints.ListRecords(host.Db, TestHttp.Anonymous())));
        Assert.Equal(StatusCodes.Status401Unauthorized,
            ResultAssert.Status(await ThicknessRecordEndpoints.SaveRecord(Req("Kayıt"), host.Db, TestHttp.Anonymous())));
        Assert.Equal(StatusCodes.Status401Unauthorized,
            ResultAssert.Status(await ThicknessRecordEndpoints.DeleteRecord(Guid.NewGuid(), host.Db, TestHttp.Anonymous())));
    }

    [Fact]
    public async Task Ad_tekligi_veritabaninda_zorlanir()
    {
        // Uygulama kodundaki "önce ara, yoksa ekle" yarış altında kopya üretiyordu
        // (aynı adla beş eşzamanlı istek beş satır açtı). Gerçek koruma
        // `(UserId, NameKey)` benzersiz dizini: uç hiç çalışmadan, doğrudan
        // eklenen ikinci satır bile reddedilmeli.
        using var host = new TestDb();
        var user = host.AddUser("a@alp.local");

        Row(host, user.Id, "üst katman");
        await host.Db.SaveChangesAsync();

        Row(host, user.Id, "üst katman");
        await Assert.ThrowsAsync<DbUpdateException>(() => host.Db.SaveChangesAsync());

        static void Row(TestDb host, string userId, string key) => host.Db.ThicknessRecords.Add(new ThicknessRecord
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = key,
            NameKey = key,
            SchemaVersion = 1,
            DataJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow,
        });
    }

    [Fact]
    public async Task Yaris_araya_girerse_kayit_guncellenir_hata_donmez()
    {
        // `SaveRecord`un çakışma dalı — beş eşzamanlı istekle beş satır açan
        // hatanın kapandığı yer. Kurgu: uç "bu ad yok" diye karar verir, KENDİ
        // yazması gerçekleşmeden araya giren başka bir istek aynı adı yazar.
        // Benzersiz dizin ekleme girişimini reddeder ve uç bunu güncellemeye
        // çevirmelidir; kullanıcıya hata dönmez, kopya satır da oluşmaz.
        //
        // Araya girme, ilk kaydetmenin hemen öncesinde tetikleniyor (interceptor):
        // "arkasından ekleyip sonra çağırmak" ucun kendi sorgusunda görünürdü ve
        // sınanan dal hiç çalışmazdı.
        using var host = new TestDb();
        var user = host.AddUser("a@alp.local");
        var rakipId = Guid.NewGuid();

        var uc = host.NewContext(new BeforeFirstSave(() =>
        {
            using var araya = host.NewContext();
            araya.ThicknessRecords.Add(new ThicknessRecord
            {
                Id = rakipId,
                UserId = user.Id,
                Name = "Üst katman",
                NameKey = "üst katman",
                SchemaVersion = 1,
                DataJson = "{\"rakip\":true}",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            araya.SaveChanges();
        }));

        var result = await ThicknessRecordEndpoints.SaveRecord(
            Req("Üst katman", "{\"v\":3}"), uc, TestHttp.For(user));

        var dto = Dto(result);
        // Kazanan satır rakibin satırıdır; üzerine bizim yükümüz yazılmıştır.
        Assert.Equal(rakipId, dto.Id);
        Assert.Equal("{\"v\":3}", dto.DataJson);
        Assert.Equal(1, await host.NewContext().ThicknessRecords.CountAsync());
    }
}

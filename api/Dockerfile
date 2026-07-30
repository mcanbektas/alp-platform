# Derleme bağlamı depo köküdür: `docker build -f api/Dockerfile .`
# (deploy/docker-compose.yml bunu `context: ..` ile verir.)

# ---- derleme ----
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Önce yalnızca proje dosyaları kopyalanır: kaynak değişip bağımlılık
# değişmediğinde restore katmanı önbellekten gelir.
COPY api/Alp.Domain/Alp.Domain.csproj Alp.Domain/
COPY api/Alp.Data/Alp.Data.csproj Alp.Data/
COPY api/Alp.Reports/Alp.Reports.csproj Alp.Reports/
COPY api/Alp.Api/Alp.Api.csproj Alp.Api/
# Çözüm dosyası DEĞİL, uygulama projesi geri yüklenir. Çözüm test projesini de
# sayıyor; onu geri yüklemek imaja xunit ve SQLite paketlerini sokar (ve dosyası
# kopyalanmadığı için derleme baştan patlar). Proje geçişli olarak Domain/Data/
# Reports'u da getirir, yani aşağıdaki `--no-restore` yayını için yeterli.
RUN dotnet restore Alp.Api/Alp.Api.csproj

COPY api/ ./
RUN dotnet publish Alp.Api/Alp.Api.csproj -c Release -o /app/publish --no-restore

# ---- çalışma ----
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime

# QuestPDF/SkiaSharp yerel bağımlılığı fontconfig'tir; kurulmazsa PDF üretimi
# çalışma anında patlar (derlemede görünmez). DejaVu, Faz 3b'nin kendi yazı
# tipleri gelene kadarki geri düşüş — kurulmazsa PDF'te yazılar boş çıkar.
# curl sağlık kontrolü için (aspnet imajında curl/wget yok).
RUN apt-get update \
    && apt-get install -y --no-install-recommends libfontconfig1 fonts-dejavu-core curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish ./

# /app/App_Data kalıcı volume'a bağlanır ve Data Protection anahtarlarını
# taşır (raporlar saklanmıyor, diske yazılan belge yok). İmajdaki `app`
# kullanıcısı (UID 1654, aspnet imajıyla gelir) yazabilsin diye dizin önceden
# açılıp devredilir.
RUN mkdir -p /app/App_Data /app/fonts && chown -R app:app /app/App_Data /app/fonts

# Rapor yazı tipleri (Faz 3b). Kaynak `assets/report-fonts/`: aynı üç aile,
# tam kapsamlı ttf sürümleri. Site tarafı woff2 alt kümelerini kullanır
# (`web/public/fonts/`) ve `ReportFonts.RegisterIfAvailable` woff2 okumaz —
# çizim katmanı çözemez. Bu yüzden ttf'ler `web/public/` altında DURMAZ: orada
# olsalar tarayıcı hiç indirmediği hâlde `dist/`e ve web imajına girerlerdi.
# Lisans metni fontun baytıyla birlikte taşınır (SIL OFL 1.1 şartı).
COPY --chown=app:app assets/report-fonts/*.ttf assets/report-fonts/OFL-*.txt /app/fonts/

USER app
EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080

HEALTHCHECK --interval=15s --timeout=3s --start-period=30s --retries=5 \
    CMD curl -fsS http://localhost:8080/api/health || exit 1

ENTRYPOINT ["dotnet", "Alp.Api.dll"]

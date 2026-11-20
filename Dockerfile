# Watermarker - portale web e API
#
#   docker build -t watermarker .
#
# Un'unica immagine: portale, motore di watermark e FFmpeg per i video.
# (La conversione in PDF con LibreOffice/Gotenberg è stata rimossa.)

# ---------------------------------------------------------------- build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Prima solo i progetti: il restore dei pacchetti resta in cache finché non cambiano.
COPY src/Watermarker.Core/Watermarker.Core.csproj src/Watermarker.Core/
COPY src/Watermarker.Web/Watermarker.Web.csproj src/Watermarker.Web/
RUN dotnet restore src/Watermarker.Web/Watermarker.Web.csproj

COPY src/ src/
RUN dotnet publish src/Watermarker.Web/Watermarker.Web.csproj -c Release -o /app --no-restore

# ---------------------------------------------------------------- runtime
# Immagine Ubuntu 24.04 (predefinita da .NET 10), non Alpine: le librerie native di SkiaSharp
# e Magick.NET richiedono glibc.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

RUN apt-get update \
 && apt-get install -y --no-install-recommends \
      ffmpeg fontconfig fonts-liberation fonts-dejavu-core fonts-crosextra-carlito libgomp1 curl \
 && rm -rf /var/lib/apt/lists/*

COPY docker/fonts.conf /etc/fonts/local.conf
RUN mkdir -p /usr/share/fonts/custom && fc-cache -f

# Dati persistenti, da montare su volumi: chiavi dei cookie di sessione, token delle API,
# file dei lavori asincroni delle API.
RUN mkdir -p /data/keys /data/api /data/jobs && chown app:app /data/keys /data/api /data/jobs

WORKDIR /app
COPY --from=build /app .

# HOME e cache in /tmp: il container può girare con il filesystem in sola lettura.
ENV ASPNETCORE_HTTP_PORTS=8080 \
    Portal__WorkDirectory=/tmp/watermarker \
    Portal__DataProtectionPath=/data/keys \
    Api__DataPath=/data/api \
    Api__JobsPath=/data/jobs \
    HOME=/tmp \
    XDG_CACHE_HOME=/tmp/.cache

USER app
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
  CMD curl -fsS http://localhost:8080/healthz || exit 1
ENTRYPOINT ["dotnet", "Watermarker.Web.dll"]

# Nomi delle vecchie varianti, per i compose che indicano ancora "target: slim" o "target: full".
# Sono la stessa immagine; si possono togliere quando tutti i compose sono aggiornati.
FROM runtime AS slim
FROM runtime AS full

# Watermarker - portale web
#
# Due varianti (target):
#   full  (predefinita): portale + LibreOffice + FFmpeg in un solo container
#   slim               : portale + FFmpeg; la conversione in PDF è delegata a Gotenberg
#                        (vedi docker-compose.split.yml)
#
#   docker build -t watermarker .                 -> full
#   docker build -t watermarker:slim --target slim .

# ---------------------------------------------------------------- build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Prima solo i progetti: il restore dei pacchetti resta in cache finché non cambiano.
COPY src/Watermarker.Core/Watermarker.Core.csproj src/Watermarker.Core/
COPY src/Watermarker.Web/Watermarker.Web.csproj src/Watermarker.Web/
RUN dotnet restore src/Watermarker.Web/Watermarker.Web.csproj

COPY src/ src/
RUN dotnet publish src/Watermarker.Web/Watermarker.Web.csproj -c Release -o /app --no-restore

# ---------------------------------------------------------------- slim
# Debian (non Alpine): le librerie native di SkiaSharp e Magick.NET richiedono glibc.
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS slim

RUN apt-get update \
 && apt-get install -y --no-install-recommends \
      ffmpeg fontconfig fonts-liberation fonts-dejavu-core fonts-crosextra-carlito libgomp1 curl \
 && rm -rf /var/lib/apt/lists/*

COPY docker/fonts.conf /etc/fonts/local.conf
RUN mkdir -p /usr/share/fonts/custom && fc-cache -f

# Chiavi Data Protection (cookie di sessione): da montare su un volume persistente.
RUN mkdir -p /data/keys && chown app:app /data/keys

WORKDIR /app
COPY --from=build /app .

# HOME e cache in /tmp: il container può girare con il filesystem in sola lettura.
ENV ASPNETCORE_HTTP_PORTS=8080 \
    Portal__WorkDirectory=/tmp/watermarker \
    Portal__DataProtectionPath=/data/keys \
    HOME=/tmp \
    XDG_CACHE_HOME=/tmp/.cache

USER app
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
  CMD curl -fsS http://localhost:8080/healthz || exit 1
ENTRYPOINT ["dotnet", "Watermarker.Web.dll"]

# ---------------------------------------------------------------- full (predefinita)
FROM slim AS full
USER root
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
      libreoffice-writer-nogui libreoffice-calc-nogui libreoffice-impress-nogui libreoffice-draw-nogui \
 && rm -rf /var/lib/apt/lists/* \
 && fc-cache -f
USER app

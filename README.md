# Watermarker

Applica un watermark testuale a documenti Office e OpenDocument, PDF, XPS, Visio, immagini, video,
archivi ZIP ed email, **mantenendo il formato originale** e le funzionalità del documento
(selezione e ricerca del testo, modifica, formule). In alternativa converte il file in PDF e poi
applica il watermark. Cifratura e permessi sono lasciati a Microsoft Purview.

Due modi d'uso, sullo stesso motore (`Watermarker.Core`):

- **Portale web** (`Watermarker.Web`, interfaccia in inglese): l'utente carica il file, regola il watermark con un'anteprima
  dal vivo e scarica il risultato. Per ora senza autenticazione.
- **Riga di comando** (`Watermarker.Cli`): per script ed elaborazioni in blocco.

## Avvio rapido

```bash
dotnet build -c Release

# Portale: http://localhost:5080
dotnet run --project src/Watermarker.Web

# CLI
dotnet run --project src/Watermarker.Cli -- contratto.docx -t "RISERVATO - {data}"
dotnet run --project src/Watermarker.Cli -- --formats
```

Con Docker, vedi la sezione [Docker](#docker).

## Programmi esterni (facoltativi)

Nelle immagini Docker sono già inclusi. Fuori da Docker:

| Programma | Serve per | Se manca |
|---|---|---|
| **LibreOffice** | Conversione in PDF (tutti i formati della seconda tabella) | L'opzione "Converti in PDF" non è disponibile |
| **FFmpeg** (con ffprobe) | Video | I video non sono accettati |

Vengono cercati nelle posizioni standard e nel PATH; altrimenti si indica il percorso in
`appsettings.json` (`Portal:LibreOfficePath`, `Portal:FfmpegPath`) o con `--libreoffice` / `--ffmpeg` nella CLI.
Su Windows LibreOffice si installa da libreoffice.org, FFmpeg da ffmpeg.org (o `winget install Gyan.FFmpeg`).

## Docker

Due configurazioni, sugli stessi file:

| | Comando | Container |
|---|---|---|
| **Standard** | `docker compose up -d --build` | `proxy` (Caddy, HTTPS) + `watermarker` (portale con LibreOffice e FFmpeg) |
| **Conversione separata** | `docker compose -f docker-compose.yml -f docker-compose.split.yml up -d --build` | come sopra, ma il portale è "slim" e la conversione in PDF la fa `converter` (Gotenberg) |

La conversione separata isola LibreOffice (un crash o un file malevolo non tocca il portale),
alleggerisce l'immagine del portale e permette di scalare la conversione a parte.

### Primo avvio

1. `cp .env.example .env` e imposta almeno `SITE_ADDRESS` (il nome con cui il portale sarà raggiungibile).
2. Certificato HTTPS:
   - nome pubblico con porte 80/443 raggiungibili: Caddy ottiene da solo un certificato Let's Encrypt;
   - nome interno: copia `cert.pem` e `key.pem` in `docker/certs` e attiva la riga `tls` in `docker/Caddyfile`;
   - `localhost`: certificato locale di Caddy, il browser mostra un avviso (solo per prove).
3. `docker compose up -d --build`, poi apri `https://<SITE_ADDRESS>`.

### File

| File | Contenuto |
|---|---|
| `Dockerfile` | build .NET e due immagini: `full` (con LibreOffice, predefinita) e `slim` |
| `docker-compose.yml` | configurazione standard |
| `docker-compose.split.yml` | aggiunta per la conversione separata |
| `docker/Caddyfile` | proxy HTTPS, limite di dimensione delle richieste, timeout lunghi per i video |
| `docker/fonts.conf` | sostituti dei font Microsoft con le stesse metriche |
| `.env.example` | parametri: nome del sito, limiti, risorse, fuso orario |
| `fonts/` | font con licenza da aggiungere (facoltativo, vedi `fonts/README.md`) |

### Sicurezza dei container

- Solo il proxy espone porte; portale e convertitore sono raggiungibili solo sulla rete interna.
- Il portale gira come utente non privilegiato, con filesystem in sola lettura, nessuna capability
  Linux e limiti di CPU e memoria. L'unico spazio scrivibile è `/tmp` in RAM (`tmpfs`): i documenti
  non vengono mai scritti su disco.
- `MAX_BODY` (Caddy, Gotenberg) deve restare un po' più grande di `MAX_UPLOAD_MB` (portale).

### Font

L'immagine contiene solo font open source: Arial, Calibri, Times New Roman, Courier New e Verdana
sono sostituiti da font con le stesse metriche. Nei file Office non cambia nulla (il font è indicato
per nome e lo applica il PC che apre il file); in PDF, XPS, immagini e video il watermark è disegnato
con il sostituto. Per usare i font originali, se l'azienda ne ha la licenza, basta copiarli in `fonts/`
e riavviare.

### Note per l'autenticazione con Entra ID (fase successiva)

- `ASPNETCORE_FORWARDEDHEADERS_ENABLED` è già attivo: il portale vede `https` e l'IP reale dietro Caddy,
  necessario per il redirect URI del login.
- Con il login servirà un volume per le chiavi di Data Protection (cookie di sessione), altrimenti a ogni
  riavvio gli utenti dovranno rifare l'accesso; con più repliche le chiavi vanno condivise.
- Il certificato dell'app registrata va passato come *secret* di Docker, non nell'immagine.

## Formati

### Formato mantenuto

| Formato | Estensioni | Tecnica | Note |
|---|---|---|---|
| PDF | .pdf | Testo convertito in contorni vettoriali, fuso nel contenuto della pagina | Non selezionabile, non riconosciuto da "Rimuovi filigrana" |
| XPS | .xps .oxps | Contorni vettoriali in primo piano su ogni pagina | Come il PDF |
| Word | .docx .docm .dotx .dotm | WordArt bloccata in tutte le intestazioni | Invisibile a "Rimuovi filigrana" di Word |
| Excel | .xlsx .xlsm .xltx .xltm | Sfondo del foglio (schermo) + immagine nell'intestazione (stampa) | Nessun oggetto sopra le celle |
| PowerPoint | .pptx .pptm .potx .potm .ppsx .ppsm | Casella bloccata su master e layout (opzionale su ogni slide) | |
| OpenDocument | .odt .ott .ods .ots .odp .otp | Forma Fontwork protetta: intestazioni (testo), master (presentazioni), livello di sfondo (fogli) | |
| Visio | .vsdx .vsdm .vstx .vstm | Forma di testo con tutti i blocchi ShapeSheet, dietro al diagramma | |
| Immagini | .png .jpg .jpeg .gif .bmp .tif .tiff .webp | Testo fuso nei pixel; TIFF multipagina e GIF/WebP animati gestiti | La forma più resistente |
| Video | .mp4 .m4v .mov .mkv .webm .avi | Testo fuso in ogni fotogramma (FFmpeg), audio copiato | Richiede FFmpeg |
| ZIP | .zip | Watermark su ogni file supportato contenuto, anche ZIP annidati | I file non supportati restano invariati e vengono segnalati |
| Email | .eml | Watermark sugli allegati, anche di messaggi inoltrati | La firma S/MIME viene invalidata |

### Solo con conversione in PDF (LibreOffice)

.doc .dot .xls .xlt .ppt .pot .pps (Office 97-2003), .rtf .txt .csv .htm .html, .vsd .vst, .odg, .dxf, .svg;
in più tutti i formati Office, OpenDocument, Visio e immagini della tabella precedente.

### Non supportati

.msg (Outlook), .xlsb, .doc/.xls/.ppt mantenendo il formato originale, .heic, file protetti da password,
file già cifrati da Purview (vanno elaborati prima dell'etichettatura).

## Impostazioni del watermark

| Parametro | CLI | Portale | Default |
|---|---|---|---|
| Testo (anche su più righe, max 10) | `-t`, a capo con `\n` | sì, a capo con Invio | — |
| Inclinazione (gradi, antiorario) | `-a` | sì | 45 |
| Dimensione (punti) | `-s` | sì | 60 |
| Colore | `-c` | sì | grigio |
| Opacità | `--opacity` | sì | 35% |
| Carattere | `-f` | lista configurabile | Arial |
| Ripetizione a griglia | `--tile` | sì | no |
| PowerPoint sopra le slide | `--pptx-slides` | sì | no |
| Conversione in PDF | `--to-pdf` | sì | no |

**Segnaposto nel testo:** `{date}`, `{time}`, `{filename}`, `{user}` (accettati anche `{data}`, `{ora}`, `{nomefile}`, `{utente}`). Nel portale `{user}` resta vuoto
finché non si aggiunge l'autenticazione; nella CLI è l'utente di Windows.

**Più righe:** le righe sono centrate una sotto l'altra e ruotano insieme. In Word e OpenDocument
ogni riga è una scritta separata (WordArt/Fontwork allargherebbero le righe corte alla larghezza della
più lunga); LibreOffice mostra il watermark multilinea dei file Word in modo meno preciso di Word.

**Dimensione su immagini e video:** è proporzionale. 60 pt occupano sull'immagine la stessa frazione
che occuperebbero su una pagina A4, a prescindere dalla risoluzione.

**Riconoscimento del formato:** il formato si ricava dal contenuto e non dal nome. Un .docx rinominato
in .pdf viene trattato come Word, e il portale lo segnala tra gli avvisi.

## Portale web

### API

- `GET /healthz`: controllo di salute (usato da Docker).
- `GET /api/capabilities`: formati disponibili, limiti e caratteri.
- `POST /api/watermark` (multipart/form-data): campi `file`, `text`, `angle`, `size`, `color` (#RRGGBB),
  `opacity` (0-1), `font`, `output` (`original` | `pdf`), `tile`, `pptxSlides`.
  La risposta è il file. Gli avvisi arrivano nell'intestazione `X-Watermark-Warnings`
  (array JSON in Base64); gli errori sono in formato ProblemDetails.

### Configurazione (`appsettings.json`, sezione `Portal`)

| Chiave | Default | Significato |
|---|---|---|
| `MaxUploadMb` | 100 | Dimensione massima del file |
| `MaxConcurrentJobs` | 2 | Elaborazioni contemporanee (le altre restano in coda) |
| `QueueLimit` | 20 | Richieste in coda oltre le quali si risponde 503 |
| `Fonts` | Arial, Calibri, ... | Caratteri selezionabili (lista chiusa) |
| `LibreOfficePath`, `FfmpegPath` | automatico | Percorsi dei programmi esterni |
| `ConverterUrl` | vuoto | URL di Gotenberg: se impostato, la conversione in PDF avviene lì invece che con LibreOffice locale |
| `WorkDirectory` | cartella temporanea | File temporanei e profili di LibreOffice |

Sotto **IIS** va alzato anche `maxAllowedContentLength` in web.config, altrimenti i file sopra 30 MB vengono rifiutati.

### Sicurezza e privacy

- I file caricati vengono cancellati subito dopo l'elaborazione, quelli prodotti appena scaricati;
  un servizio in background rimuove ogni ora gli eventuali residui.
- Il carattere si sceglie da una lista chiusa: il motore accetta anche percorsi di file, che non devono
  essere controllabili dall'utente.
- Per gli archivi ZIP ci sono limiti su numero di file, dimensione decompressa e livelli di annidamento.
- Intestazioni di sicurezza (CSP, X-Frame-Options, nosniff) e nessuna risorsa esterna nella pagina.
- Ogni elaborazione viene registrata nel log (file, formato, IP, durata).

### Aggiungere l'autenticazione

Il portale è già predisposto: `{user}` viene riempito con `HttpContext.User.Identity.Name`.
Per Microsoft Entra ID bastano il pacchetto `Microsoft.Identity.Web`, la configurazione `AzureAd`, e
`.RequireAuthorization()` sugli endpoint e sui file statici.

## Limiti

- Nei formati modificabili (Office, OpenDocument, Visio) chi conosce il formato può rimuovere il
  watermark. La protezione vera viene da Purview, impedendo la modifica.
- **PDF/XPS**: un esperto può analizzare il contenuto della pagina e togliere i tracciati.
- **Excel**: lo sfondo si vede solo a schermo, l'intestazione solo in stampa; uno sfondo già presente viene sostituito.
- **OpenDocument (testo)**: se una pagina non ha intestazione ne viene creata una alta un punto.
- **Visio**: il carattere è quello predefinito del documento; la trasparenza è rispettata da Visio, non dal visualizzatore di LibreOffice.
- **Video**: viene ricodificato (H.264, oppure VP9 per WebM); i sottotitoli non vengono copiati.
- **Immagini CMYK**: i colori del watermark possono differire leggermente.

## Struttura

```
src/Watermarker.Core
  WatermarkEngine.cs         motore: riconoscimento, scelta del formato, conversione, scrittura atomica
  WatermarkOptions.cs        opzioni, colore, modalità di uscita
  TextTemplate.cs            segnaposto {data} {ora} {nomefile} {utente}
  Formats/                   riconoscimento del formato dal contenuto
  Pdf/ Office/ OpenDocument/ Visio/ Xps/ Images/ Video/ Containers/
  Conversion/                LibreOffice, FFmpeg, ricerca degli eseguibili
  Rendering/                 contorni dei glifi e livelli trasparenti (SkiaSharp)
  Packaging/                 lettura/scrittura dei pacchetti ZIP
src/Watermarker.Cli          riga di comando
src/Watermarker.Web          portale (API minimal + pagina in wwwroot)
```

## Librerie

PDFsharp (MIT), DocumentFormat.OpenXml (MIT), SkiaSharp (MIT), Magick.NET (Apache 2.0), MimeKit (MIT).
LibreOffice (MPL 2.0) e FFmpeg (LGPL/GPL) sono usati come programmi esterni.

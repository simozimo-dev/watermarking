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

### Stack per un Caddy condiviso

`deploy/watermarker/` contiene lo stack da usare quando Caddy è in uno stack separato (rete `edge`),
con Gotenberg e il login Entra ID: copia `docker-compose.yml` e `.env.example` (rinominato `.env`) nella
cartella dello stack sul server.

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

**Segnaposto nel testo:** `{date}`, `{time}`, `{filename}`, `{user}`, `{email}` (accettati anche `{data}`, `{ora}`, `{nomefile}`, `{utente}`). Nel portale `{user}` e `{email}`
vengono dall'account Entra ID (vuoti se il login è disattivato); nella CLI `{user}` è l'utente di Windows.

**Più righe:** le righe sono centrate una sotto l'altra e ruotano insieme. In Word e OpenDocument
ogni riga è una scritta separata (WordArt/Fontwork allargherebbero le righe corte alla larghezza della
più lunga); LibreOffice mostra il watermark multilinea dei file Word in modo meno preciso di Word.

**Dimensione su immagini e video:** è proporzionale. 60 pt occupano sull'immagine la stessa frazione
che occuperebbero su una pagina A4, a prescindere dalla risoluzione.

**Riconoscimento del formato:** il formato si ricava dal contenuto e non dal nome. Un .docx rinominato
in .pdf viene trattato come Word, e il portale lo segnala tra gli avvisi.

## Portale web

### Endpoint usati dalla pagina

- `GET /healthz`: controllo di salute (usato da Docker).
- `GET /api/capabilities`: formati disponibili, limiti e caratteri.
- `GET /api/me`: utente autenticato (nome, email, ruoli, ora di accesso).
- `POST /api/watermark` (multipart/form-data): campi `file`, `text`, `angle`, `size`, `color` (#RRGGBB),
  `opacity` (0-1), `font`, `output` (`original` | `pdf`), `tile`, `pptxSlides`.
  Richiede l'header `X-Requested-With: watermarker` (protezione CSRF). La risposta è il file. Gli avvisi arrivano nell'intestazione `X-Watermark-Warnings`
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

### Accesso con Microsoft Entra ID

Il login si attiva da solo quando sono configurati `AzureAd:TenantId`, `AzureAd:ClientId` e
`AzureAd:ClientSecret` (in Docker: `AZUREAD_TENANT_ID`, `AZUREAD_CLIENT_ID`, `AZUREAD_CLIENT_SECRET`).
Senza, il portale funziona senza login e la pagina lo segnala ("development mode").

Con il login attivo:
- tutto il portale richiede l'accesso, tranne `/healthz` e la pagina di uscita;
- in alto a destra compaiono nome, email, ruoli e ora di accesso, con il pulsante **Sign out**;
- `{user}` (nome) e `{email}` (UPN) nel testo del watermark vengono dal token di Entra ID e non sono falsificabili;
- ogni elaborazione è registrata nel log con UPN e object ID dell'utente;
- con `Portal:RequiredRole` (es. `Watermark.User`) solo chi ha quel ruolo dell'app può usare il portale.

**Registrazione dell'app** (Entra admin center → *Identity → Applications → App registrations*):

1. *New registration*: nome "Watermark portal", *Accounts in this organizational directory only*,
   Redirect URI di tipo **Web**: `https://<nome-del-portale>/signin-oidc`.
2. *Authentication*: aggiungi anche `https://<nome-del-portale>/signout-callback-oidc` tra i Redirect URI
   (serve per tornare al portale dopo il logout). Non serve attivare "ID tokens" né "Access tokens".
3. *Certificates & secrets → New client secret*: copia il **Value** in `AZUREAD_CLIENT_SECRET`.
   Annota la scadenza: alla scadenza il login smette di funzionare finché non si crea un nuovo segreto.
4. *Overview*: copia **Directory (tenant) ID** e **Application (client) ID**.
5. Facoltativo, ruoli: *App roles → Create app role* con valore `Watermark.User`, membri "Users/Groups";
   poi imposta `REQUIRED_ROLE=Watermark.User`.
6. *Enterprise applications* → la stessa app → *Properties*: **Assignment required = Yes**;
   *Users and groups*: assegna gli utenti o i gruppi (con il ruolo, se l'hai creato).
   L'assegnazione di gruppi richiede Entra ID P1.

Poi `docker compose up -d` nella cartella dello stack. Le chiavi che proteggono i cookie di sessione sono
nel volume `keys`: senza quel volume gli utenti devono rifare l'accesso a ogni riavvio del container.

Dietro Tailscale il login funziona senza aprire porte: il passaggio da Entra ID avviene nel browser
dell'utente, che è già nella tailnet.

## Più file insieme

Si possono selezionare o trascinare fino a `Portal:MaxFilesPerBatch` file (default 20). La pagina li invia
uno alla volta, mostra lo stato di ognuno (in coda, in elaborazione, fatto, fallito), e alla fine:
un file viene scaricato direttamente, più file in un unico archivio ZIP. Ogni file resta scaricabile anche
singolarmente dalla lista, e quelli falliti si possono rielaborare. L'anteprima mostra il file selezionato
nella lista.

## API per altre applicazioni

Le altre applicazioni usano il servizio con un **token** creato dall'amministratore, uno per applicazione.
Le API sono sotto `/api/v1` e non usano il login di Entra ID: il token basta. La descrizione OpenAPI è in
`GET /api/v1/openapi.yaml` (senza token), utile per generare client o un connettore personalizzato.

### Gestione dei token

La pagina **`/admin.html`** (link "Manage API tokens" nel riquadro utente) permette di creare, modificare,
disattivare, rinnovare il segreto ed eliminare i token. È riservata al ruolo dell'app `Watermark.Admin`
(`Portal:AdminRole`, in Docker `ADMIN_ROLE`): in Entra *App registrations → App roles* crea il ruolo con valore
`Watermark.Admin` e assegnalo agli amministratori da *Enterprise applications → Users and groups*.
Chi ha il ruolo amministrativo può usare anche il portale, pure se `REQUIRED_ROLE` è impostato.
Senza login (sviluppo) la pagina funziona solo con `ASPNETCORE_ENVIRONMENT=Development`.

Per ogni token si può impostare:

| Impostazione | Effetto |
|---|---|
| Scadenza | 30/90/180/365 giorni, una data, oppure mai. Dopo la scadenza il token viene rifiutato (401) |
| Abilitato | Un token disattivato viene rifiutato subito, senza eliminarlo |
| Modalità | Sincrona, asincrona o entrambe |
| Dimensione massima del file | Non oltre `Portal:MaxUploadMb` |
| Richieste al minuto | Elaborazioni (sincrone o nuovi lavori) al minuto; oltre: 429 con `Retry-After` |
| Lavori attivi | Lavori asincroni in coda o in corso contemporaneamente |
| Testo fisso | Se presente, il testo inviato dall'applicazione è ignorato (segnaposto ammessi) |
| Utente finale | Se permesso, l'applicazione può indicare `user` ed `email` per {user} ed {email}; altrimenti {user} è il nome del token |
| Indirizzi ammessi | IP o reti CIDR (es. `100.64.0.0/10`, gli IP della tailnet) da cui il token è accettato |

Il token completo (`wmk_<id>_<segreto>`) è mostrato **una sola volta**, alla creazione o al rinnovo del segreto.
Sul server si conserva solo l'impronta SHA-256 del segreto (file `tokens.json` nel volume `api`): chi legge il
file non può usare i token. Se un token viene perso o esposto: *Rotate* (il vecchio smette subito di funzionare)
oppure *Disable*. Ultimo utilizzo, indirizzo e numero di richieste sono visibili nella pagina.

### Chiamate

Il token va nell'intestazione `Authorization: Bearer wmk_...` (in alternativa `X-Api-Key: wmk_...`).
I campi del form sono gli stessi del portale: `file` (obbligatorio), `text`, `angle`, `size`, `color`,
`opacity`, `font`, `output` (`original` | `pdf`), `tile`, `pptxSlides`, e, se il token lo permette, `user` ed `email`.

| Metodo e percorso | Uso |
|---|---|
| `POST /api/v1/watermark` | **Sincrona**: la risposta è il file marcato. Intestazioni `X-Watermark-Id`, `X-Watermark-Format`, `X-Watermark-Warnings` (array JSON in Base64) |
| `POST /api/v1/jobs` | **Asincrona**: 202 con l'id del lavoro e `Location` |
| `GET /api/v1/jobs/{id}` | Stato: `queued`, `running`, `succeeded`, `failed` (con errore) |
| `GET /api/v1/jobs/{id}/result` | File marcato (409 se non è ancora pronto) |
| `DELETE /api/v1/jobs/{id}` | Elimina subito lavoro e file |
| `GET /api/v1/jobs` | Ultimi lavori del token |
| `GET /api/v1/capabilities` | Formati, font e limiti del token |

Ogni token vede solo i propri lavori. I risultati restano disponibili per `Api:ResultRetentionHours` ore
(default 24), poi vengono cancellati. Gli errori sono in formato ProblemDetails (`title`, `detail`).

```bash
# Sincrona
curl -H "Authorization: Bearer $WATERMARK_TOKEN" \
     -F file=@report.docx -F "text=CONFIDENTIAL" -F output=original \
     -o report_watermarked.docx https://<portale>/api/v1/watermark

# Asincrona
curl -H "Authorization: Bearer $WATERMARK_TOKEN" -F file=@video.mp4 -F "text={user} · {date}" \
     https://<portale>/api/v1/jobs                       # → {"id":"…","status":"queued",…}
curl -H "Authorization: Bearer $WATERMARK_TOKEN" https://<portale>/api/v1/jobs/<id>
curl -H "Authorization: Bearer $WATERMARK_TOKEN" -OJ https://<portale>/api/v1/jobs/<id>/result
```

```powershell
$h = @{ Authorization = "Bearer $env:WATERMARK_TOKEN" }
$form = @{ file = Get-Item .\report.pdf; text = "CONFIDENTIAL"; opacity = "0.3" }
Invoke-RestMethod -Method Post -Uri https://<portale>/api/v1/watermark -Headers $h -Form $form `
                  -OutFile .\report_watermarked.pdf
```
(`-Form` richiede PowerShell 7.)

### Configurazione (sezione `Api`)

| Chiave | Default | Significato |
|---|---|---|
| `Enabled` | true | Attiva `/api/v1` e la pagina dei token |
| `DataPath` | `App_Data/api` (Docker: `/data/api`) | Cartella persistente dei token |
| `JobsPath` | `App_Data/jobs` (Docker: `/data/jobs`) | File dei lavori asincroni |
| `AsyncWorkers` | 1 | Lavori asincroni elaborati insieme (si sommano a `Portal:MaxConcurrentJobs`) |
| `MaxQueuedJobs` | 50 | Lavori in attesa, per tutti i token, oltre i quali si risponde 503 |
| `ResultRetentionHours` | 24 | Durata dei risultati asincroni |
| `DefaultRequestsPerMinute` | 60 | Limite per i token che non ne indicano uno |
| `DefaultMaxActiveJobs` | 5 | Idem per i lavori attivi |

In Docker i due volumi `api` e `jobs` sono già nei file compose. Le richieste sincrone condividono la coda
del portale (`MaxConcurrentJobs`, `QueueLimit`). Dopo un riavvio i lavori già terminati restano scaricabili;
quelli in coda o in corso risultano falliti ("Interrupted") e vanno reinviati.

**Rete.** Il servizio è raggiungibile solo dalla tailnet: le applicazioni che lo chiamano devono essere
nella tailnet (con le ACL di Tailscale si decide quali macchine possono raggiungerlo). Servizi cloud come
Power Automate non lo raggiungono senza un punto di accesso pubblico o un gateway.

### Test e KPI

`tools/apitest` contiene `wmtest`, un client a riga di comando (solo Python 3.9+, nessuna installazione)
che esegue anche una suite di test funzionali delle API e test di carico a gradini, con report HTML,
JUnit e KPI (throughput, latenze p50/p95/p99, error rate, Apdex, capacità sostenibile). Vedi
[tools/apitest/README.md](tools/apitest/README.md).

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
  Processing.cs              lettura delle impostazioni ed elaborazione, comuni a portale e API
  ApiTokens.cs ApiJobs.cs    token, limiti, lavori asincroni
  ApiEndpoints.cs            /api/v1 per le altre applicazioni (openapi.yaml)
  AdminEndpoints.cs          gestione dei token (wwwroot/admin.html)
```

## Librerie

PDFsharp (MIT), DocumentFormat.OpenXml (MIT), SkiaSharp (MIT), Magick.NET (Apache 2.0), MimeKit (MIT).
LibreOffice (MPL 2.0) e FFmpeg (LGPL/GPL) sono usati come programmi esterni.

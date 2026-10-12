# wmtest: client, test funzionali e test di carico delle API

Strumento a riga di comando per usare e verificare le API `/api/v1` del servizio di watermark.
Serve **solo Python 3.9 o successivo**: non c'è niente da installare (usa la sola libreria standard).

| Comando | A cosa serve |
|---|---|
| `caps`, `watermark`, `job`, `jobs`, `status`, `result`, `delete` | Client: usare le API a mano |
| `functional` | ~65 test funzionali: autenticazione, tutti i formati, opzioni, validazione, robustezza, lavori asincroni, limiti |
| `load` | Test di carico, anche a gradini di concorrenza, con calcolo dei KPI |
| `all` | Test funzionali e poi di carico, in un unico report |
| `report` | Rigenera il report HTML di un test già eseguito (es. con SLO diversi) |

Ogni test crea una cartella in `results/` con:
- `report.html`: report autonomo con KPI, grafici e tabelle, da aprire nel browser o allegare a un ticket;
- `functional.json` e `junit.xml` per i test funzionali (`junit.xml` è leggibile da Azure DevOps, GitHub, Jenkins);
- `requests.csv` (una riga per richiesta) e `summary.json` (tutti i KPI) per i test di carico.

I test di carico aggiungono una riga a `results/history.csv`: così si confrontano le prestazioni
tra versioni, configurazioni del server o macchine diverse.

## Preparazione

1. Nella pagina **API tokens** del portale (`/admin.html`) crea un token dedicato ai test, per esempio
   "Test e collaudo", con:
   - modalità sincrona e asincrona;
   - **richieste al minuto alte** (es. 10000) e **lavori attivi alti** (es. 100), altrimenti nel test di carico
     si misura il limite del token invece del servizio (compaiono errori `rate_limited`);
   - per provare anche `user` ed `email` nel watermark, la spunta "The application can set the end user".
2. Imposta URL e token come variabili d'ambiente, così il token non finisce nella cronologia:

   ```powershell
   # Windows PowerShell
   $env:WATERMARK_URL   = "https://watermark.<tailnet>.ts.net"
   $env:WATERMARK_TOKEN = "wmk_..."
   ```
   ```bash
   # Linux
   export WATERMARK_URL=https://watermark.<tailnet>.ts.net
   read -rs WATERMARK_TOKEN && export WATERMARK_TOKEN
   ```
   In alternativa `--token-file percorso` legge il token da un file.

3. Controlla che tutto risponda:
   ```
   python wmtest.py caps
   ```

## Client

```
python wmtest.py watermark contratto.docx --text "CONFIDENTIAL\n{user} {date}" -o contratto_wm.docx
python wmtest.py watermark scansione.tif --pdf --opacity 0.25 --tile
python wmtest.py job video.mp4 --text "INTERNAL" -o video_wm.mp4      # asincrono, attende e scarica
python wmtest.py job grande.pptx --no-wait                              # mette in coda ed esce
python wmtest.py jobs
python wmtest.py status <id>
python wmtest.py result <id> -o file.pptx
python wmtest.py delete <id>
```

Il comando `watermark` mostra anche quanto tempo è andato in upload, elaborazione e download.
`wmapi.py` si può usare come libreria Python in altri script (vedi l'esempio in testa al file).

## Test funzionali

```
python wmtest.py functional
python wmtest.py functional --token2 wmk_...        # anche l'isolamento tra token diversi
python wmtest.py functional --include-limits        # anche i limiti al minuto e dei lavori attivi
```

| Area | Cosa verifica |
|---|---|
| Servizio | OpenAPI pubblica, capabilities del token |
| Autenticazione | Nessun token, token malformato, segreto sbagliato → 401; header `X-Api-Key`; l'API admin non accetta i token; un token non vede i lavori di un altro |
| Formati | Ogni file di `samples/`: formato valido in uscita, nome `_watermark`, testo del watermark presente dove il formato lo conserva (Word, PowerPoint, OpenDocument), immagini con le stesse dimensioni, allegati e contenuto degli ZIP marcati |
| Conversione | Word, .doc, Excel, PowerPoint, ODT → PDF (se il server ha il convertitore) |
| Opzioni | Più righe, segnaposto, Unicode, tutti i font, valori limite, ripetizione, slide PowerPoint, colori |
| Validazione | Valori fuori intervallo, colore e font non validi, testo vuoto/troppo lungo/troppe righe, file mancante o vuoto → 400 |
| Utente e testo fisso | `user`/`email` accettati o rifiutati secondo il token, `{user}` predefinito, testo fisso |
| Robustezza | Byte casuali → 415, Word troncato → 4xx (500 = avviso), PDF con estensione .docx, path traversal nel nome, nome lunghissimo, servizio ancora in salute |
| Asincrono | Ciclo completo (coda, stato, risultato, doppio download, elenco, cancellazione), lavoro fallito, risultato non pronto (409), id inesistente, video, lavori in parallelo, coerenza dei tempi e della scadenza |
| Limiti | Modalità non permesse → 403, file oltre il limite → 413, richieste al minuto → 429, lavori attivi → 429 |

Esito: `PASS`, `FAIL`, `WARN` (funziona ma in modo migliorabile) o `skip` (non applicabile al token
o al server). Il comando termina con codice 1 se un test fallisce: si può usare in una pipeline.

Per provare documenti vostri, metteteli in una cartella e usate `--samples cartella`.

## Test di carico

```
# 4 utenti virtuali per 2 minuti
python wmtest.py load -c 4 -d 120

# Gradini: 1, 2, 4, 8, 16 utenti, 60 secondi ciascuno → trova il punto di saturazione
python wmtest.py load --steps 1,2,4,8,16 -d 60

# Corpus realistico: i vostri file, con un mix di formati
python wmtest.py load --steps 2,4,8 --samples D:\corpus --mix docx=5,pdf=4,xlsx=2,pptx=1

# Lavori asincroni (code, video, file grandi)
python wmtest.py load --mode async --steps 2,4,8 -d 90

# Obiettivi di servizio diversi da quelli predefiniti
python wmtest.py load --steps 1,2,4,8 --slo-p95 5 --slo-errors 0.5 --apdex 3
```

Ogni utente virtuale invia un file dopo l'altro (con `--think` si aggiunge una pausa media).
In modalità asincrona un "giro" è: invio del lavoro, controllo dello stato, download, cancellazione.
Con Ctrl+C il test si ferma e il report viene comunque prodotto.

Durante il test conviene guardare anche le risorse del server, per esempio con `docker stats`
sul server, per capire se il limite è la CPU, la memoria o `MaxConcurrentJobs`.

### KPI calcolati

| KPI | Significato |
|---|---|
| **Throughput** | File elaborati con successo al secondo (e al minuto); MB/s in ingresso e in uscita |
| **Latenza** p50 / p90 / p95 / p99 / max | Tempo per avere il file marcato. Sincrona: la richiesta completa. Asincrona: dall'invio al file scaricato |
| **Scomposizione** | Upload, attesa del server, download; in asincrono anche attesa in coda ed elaborazione (dai tempi registrati dal server) |
| **Velocità del server** per formato | MB elaborati al secondo: utile per stimare i tempi dei file grandi |
| **Error rate** e tipi di errore | `rate_limited` (429, limite del token), `busy_503` (coda del server piena), `unprocessable`, `server_5xx`, `timeout`, `connection` |
| **Apdex** | Soddisfazione da 0 a 1: risposta entro T = soddisfatto, entro 4T = tollerabile, oltre = insoddisfatto |
| **Capacità** | Il gradino con il throughput più alto che rispetta gli SLO ("carico massimo sostenibile") e il gradino oltre il quale il throughput cresce meno del 10% (saturazione: altri utenti aggiungono solo attesa) |
| **SLO** | Latenza p95, error rate ed eventuale throughput minimo: superato o no, per l'intero test e per ogni gradino |

Codice di uscita: 0 se gli SLO sono rispettati, 2 se no, 1 per errori di esecuzione.

## File di esempio

`samples/` contiene file piccoli e realistici di ogni formato: Word, Excel, PowerPoint,
OpenDocument, PDF da 1 e 40 pagine, PNG, JPEG, un video MP4, una mail con allegati, uno ZIP
e un `.doc` (convertibile solo in PDF). `make_samples.py` li rigenera; non serve per usare i test.
Per i KPI di carico i file vostri, con le dimensioni reali, danno numeri più significativi.

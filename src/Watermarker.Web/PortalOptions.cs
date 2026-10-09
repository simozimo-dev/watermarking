namespace Watermarker.Web;

/// <summary>Configurazione del portale (sezione "Portal" di appsettings.json).</summary>
public sealed class PortalOptions
{
    /// <summary>Dimensione massima del file caricato, in MB.</summary>
    public int MaxUploadMb { get; set; } = 100;

    /// <summary>Elaborazioni contemporanee. Le altre richieste restano in coda.</summary>
    public int MaxConcurrentJobs { get; set; } = 2;

    /// <summary>Richieste in coda oltre le quali si risponde "servizio occupato".</summary>
    public int QueueLimit { get; set; } = 20;

    /// <summary>Lunghezza massima del testo del watermark.</summary>
    public int MaxTextLength { get; set; } = 200;

    /// <summary>
    /// Caratteri selezionabili. È una lista chiusa: il motore accetta anche percorsi di file
    /// e un utente non deve poter far leggere file arbitrari al server.
    /// </summary>
    public List<string> Fonts { get; set; } = [];

    /// <summary>
    /// La lista vuota indica i caratteri predefiniti. Il binder della configurazione aggiunge
    /// gli elementi a una lista esistente invece di sostituirla, per questo il default sta qui.
    /// </summary>
    public IReadOnlyList<string> EffectiveFonts =>
        Fonts.Count > 0 ? Fonts.Distinct().ToList() : ["Arial", "Calibri", "Verdana", "Times New Roman", "Courier New"];

    public string? LibreOfficePath { get; set; }
    public string? FfmpegPath { get; set; }

    /// <summary>URL del servizio di conversione Gotenberg. Se vuoto si usa LibreOffice installato localmente.</summary>
    public string? ConverterUrl { get; set; }

    /// <summary>Cartella per i file temporanei. Default: cartella temporanea di sistema.</summary>
    public string? WorkDirectory { get; set; }

    public int ConversionTimeoutSeconds { get; set; } = 120;
    public int VideoTimeoutMinutes { get; set; } = 30;
}

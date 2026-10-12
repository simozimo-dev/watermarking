namespace Watermarker.Web;

/// <summary>Configurazione delle API per le altre applicazioni (sezione "Api" di appsettings.json).</summary>
public sealed class ApiOptions
{
    /// <summary>Attiva le API /api/v1 e la gestione dei token.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Cartella persistente in cui salvare i token (solo l'impronta del segreto, mai il segreto).</summary>
    public string? DataPath { get; set; }

    /// <summary>Cartella persistente per i file dei lavori asincroni (ingresso e risultato).</summary>
    public string? JobsPath { get; set; }

    /// <summary>Lavori asincroni elaborati contemporaneamente (si sommano a Portal:MaxConcurrentJobs).</summary>
    public int AsyncWorkers { get; set; } = 1;

    /// <summary>Lavori asincroni in attesa, per tutti i token insieme, oltre i quali si risponde 503.</summary>
    public int MaxQueuedJobs { get; set; } = 50;

    /// <summary>Ore per cui il risultato di un lavoro asincrono resta scaricabile.</summary>
    public int ResultRetentionHours { get; set; } = 24;

    /// <summary>Richieste di elaborazione al minuto per token, se il token non ne indica un'altra.</summary>
    public int DefaultRequestsPerMinute { get; set; } = 60;

    /// <summary>Lavori asincroni non ancora terminati per token, se il token non ne indica un altro numero.</summary>
    public int DefaultMaxActiveJobs { get; set; } = 5;
}

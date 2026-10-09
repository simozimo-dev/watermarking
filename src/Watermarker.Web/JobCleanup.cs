using Watermarker.Core;

namespace Watermarker.Web;

/// <summary>
/// Cancella i file temporanei rimasti orfani (download interrotti, riavvii del server).
/// I documenti caricati non vengono conservati oltre il tempo necessario all'elaborazione.
/// </summary>
public sealed class JobCleanup(WatermarkEngine engine, ILogger<JobCleanup> log) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            Clean();
            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void Clean()
    {
        var dir = new DirectoryInfo(Path.Combine(engine.Settings.WorkDirectory, "jobs"));
        if (!dir.Exists) return;

        int removed = 0;
        foreach (var file in dir.EnumerateFiles())
        {
            if (DateTime.UtcNow - file.LastWriteTimeUtc < MaxAge) continue;
            try { file.Delete(); removed++; }
            catch (IOException) { /* ancora in uso: riproviamo al giro successivo */ }
            catch (UnauthorizedAccessException) { }
        }

        if (removed > 0) log.LogInformation("Rimossi {Count} file temporanei scaduti.", removed);
    }
}

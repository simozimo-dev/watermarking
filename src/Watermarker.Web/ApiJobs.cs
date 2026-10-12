using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Watermarker.Core;

namespace Watermarker.Web;

/// <summary>Lavoro asincrono: stato e metadati, salvati in job.json nella cartella del lavoro.</summary>
public sealed class ApiJob
{
    public string Id { get; set; } = "";
    public string TokenId { get; set; } = "";
    public string TokenName { get; set; } = "";
    public string Status { get; set; } = JobStatus.Queued;

    public string FileName { get; set; } = "";
    public long InputBytes { get; set; }
    public string Output { get; set; } = "original";
    public string? User { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }

    public string? Format { get; set; }
    public string? ResultName { get; set; }
    public string? ResultFile { get; set; }
    public string? ResultContentType { get; set; }
    public long? ResultBytes { get; set; }
    public List<string> Warnings { get; set; } = [];

    public int? ErrorStatus { get; set; }
    public string? ErrorTitle { get; set; }
    public string? Error { get; set; }

    [JsonIgnore] public bool IsFinished => Status is JobStatus.Succeeded or JobStatus.Failed;

    /// <summary>Risposta per il chiamante.</summary>
    public object ToView()
    {
        lock (this)
        {
            return new
            {
                id = Id,
                status = Status,
                fileName = FileName,
                inputBytes = InputBytes,
                output = Output,
                createdAt = CreatedAt,
                startedAt = StartedAt,
                completedAt = CompletedAt,
                expiresAt = ExpiresAt,
                format = Format,
                warnings = Warnings.ToList(),
                result = Status == JobStatus.Succeeded
                    ? new { fileName = ResultName, contentType = ResultContentType, bytes = ResultBytes, url = $"/api/v1/jobs/{Id}/result" }
                    : null,
                error = Status == JobStatus.Failed ? new { status = ErrorStatus, title = ErrorTitle, detail = Error } : null,
                statusUrl = $"/api/v1/jobs/{Id}",
            };
        }
    }
}

public static class JobStatus
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
}

/// <summary>
/// Lavori asincroni: una cartella per lavoro sul volume persistente, una coda in memoria.
/// Dopo un riavvio i lavori terminati restano scaricabili; quelli in coda o in corso sono segnati
/// come falliti, perché le impostazioni del watermark non vengono salvate su disco.
/// </summary>
public sealed class ApiJobStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<string, ApiJob> _jobs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (WatermarkOptions Options, OutputMode Mode)> _pending = new(StringComparer.Ordinal);
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleWriter = false });
    private readonly ILogger<ApiJobStore> _log;

    public ApiJobStore(string root, ILogger<ApiJobStore> log)
    {
        Root = root;
        _log = log;
        Directory.CreateDirectory(root);

        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var file = Path.Combine(dir, "job.json");
            try
            {
                var job = File.Exists(file) ? JsonSerializer.Deserialize<ApiJob>(File.ReadAllText(file), Json) : null;
                if (job is null || job.Id != Path.GetFileName(dir)) { Directory.Delete(dir, recursive: true); continue; }

                if (!job.IsFinished)
                    Fail(job, 503, "Interrupted", "The server restarted before the job finished. Submit the file again.");
                _jobs[job.Id] = job;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                log.LogWarning(ex, "Skipping unreadable job folder {Dir}", dir);
            }
        }
    }

    public string Root { get; }

    public ChannelReader<string> Queue => _queue.Reader;

    public string DirectoryOf(string id) => Path.Combine(Root, id);

    public int QueuedCount => _jobs.Values.Count(j => j.Status == JobStatus.Queued);

    public int ActiveCount(string tokenId) => _jobs.Values.Count(j => j.TokenId == tokenId && !j.IsFinished);

    public IEnumerable<ApiJob> ForToken(string tokenId) =>
        _jobs.Values.Where(j => j.TokenId == tokenId).OrderByDescending(j => j.CreatedAt);

    public ApiJob? Get(string id, string tokenId) =>
        _jobs.TryGetValue(id, out var job) && job.TokenId == tokenId ? job : null;

    public void Enqueue(ApiJob job, WatermarkOptions options, OutputMode mode)
    {
        _jobs[job.Id] = job;
        _pending[job.Id] = (options, mode);
        Save(job);
        _queue.Writer.TryWrite(job.Id);
    }

    public bool TryTake(string id, out ApiJob job, out WatermarkOptions options, out OutputMode mode)
    {
        options = null!;
        mode = default;
        job = null!;
        if (!_pending.TryRemove(id, out var work) || !_jobs.TryGetValue(id, out var j)) return false;
        (job, options, mode) = (j, work.Options, work.Mode);
        return true;
    }

    public void Save(ApiJob job)
    {
        byte[] content;
        lock (job) content = JsonSerializer.SerializeToUtf8Bytes(job, Json);
        try
        {
            Storage.WriteAtomic(Path.Combine(DirectoryOf(job.Id), "job.json"), content);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogError(ex, "Could not save job {Id}", job.Id);
        }
    }

    /// <summary>Elimina un lavoro e i suoi file. Un lavoro in corso non può essere eliminato.</summary>
    public bool TryRemove(string id)
    {
        if (!_jobs.TryGetValue(id, out var job)) return true;
        lock (job) if (job.Status == JobStatus.Running) return false;

        _pending.TryRemove(id, out _);
        _jobs.TryRemove(id, out _);
        try { Directory.Delete(DirectoryOf(id), recursive: true); }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log.LogWarning(ex, "Could not delete job folder {Id}", id); }
        return true;
    }

    public int RemoveExpired()
    {
        var now = DateTimeOffset.UtcNow;
        int removed = 0;
        foreach (var job in _jobs.Values.Where(j => j.IsFinished && j.ExpiresAt is { } e && e <= now).ToList())
            if (TryRemove(job.Id)) removed++;
        return removed;
    }

    public void Fail(ApiJob job, int status, string title, string detail, int retentionHours = 24)
    {
        lock (job)
        {
            job.Status = JobStatus.Failed;
            job.ErrorStatus = status;
            job.ErrorTitle = title;
            job.Error = detail;
            job.CompletedAt = DateTimeOffset.UtcNow;
            job.ExpiresAt = job.CompletedAt.Value.AddHours(retentionHours);
        }
        Save(job);
    }
}

/// <summary>Esegue i lavori asincroni, pochi alla volta.</summary>
public sealed class ApiJobWorker(ApiJobStore store, WatermarkEngine engine, ApiOptions api, ILogger<ApiJobWorker> log)
    : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(0, Math.Max(1, api.AsyncWorkers)).Select(_ => Loop(stoppingToken)));

    private async Task Loop(CancellationToken stop)
    {
        try
        {
            await foreach (var id in store.Queue.ReadAllAsync(stop))
            {
                if (!store.TryTake(id, out var job, out var options, out var mode)) continue; // eliminato mentre era in coda
                await Run(job, options, mode, stop);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    private async Task Run(ApiJob job, WatermarkOptions options, OutputMode mode, CancellationToken stop)
    {
        var dir = store.DirectoryOf(job.Id);
        var input = Path.Combine(dir, "input");
        var sw = Stopwatch.StartNew();

        lock (job)
        {
            job.Status = JobStatus.Running;
            job.StartedAt = DateTimeOffset.UtcNow;
        }
        store.Save(job);

        try
        {
            var outcome = await Processing.RunAsync(engine, input, job.FileName, Path.Combine(dir, "result"), options, mode, stop);
            lock (job)
            {
                job.Status = JobStatus.Succeeded;
                job.Format = outcome.Format;
                job.ResultName = outcome.DownloadName;
                job.ResultFile = Path.GetFileName(outcome.OutputPath);
                job.ResultContentType = outcome.ContentType;
                job.ResultBytes = new FileInfo(outcome.OutputPath).Length;
                job.Warnings = outcome.Warnings.ToList();
                job.CompletedAt = DateTimeOffset.UtcNow;
                job.ExpiresAt = job.CompletedAt.Value.AddHours(api.ResultRetentionHours);
            }
            store.Save(job);

            log.LogInformation(
                "API job {Id} done: {File} ({Size} bytes, {Format}, output {Mode}) for {Token}, user {User}, in {Ms} ms, {Warnings} warnings",
                job.Id, job.FileName, job.InputBytes, outcome.Format, mode, job.TokenName, job.User ?? "-",
                sw.ElapsedMilliseconds, outcome.Warnings.Count);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            store.Fail(job, 503, "Interrupted", "The server stopped before the job finished. Submit the file again.", api.ResultRetentionHours);
        }
        catch (Exception ex)
        {
            var (status, title, detail) = Processing.Classify(ex);
            Processing.LogFailure(log, ex, status, job.FileName);
            store.Fail(job, status, title, detail, api.ResultRetentionHours);
        }
        finally
        {
            try { File.Delete(input); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}

/// <summary>Salva l'uso dei token e cancella i risultati scaduti.</summary>
public sealed class ApiMaintenance(ApiTokenStore tokens, ApiJobStore jobs, ILogger<ApiMaintenance> log) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }

            tokens.Flush();
            var removed = jobs.RemoveExpired();
            if (removed > 0) log.LogInformation("Removed {Count} expired API jobs.", removed);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        tokens.Flush();
    }
}

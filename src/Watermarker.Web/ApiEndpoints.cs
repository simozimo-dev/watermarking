using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Watermarker.Core;

namespace Watermarker.Web;

/// <summary>
/// API per altre applicazioni (/api/v1), autenticate con un token creato dall'amministratore.
///
///   POST   /api/v1/watermark          sincrona: invii il file, ricevi il file marcato
///   POST   /api/v1/jobs               asincrona: 202 con l'id del lavoro
///   GET    /api/v1/jobs               lavori del token
///   GET    /api/v1/jobs/{id}          stato del lavoro
///   GET    /api/v1/jobs/{id}/result   file marcato
///   DELETE /api/v1/jobs/{id}          elimina lavoro e file
///   GET    /api/v1/capabilities       formati, font e limiti del token
///   GET    /api/v1/openapi.yaml       descrizione OpenAPI (senza token)
/// </summary>
public static class ApiEndpoints
{
    private const string ClientKey = "wm.api-token";
    private const string LogCategory = "Watermarker.Web.Api";

    private static readonly Lazy<string> OpenApi = new(() =>
    {
        using var s = typeof(ApiEndpoints).Assembly.GetManifestResourceStream("openapi.yaml")
                      ?? throw new InvalidOperationException("openapi.yaml is not embedded in the assembly.");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    });

    private sealed record Submission(string FileName, IFormFile File, WatermarkOptions Options, OutputMode Mode, string? User);

    // ------------------------------------------------------------------ autenticazione

    /// <summary>Verifica il token su tutte le richieste /api/v1 (tranne la descrizione OpenAPI).</summary>
    public static void UseApiTokens(this WebApplication app)
    {
        app.UseWhen(ctx => ctx.Request.Path.StartsWithSegments("/api/v1"), branch => branch.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.Equals("/api/v1/openapi.yaml", StringComparison.OrdinalIgnoreCase))
            {
                await next();
                return;
            }

            var presented = ReadToken(ctx.Request);
            if (presented is null)
            {
                await Reject(ctx, 401, "Missing API token", "Send the token in the header \"Authorization: Bearer wmk_...\".");
                return;
            }

            var store = ctx.RequestServices.GetRequiredService<ApiTokenStore>();
            var token = store.Authenticate(presented, out var failure);
            var remote = ctx.Connection.RemoteIpAddress;
            if (token is null)
            {
                ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategory)
                    .LogWarning("API token rejected from {Client}: {Reason}", remote, failure);
                await Reject(ctx, 401, "Invalid API token", failure);
                return;
            }

            if (!ApiTokenStore.IsAllowedFrom(token, remote))
            {
                ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategory)
                    .LogWarning("API token {Token} used from a network that is not allowed: {Client}", token.Name, remote);
                await Reject(ctx, 403, "Network not allowed", $"This token cannot be used from {remote}.");
                return;
            }

            ctx.Items[ClientKey] = token;
            store.Touch(token.Id, remote?.ToString());
            await next();
        }));
    }

    private static string? ReadToken(HttpRequest request)
    {
        var auth = request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return auth["Bearer ".Length..].Trim();

        var key = request.Headers["X-Api-Key"].ToString();
        return key.Length > 0 ? key.Trim() : null;
    }

    private static Task Reject(HttpContext ctx, int status, string title, string detail)
    {
        if (status == 401) ctx.Response.Headers.WWWAuthenticate = "Bearer realm=\"watermarker\"";
        return Results.Problem(statusCode: status, title: title, detail: detail).ExecuteAsync(ctx);
    }

    private static ApiToken Client(HttpContext http) =>
        http.Items[ClientKey] as ApiToken ?? throw new InvalidOperationException("API token middleware not configured.");

    // ------------------------------------------------------------------ endpoint

    public static void MapApiV1(this WebApplication app)
    {
        var v1 = app.MapGroup("/api/v1");

        v1.MapGet("/openapi.yaml", () => Results.Text(OpenApi.Value, "application/yaml; charset=utf-8"));
        v1.MapGet("/capabilities", Capabilities);
        v1.MapPost("/watermark", Watermark).DisableAntiforgery().RequireRateLimiting("processing");
        v1.MapPost("/jobs", CreateJob).DisableAntiforgery();
        v1.MapGet("/jobs", ListJobs);
        v1.MapGet("/jobs/{id}", GetJob);
        v1.MapGet("/jobs/{id}/result", GetResult);
        v1.MapDelete("/jobs/{id}", DeleteJob);
    }

    private static IResult Capabilities(HttpContext http, WatermarkEngine engine, PortalOptions portal, ApiOptions api)
    {
        var token = Client(http);
        var s = token.Settings;
        return Results.Ok(new
        {
            token = new
            {
                name = token.Name,
                expiresAt = token.ExpiresAt,
                allowSync = s.AllowSync,
                allowAsync = s.AllowAsync,
                maxUploadBytes = (s.MaxUploadMb ?? portal.MaxUploadMb) * 1024L * 1024L,
                requestsPerMinute = s.RequestsPerMinute ?? api.DefaultRequestsPerMinute,
                maxActiveJobs = s.MaxActiveJobs ?? api.DefaultMaxActiveJobs,
                fixedText = s.FixedText is not null,
                allowUserOverride = s.AllowUserOverride,
            },
            native = engine.NativeExtensions,
            pdf = engine.PdfExtensions,
            converterAvailable = engine.Converter is not null,
            maxTextLength = portal.MaxTextLength,
            maxLines = WatermarkOptions.MaxLines,
            fonts = portal.EffectiveFonts,
            placeholders = TextTemplate.Placeholders,
            resultRetentionHours = api.ResultRetentionHours,
        });
    }

    /// <summary>Elaborazione sincrona: la risposta è il file marcato.</summary>
    private static async Task<IResult> Watermark(HttpContext http, WatermarkEngine engine, PortalOptions portal, ApiOptions api,
                                                 ApiRateLimiter limiter, ILoggerFactory logs, CancellationToken ct)
    {
        var token = Client(http);
        if (!token.Settings.AllowSync)
            return Processing.Problem(403, "Not allowed", "This token cannot use synchronous processing. Use POST /api/v1/jobs.");
        if (CheckRate(http, token, api, limiter) is { } limited) return limited;

        var (sub, error) = await ReadSubmission(http, token, portal, ct);
        if (sub is null) return error!;

        var log = logs.CreateLogger(LogCategory);
        var work = Path.Combine(engine.Settings.WorkDirectory, "jobs");
        Directory.CreateDirectory(work);
        var id = Guid.NewGuid().ToString("N");
        var input = Path.Combine(work, id + ".in");
        var sw = Stopwatch.StartNew();

        try
        {
            await using (var fs = File.Create(input))
                await sub.File.CopyToAsync(fs, ct);

            var outcome = await Processing.RunAsync(engine, input, sub.FileName, Path.Combine(work, id + ".out"),
                                                    sub.Options, sub.Mode, ct);

            log.LogInformation(
                "API watermark {Id}: {File} ({Size} bytes, {Format}, output {Mode}) for {Token}, user {User}, from {Client} in {Ms} ms, {Warnings} warnings",
                id, sub.FileName, sub.File.Length, outcome.Format, sub.Mode, token.Name, sub.User ?? "-",
                http.Connection.RemoteIpAddress, sw.ElapsedMilliseconds, outcome.Warnings.Count);

            var h = http.Response.Headers;
            h["X-Watermark-Id"] = id;
            h["X-Watermark-Format"] = Uri.EscapeDataString(outcome.Format);
            h["X-Watermark-Warnings"] = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(outcome.Warnings));
            h["X-Watermark-Warning-Count"] = outcome.Warnings.Count.ToString(CultureInfo.InvariantCulture);

            return Results.File(Processing.OpenAndDeleteOnClose(outcome.OutputPath), outcome.ContentType, outcome.DownloadName);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Results.Empty;
        }
        catch (Exception ex)
        {
            var (status, title, detail) = Processing.Classify(ex);
            Processing.LogFailure(log, ex, status, sub.FileName);
            return Processing.Problem(status, title, detail);
        }
        finally
        {
            try { File.Delete(input); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Elaborazione asincrona: salva il file, mette il lavoro in coda e risponde 202.</summary>
    private static async Task<IResult> CreateJob(HttpContext http, ApiJobStore jobs, PortalOptions portal, ApiOptions api,
                                                 ApiRateLimiter limiter, ILoggerFactory logs, CancellationToken ct)
    {
        var token = Client(http);
        if (!token.Settings.AllowAsync)
            return Processing.Problem(403, "Not allowed", "This token cannot use asynchronous jobs. Use POST /api/v1/watermark.");
        if (CheckRate(http, token, api, limiter) is { } limited) return limited;

        var maxActive = token.Settings.MaxActiveJobs ?? api.DefaultMaxActiveJobs;
        if (jobs.ActiveCount(token.Id) >= maxActive)
        {
            http.Response.Headers.RetryAfter = "30";
            return Processing.Problem(429, "Too many active jobs",
                $"This token can have at most {maxActive} jobs queued or running. Wait for them to finish.");
        }
        if (jobs.QueuedCount >= api.MaxQueuedJobs)
        {
            http.Response.Headers.RetryAfter = "60";
            return Processing.Problem(503, "Service busy", "Too many jobs are waiting. Try again later.");
        }

        var (sub, error) = await ReadSubmission(http, token, portal, ct);
        if (sub is null) return error!;

        var id = Guid.NewGuid().ToString("N");
        var dir = jobs.DirectoryOf(id);
        Directory.CreateDirectory(dir);
        try
        {
            await using var fs = File.Create(Path.Combine(dir, "input"));
            await sub.File.CopyToAsync(fs, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
            return Results.Empty;
        }

        var job = new ApiJob
        {
            Id = id,
            TokenId = token.Id,
            TokenName = token.Name,
            FileName = sub.FileName,
            InputBytes = sub.File.Length,
            Output = sub.Mode == OutputMode.Pdf ? "pdf" : "original",
            User = sub.User,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        jobs.Enqueue(job, sub.Options, sub.Mode);

        logs.CreateLogger(LogCategory).LogInformation(
            "API job {Id} queued: {File} ({Size} bytes) for {Token}, user {User}, from {Client}",
            id, sub.FileName, sub.File.Length, token.Name, sub.User ?? "-", http.Connection.RemoteIpAddress);

        return Results.Accepted($"/api/v1/jobs/{id}", job.ToView());
    }

    private static IResult ListJobs(HttpContext http, ApiJobStore jobs) =>
        Results.Ok(jobs.ForToken(Client(http).Id).Take(200).Select(j => j.ToView()));

    private static IResult GetJob(string id, HttpContext http, ApiJobStore jobs) =>
        jobs.Get(id, Client(http).Id) is { } job ? Results.Ok(job.ToView()) : NotFound();

    private static IResult GetResult(string id, HttpContext http, ApiJobStore jobs)
    {
        var job = jobs.Get(id, Client(http).Id);
        if (job is null) return NotFound();

        string status, name, type;
        string? file;
        lock (job)
        {
            status = job.Status;
            file = job.ResultFile;
            name = job.ResultName ?? "result";
            type = job.ResultContentType ?? "application/octet-stream";
        }

        if (status == JobStatus.Failed)
            return Processing.Problem(409, "Job failed", "The job failed: see the error in GET /api/v1/jobs/" + id + ".");
        if (status != JobStatus.Succeeded || file is null)
        {
            http.Response.Headers.RetryAfter = "5";
            return Processing.Problem(409, "Not ready", $"The job is {status}. Try again later.");
        }

        var path = Path.Combine(jobs.DirectoryOf(id), file);
        if (!File.Exists(path)) return Processing.Problem(410, "Result no longer available", "The result has been deleted.");

        http.Response.Headers["X-Watermark-Id"] = id;
        return Results.File(path, type, name, enableRangeProcessing: true);
    }

    private static IResult DeleteJob(string id, HttpContext http, ApiJobStore jobs)
    {
        if (jobs.Get(id, Client(http).Id) is null) return NotFound();
        return jobs.TryRemove(id)
            ? Results.NoContent()
            : Processing.Problem(409, "Job running", "The job is running and cannot be deleted now. Try again when it has finished.");
    }

    // ------------------------------------------------------------------ supporto

    private static IResult NotFound() =>
        Processing.Problem(404, "Job not found", "The job does not exist, has expired or belongs to another token.");

    private static IResult? CheckRate(HttpContext http, ApiToken token, ApiOptions api, ApiRateLimiter limiter)
    {
        var limit = token.Settings.RequestsPerMinute ?? api.DefaultRequestsPerMinute;
        if (limiter.TryAcquire(token.Id, limit, out var retryAfter)) return null;

        http.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
        return Processing.Problem(429, "Too many requests", $"This token allows {limit} requests per minute.");
    }

    private static async Task<(Submission? Value, IResult? Error)> ReadSubmission(HttpContext http, ApiToken token,
                                                                                  PortalOptions portal, CancellationToken ct)
    {
        if (!http.Request.HasFormContentType)
            return (null, Processing.Problem(400, "Invalid request", "Send the file as multipart/form-data in a field named \"file\"."));

        var maxMb = Math.Min(token.Settings.MaxUploadMb ?? portal.MaxUploadMb, portal.MaxUploadMb);
        long maxBytes = maxMb * 1024L * 1024L;
        var tooLarge = Processing.Problem(413, "File too large", $"The maximum size for this token is {maxMb} MB.");

        if (http.Request.ContentLength > maxBytes + 1024 * 1024) return (null, tooLarge);

        IFormCollection form;
        try
        {
            form = await http.Request.ReadFormAsync(ct);
        }
        catch (Exception ex) when (ex is InvalidDataException or BadHttpRequestException { StatusCode: 413 })
        {
            return (null, tooLarge);
        }

        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0)
            return (null, Processing.Problem(400, "No file", "Send the file in a multipart field named \"file\"."));
        if (file.Length > maxBytes) return (null, tooLarge);

        var fileName = Path.GetFileName(file.FileName);
        if (string.IsNullOrWhiteSpace(fileName)) fileName = "file";

        string? user = null, email = null;
        bool wantsUser = !string.IsNullOrWhiteSpace(form["user"]) || !string.IsNullOrWhiteSpace(form["email"]);
        if (wantsUser && !token.Settings.AllowUserOverride)
            return (null, Processing.Problem(403, "Not allowed", "This token cannot set the user or email shown in the watermark."));
        if (wantsUser)
        {
            user = Limit(form["user"], 200);
            email = Limit(form["email"], 200);
        }

        var identity = new WatermarkIdentity(user ?? token.Name, email);
        if (!Processing.TryReadOptions(form, portal, fileName, identity, token.Settings.FixedText,
                                       out var options, out var mode, out var error))
            return (null, Processing.Problem(400, "Invalid settings", error));

        return (new Submission(fileName, file, options, mode, user ?? email), null);
    }

    private static string? Limit(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        return value.Length <= max ? value : value[..max];
    }
}

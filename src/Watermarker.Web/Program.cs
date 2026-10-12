using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using Watermarker.Core;
using Watermarker.Web;

var builder = WebApplication.CreateBuilder(args);

var portal = builder.Configuration.GetSection("Portal").Get<PortalOptions>() ?? new PortalOptions();
long maxBodyBytes = portal.MaxUploadMb * 1024L * 1024L;

builder.Services.AddSingleton(portal);
builder.Services.AddSingleton(_ => new WatermarkEngine(new EngineSettings
{
    // Nella configurazione un valore null diventa stringa vuota: va trattata come "non impostato".
    LibreOfficePath = NullIfEmpty(portal.LibreOfficePath),
    FfmpegPath = NullIfEmpty(portal.FfmpegPath),
    ConverterUrl = NullIfEmpty(portal.ConverterUrl),
    WorkDirectory = NullIfEmpty(portal.WorkDirectory) ?? Path.Combine(Path.GetTempPath(), "watermarker"),
    MaxParallelConversions = portal.MaxConcurrentJobs,
    ConversionTimeout = TimeSpan.FromSeconds(portal.ConversionTimeoutSeconds),
    VideoTimeout = TimeSpan.FromMinutes(portal.VideoTimeoutMinutes),
}));
builder.Services.AddHostedService<JobCleanup>();

// API per le altre applicazioni: token creati dall'amministratore, lavori sincroni e asincroni.
var api = builder.Configuration.GetSection("Api").Get<ApiOptions>() ?? new ApiOptions();
builder.Services.AddSingleton(api);
if (api.Enabled)
{
    var appData = Path.Combine(builder.Environment.ContentRootPath, "App_Data");
    var tokensPath = NullIfEmpty(api.DataPath) ?? Path.Combine(appData, "api");
    var jobsPath = NullIfEmpty(api.JobsPath) ?? Path.Combine(appData, "jobs");

    builder.Services.AddSingleton(sp => new ApiTokenStore(tokensPath, sp.GetRequiredService<ILogger<ApiTokenStore>>()));
    builder.Services.AddSingleton(sp =>
    {
        var log = sp.GetRequiredService<ILogger<ApiJobStore>>();
        var root = jobsPath;
        if (!Storage.IsWritable(root))
        {
            root = Path.Combine(Path.GetTempPath(), "watermarker", "api-jobs");
            log.LogWarning("API job folder '{Path}' is not writable (missing volume?): using {Fallback}.", jobsPath, root);
        }
        return new ApiJobStore(root, log);
    });
    builder.Services.AddSingleton<ApiRateLimiter>();
    builder.Services.AddHostedService<ApiJobWorker>();
    builder.Services.AddHostedService<ApiMaintenance>();
}

// Limiti di dimensione: Kestrel e parser multipart (sotto IIS va impostato anche maxAllowedContentLength).
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = maxBodyBytes + 1024 * 1024);
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = maxBodyBytes);

// Le elaborazioni sono pesanti (LibreOffice, FFmpeg): ne eseguiamo poche alla volta, le altre attendono in coda.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status503ServiceUnavailable;
    o.AddConcurrencyLimiter("processing", c =>
    {
        c.PermitLimit = Math.Max(1, portal.MaxConcurrentJobs);
        c.QueueLimit = Math.Max(0, portal.QueueLimit);
        c.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
    });
});

// Login con Entra ID: attivo solo se la sezione "AzureAd" è configurata.
bool authEnabled = builder.AddPortalAuthentication(portal);

var app = builder.Build();

app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "no-referrer";
    h["Content-Security-Policy"] = "default-src 'self'; img-src 'self' blob: data:; style-src 'self'; script-src 'self'; frame-ancestors 'none'";
    await next();
});

if (authEnabled) app.UsePortalAuthentication(portal);
else app.Logger.LogWarning("Authentication is DISABLED: configure the AzureAd section to require Entra ID sign-in.");

if (api.Enabled)
{
    app.UseApiTokens();
    // Crea subito archivio dei token e coda, così un problema di configurazione emerge all'avvio.
    _ = app.Services.GetRequiredService<ApiTokenStore>();
    _ = app.Services.GetRequiredService<ApiJobStore>();
}

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();

// ---------------------------------------------------------------- API

// Controllo di salute per Docker e per il bilanciatore. Con l'autenticazione dovrà restare anonimo.
app.MapGet("/healthz", () => Results.Text("ok"));

app.MapGet("/api/capabilities", (WatermarkEngine engine, PortalOptions options) => Results.Ok(new
{
    native = engine.NativeExtensions,
    pdf = engine.PdfExtensions,
    converterAvailable = engine.Converter is not null,
    maxUploadBytes = maxBodyBytes,
    maxTextLength = options.MaxTextLength,
    maxLines = WatermarkOptions.MaxLines,
    maxFiles = Math.Max(1, options.MaxFilesPerBatch),
    fonts = options.EffectiveFonts,
    placeholders = TextTemplate.Placeholders,
}));

// Utente collegato, mostrato nella pagina.
app.MapGet("/api/me", async (HttpContext http) =>
{
    bool isAdmin = api.Enabled && AdminEndpoints.IsAdmin(http, authEnabled, portal, app.Environment);
    if (!authEnabled) return Results.Ok(new { authEnabled = false, isAdmin });

    var user = PortalUser.From(http.User);
    var session = await http.AuthenticateAsync(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok(new
    {
        authEnabled = true,
        name = user?.Name,
        username = user?.Username,
        objectId = user?.ObjectId,
        tenantId = user?.TenantId,
        roles = user?.Roles ?? [],
        signedInAt = session.Properties?.IssuedUtc,
        sessionExpiresAt = session.Properties?.ExpiresUtc,
        signOutUrl = "/auth/signout",
        isAdmin,
    });
});

app.MapPost("/api/watermark", async (HttpContext http, WatermarkEngine engine, PortalOptions options,
                                     ILogger<Program> log, CancellationToken ct) =>
{
    if (!http.Request.HasFormContentType)
        return Problem(400, "Invalid request", "Send the file as multipart/form-data.");

    // Protezione CSRF: un form di un altro sito non può aggiungere header personalizzati.
    if (http.Request.Headers["X-Requested-With"] != "watermarker")
        return Problem(400, "Invalid request", "Missing request header.");

    var user = authEnabled ? PortalUser.From(http.User) : null;

    IFormCollection form;
    try
    {
        form = await http.Request.ReadFormAsync(ct);
    }
    catch (Exception ex) when (ex is InvalidDataException or BadHttpRequestException { StatusCode: 413 })
    {
        return Problem(413, "File too large", $"The maximum size is {options.MaxUploadMb} MB.");
    }

    var file = form.Files.GetFile("file");
    if (file is null || file.Length == 0)
        return Problem(400, "No file", "Choose the file to watermark.");

    // {user} ed {email} vengono dal token di Entra ID: l'utente non può falsificarli.
    var identity = new WatermarkIdentity(user?.Name, user?.Username);
    if (!Processing.TryReadOptions(form, options, file.FileName, identity, fixedText: null,
                                   out var wmOptions, out var mode, out var error))
        return Problem(400, "Invalid settings", error);

    var jobs = Path.Combine(engine.Settings.WorkDirectory, "jobs");
    Directory.CreateDirectory(jobs);
    var id = Guid.NewGuid().ToString("N");
    var input = Path.Combine(jobs, id + ".in");
    var sw = Stopwatch.StartNew();

    try
    {
        await using (var fs = File.Create(input))
            await file.CopyToAsync(fs, ct);

        var outcome = await Processing.RunAsync(engine, input, file.FileName, Path.Combine(jobs, id + ".out"), wmOptions, mode, ct);

        log.LogInformation(
            "Watermark applied: {File} ({Size} bytes, {Format}, output {Mode}) by {User} ({ObjectId}) from {Client} in {Ms} ms, {Warnings} warnings",
            file.FileName, file.Length, outcome.Format, mode, user?.Username ?? "anonymous", user?.ObjectId,
            http.Connection.RemoteIpAddress, sw.ElapsedMilliseconds, outcome.Warnings.Count);

        http.Response.Headers["X-Watermark-Id"] = id;
        http.Response.Headers["X-Watermark-Warnings"] =
            Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(outcome.Warnings));
        http.Response.Headers["X-Watermark-Format"] = Uri.EscapeDataString(outcome.Format);

        // Il file viene cancellato appena il download è terminato.
        return Results.File(Processing.OpenAndDeleteOnClose(outcome.OutputPath), outcome.ContentType, outcome.DownloadName);
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        return Results.Empty;
    }
    catch (Exception ex)
    {
        var (status, title, detail) = Processing.Classify(ex);
        Processing.LogFailure(log, ex, status, file.FileName);
        return Problem(status, title, detail);
    }
    finally
    {
        try { File.Delete(input); } catch { /* ripulito da JobCleanup */ }
    }
})
.DisableAntiforgery()
.RequireRateLimiting("processing");

if (api.Enabled)
{
    app.MapApiV1();
    app.MapAdmin(authEnabled, portal);
}

app.Run();

// ---------------------------------------------------------------- funzioni di supporto

static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

static IResult Problem(int status, string title, string detail) => Processing.Problem(status, title, detail);

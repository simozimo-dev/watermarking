namespace Watermarker.Web;

/// <summary>
/// Gestione dei token delle API, riservata a chi ha il ruolo amministrativo di Entra ID
/// (Portal:AdminRole, default "Watermark.Admin"). Senza login è disponibile solo nell'ambiente Development.
/// </summary>
public static class AdminEndpoints
{
    private const string LogCategory = "Watermarker.Web.Admin";

    public static bool IsAdmin(HttpContext http, bool authEnabled, PortalOptions portal, IHostEnvironment env) =>
        authEnabled
            ? !string.IsNullOrWhiteSpace(portal.AdminRole) && http.User.IsInRole(portal.AdminRole.Trim())
            : env.IsDevelopment();

    public static void MapAdmin(this WebApplication app, bool authEnabled, PortalOptions portal)
    {
        var g = app.MapGroup("/api/admin");

        g.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            if (!IsAdmin(http, authEnabled, portal, app.Environment))
                return Processing.Problem(403, "Access denied", authEnabled
                    ? $"Managing API tokens requires the '{portal.AdminRole}' role."
                    : "Managing API tokens requires Entra ID sign-in.");

            // Protezione CSRF, come per il caricamento dei file.
            if (!HttpMethods.IsGet(http.Request.Method) && http.Request.Headers["X-Requested-With"] != "watermarker")
                return Processing.Problem(400, "Invalid request", "Missing request header.");

            return await next(ctx);
        });

        g.MapGet("/tokens", (ApiTokenStore store, PortalOptions options, ApiOptions api) => Results.Ok(new
        {
            persistent = store.IsPersistent,
            defaults = new
            {
                maxUploadMb = options.MaxUploadMb,
                requestsPerMinute = api.DefaultRequestsPerMinute,
                maxActiveJobs = api.DefaultMaxActiveJobs,
                maxTextLength = options.MaxTextLength,
                resultRetentionHours = api.ResultRetentionHours,
            },
            tokens = store.List().Select(t => t.ToView()),
        }));

        g.MapPost("/tokens", (ApiTokenInput input, ApiTokenStore store, PortalOptions options, HttpContext http, ILoggerFactory logs) =>
        {
            if (ApiTokenStore.Validate(input, options, isNew: true) is { } error)
                return Processing.Problem(400, "Invalid token settings", error);

            var (token, value) = store.Create(input, Who(http));
            logs.CreateLogger(LogCategory).LogInformation(
                "API token {Name} ({Id}) created by {User}, expires {Expires}", token.Name, token.Id, Who(http), token.ExpiresAt);
            return Results.Created($"/api/admin/tokens/{token.Id}", new { token = token.ToView(), value });
        });

        g.MapPut("/tokens/{id}", (string id, ApiTokenInput input, ApiTokenStore store, PortalOptions options, HttpContext http,
                                  ILoggerFactory logs) =>
        {
            if (ApiTokenStore.Validate(input, options, isNew: false) is { } error)
                return Processing.Problem(400, "Invalid token settings", error);

            var token = store.Update(id, input, Who(http));
            if (token is null) return TokenNotFound();

            logs.CreateLogger(LogCategory).LogInformation(
                "API token {Name} ({Id}) updated by {User}: {Status}, expires {Expires}",
                token.Name, token.Id, Who(http), token.Status, token.ExpiresAt);
            return Results.Ok(token.ToView());
        });

        g.MapPost("/tokens/{id}/rotate", (string id, ApiTokenStore store, HttpContext http, ILoggerFactory logs) =>
        {
            var rotated = store.Rotate(id, Who(http));
            if (rotated is null) return TokenNotFound();
            var (token, value) = rotated.Value;

            logs.CreateLogger(LogCategory).LogInformation(
                "API token {Name} ({Id}) secret replaced by {User}", token.Name, id, Who(http));
            return Results.Ok(new { token = token.ToView(), value });
        });

        g.MapDelete("/tokens/{id}", (string id, ApiTokenStore store, HttpContext http, ILoggerFactory logs) =>
        {
            var token = store.Get(id);
            if (token is null || !store.Delete(id)) return TokenNotFound();

            logs.CreateLogger(LogCategory).LogInformation(
                "API token {Name} ({Id}) deleted by {User}", token.Name, id, Who(http));
            return Results.NoContent();
        });
    }

    private static string Who(HttpContext http) => PortalUser.From(http.User)?.Username ?? "development";

    private static IResult TokenNotFound() => Processing.Problem(404, "Token not found", "The token does not exist.");
}

using System.Net;

namespace Watermarker.Web;

/// <summary>
/// Pagina "Access denied" (403) nello stile del portale, generata dal server: chi non ha il ruolo
/// non riceve né la pagina richiesta né i suoi script.
/// </summary>
public static class AccessDenied
{
    /// <summary>Pagine riservate agli amministratori (gestione dei token delle API).</summary>
    private static readonly string[] AdminPaths = ["/admin.html", "/admin.js"];

    public static async Task WriteAsync(HttpContext ctx, string title, string message, bool canUsePortal, bool canSignOut)
    {
        var user = PortalUser.From(ctx.User);
        string E(string? s) => WebUtility.HtmlEncode(s ?? "");

        var account = user is null ? "" :
            $"""<p class="account">Signed in as <strong>{E(user.Name)}</strong>{(user.Username is null ? "" : $" ({E(user.Username)})")}.</p>""";
        var back = canUsePortal ? """<a class="primary" href="/">Back to the portal</a>""" : "";
        var signOut = canSignOut ? """<a class="secondary" href="/auth/signout">Sign in with another account</a>""" : "";

        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-store";
        await ctx.Response.WriteAsync($"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>Access denied · Document Watermark</title>
              <link rel="stylesheet" href="/app.css">
            </head>
            <body>
              <main class="signed-out">
                <h1>{E(title)}</h1>
                <p>{E(message)}</p>
                {account}
                <div class="buttons">{back}{signOut}</div>
              </main>
            </body>
            </html>
            """);
    }

    /// <summary>
    /// Blocca le pagine di amministrazione a chi non ha il ruolo amministrativo.
    /// Va prima dei file statici; le API /api/admin hanno già il proprio controllo.
    /// </summary>
    public static void UseAdminPageProtection(this WebApplication app, bool authEnabled, PortalOptions portal)
    {
        app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path;
            if (AdminPaths.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase))
                && !AdminEndpoints.IsAdmin(ctx, authEnabled, portal, app.Environment))
            {
                await WriteAsync(ctx,
                    "Access denied",
                    authEnabled
                        ? $"Managing API tokens requires the '{portal.AdminRole}' role. Ask your administrator to grant it, then sign in again."
                        : "Managing API tokens requires Entra ID sign-in.",
                    canUsePortal: true,
                    canSignOut: authEnabled);
                return;
            }
            await next();
        });
    }
}

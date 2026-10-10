using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Identity.Web;

namespace Watermarker.Web;

/// <summary>Dati dell'utente autenticato, ricavati dai claim del token di Entra ID.</summary>
public sealed record PortalUser(
    string Name,
    string? Username,
    string? ObjectId,
    string? TenantId,
    IReadOnlyList<string> Roles)
{
    private const string ObjectIdClaim = "http://schemas.microsoft.com/identity/claims/objectidentifier";
    private const string TenantIdClaim = "http://schemas.microsoft.com/identity/claims/tenantid";

    public static PortalUser? From(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true) return null;

        string? Find(params string[] types) =>
            types.Select(t => principal.FindFirst(t)?.Value).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        var username = Find("preferred_username", "upn", ClaimTypes.Upn, "email", ClaimTypes.Email);
        var name = Find("name", ClaimTypes.Name) ?? username ?? "Unknown user";
        var roles = principal.Claims
            .Where(c => c.Type is "roles" or ClaimTypes.Role)
            .Select(c => c.Value)
            .Distinct()
            .ToList();

        return new PortalUser(name, username, Find("oid", ObjectIdClaim), Find("tid", TenantIdClaim), roles);
    }
}

/// <summary>
/// Login con Microsoft Entra ID (OpenID Connect) e sessione con cookie.
///
/// Si attiva solo se la sezione "AzureAd" contiene TenantId e ClientId: senza, il portale
/// funziona come prima (utile in sviluppo) e lo segnala nel log e nella pagina.
/// Con l'autenticazione attiva tutto è protetto, pagina compresa, tranne /healthz e la pagina
/// di uscita. Le API rispondono 401 invece di reindirizzare al login, così la pagina può gestirlo.
/// </summary>
public static class PortalAuthentication
{
    /// <summary>Percorsi accessibili senza login.</summary>
    private static readonly string[] PublicPaths = ["/healthz", "/signed-out.html", "/app.css", "/favicon.ico"];

    public static bool AddPortalAuthentication(this WebApplicationBuilder builder, PortalOptions portal)
    {
        var azure = builder.Configuration.GetSection("AzureAd");
        bool enabled = !string.IsNullOrWhiteSpace(azure["TenantId"]) && !string.IsNullOrWhiteSpace(azure["ClientId"]);

        // Chiavi che proteggono i cookie: vanno conservate tra un riavvio e l'altro (e condivise tra repliche),
        // altrimenti a ogni riavvio del container gli utenti devono rifare il login.
        var dp = builder.Services.AddDataProtection().SetApplicationName("watermarker");
        if (!string.IsNullOrWhiteSpace(portal.DataProtectionPath))
        {
            if (IsWritable(portal.DataProtectionPath))
                dp.PersistKeysToFileSystem(new DirectoryInfo(portal.DataProtectionPath));
            else
                Console.Error.WriteLine(
                    $"warn: Data Protection key folder '{portal.DataProtectionPath}' is not writable (missing volume?). " +
                    "Keys are kept in memory: users will have to sign in again after every restart.");
        }

        if (!enabled) return false;

        if (string.IsNullOrWhiteSpace(azure["ClientSecret"]) && !azure.GetSection("ClientCredentials").GetChildren().Any())
            throw new InvalidOperationException(
                "AzureAd:ClientSecret is not set. Create a client secret in the app registration and pass it " +
                "as the environment variable AzureAd__ClientSecret.");

        builder.Services
            .AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
            .AddMicrosoftIdentityWebApp(azure);

        builder.Services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, o =>
        {
            o.TokenValidationParameters.NameClaimType = "name";
            o.TokenValidationParameters.RoleClaimType = "roles";
            o.SignedOutRedirectUri = "/signed-out.html";
        });

        builder.Services.Configure<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme, o =>
        {
            o.Cookie.Name = "wm.session";
            o.Cookie.HttpOnly = true;
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.ExpireTimeSpan = TimeSpan.FromHours(portal.SessionHours);
            o.SlidingExpiration = true;
        });

        builder.Services.AddAuthorization();
        return true;
    }

    private static bool IsWritable(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, $".probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Autenticazione, autorizzazione e blocco di tutto ciò che non è pubblico.</summary>
    public static void UsePortalAuthentication(this WebApplication app, PortalOptions portal)
    {
        app.UseAuthentication();
        app.UseAuthorization();

        var requiredRole = string.IsNullOrWhiteSpace(portal.RequiredRole) ? null : portal.RequiredRole.Trim();

        app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path;
            if (PublicPaths.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase)))
            {
                await next();
                return;
            }

            bool isApi = path.StartsWithSegments("/api");

            if (ctx.User.Identity?.IsAuthenticated != true)
            {
                if (isApi) ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                else await ctx.ChallengeAsync(OpenIdConnectDefaults.AuthenticationScheme,
                                              new AuthenticationProperties { RedirectUri = ctx.Request.Path + ctx.Request.QueryString });
                return;
            }

            if (requiredRole is not null && !ctx.User.IsInRole(requiredRole))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                if (isApi)
                {
                    await Results.Problem(statusCode: 403, title: "Access denied",
                        detail: $"Your account does not have the '{requiredRole}' role.").ExecuteAsync(ctx);
                }
                else
                {
                    ctx.Response.ContentType = "text/plain; charset=utf-8";
                    await ctx.Response.WriteAsync(
                        $"Access denied.\n\nYou are signed in as {PortalUser.From(ctx.User)?.Username}, but your account " +
                        $"does not have the '{requiredRole}' role required to use this portal.\n" +
                        "Ask your administrator to grant access, then sign in again: /auth/signout");
                }
                return;
            }

            await next();
        });

        // Uscita: chiude la sessione locale e quella di Entra ID, poi mostra la pagina pubblica di conferma.
        app.MapGet("/auth/signout", () => Results.SignOut(
            new AuthenticationProperties { RedirectUri = "/signed-out.html" },
            [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]));
    }
}

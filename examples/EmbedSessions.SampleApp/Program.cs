using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Azimap.CustomerExample;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true).AddEnvironmentVariables();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "Azimap.EmbedSample.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = 401;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = 403;
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddAzimapCustomerBackend(options => options.AuthorizeAsync = (context, mapId, operations, cancellationToken) =>
{
    var map = mapId.ToString(CultureInfo.InvariantCulture);
    var user = context.User;
    if (!user.HasClaim("azimap-map", map)) return Task.FromResult<CustomerMapGrant?>(null);
    var layers = user.FindAll("azimap-layer").Select(claim => claim.Value.Split(':'))
        .Where(parts => parts.Length == 2 && parts[0] == map)
        .Select(parts => int.TryParse(parts[1], out var id) ? id : 0).Where(id => id > 0).Distinct().ToArray();
    var editable = user.HasClaim("azimap-edit-map", map);
    if (operations.Count > 0 && (!editable || operations.Any(x => !layers.Contains(x.LayerId))))
        return Task.FromResult<CustomerMapGrant?>(null);
    // This single-user sample uses trusted claims. Real apps can query live permissions here.
    return Task.FromResult<CustomerMapGrant?>(new CustomerMapGrant(mapId, layers, editable,
        user.HasClaim("azimap-export-map", map), user.FindFirstValue(ClaimTypes.NameIdentifier)!));
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

var app = builder.Build();
var settings = app.Configuration.Get<SampleSettings>() ?? new SampleSettings();
var missing = settings.MissingSettings();
var passwordHasher = new PasswordHasher<string>();
var passwordHash = string.IsNullOrEmpty(settings.Sample.Password)
    ? null : passwordHasher.HashPassword(settings.Sample.Username, settings.Sample.Password);
app.Use(async (context, next) =>
{
    context.Response.Headers["Cache-Control"] = "no-store";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/api/bootstrap", (HttpContext context, IAntiforgery antiforgery) =>
{
    var authenticated = context.User.Identity?.IsAuthenticated == true;
    // Only this explicit public DTO reaches the browser. Never serialize settings.
    return Results.Json(new
    {
        authenticated,
        configured = missing.Length == 0,
        missingSettings = missing,
        csrfToken = antiforgery.GetAndStoreTokens(context).RequestToken,
        username = authenticated ? context.User.Identity!.Name : null,
        map = authenticated ? new
        {
            mapId = settings.Azimap.MapId,
            apiBaseUrl = settings.Azimap.ApiBaseUrl,
            authBaseUrl = settings.Azimap.IssuerBaseUrl,
            assetBaseUrl = settings.Azimap.AssetBaseUrl,
            layerContentApiUrl = settings.Azimap.LayerContentApiUrl,
            editable = context.User.HasClaim("azimap-edit-map", settings.Azimap.MapId.ToString(CultureInfo.InvariantCulture))
        } : null
    });
});

app.MapPost("/api/login", async (LoginRequest request, HttpContext context, IAntiforgery antiforgery) =>
{
    try { await antiforgery.ValidateRequestAsync(context); }
    catch (AntiforgeryValidationException) { return Results.BadRequest(); }
    if (missing.Length != 0) return Results.Problem("Complete the server configuration listed on the setup screen.", statusCode: 503);
    if (string.IsNullOrEmpty(request.Password) || request.Password.Length > 1024 || passwordHash == null)
        return Results.Unauthorized();
    var validPassword = passwordHasher.VerifyHashedPassword(settings.Sample.Username, passwordHash, request.Password)
        != PasswordVerificationResult.Failed;
    if (!validPassword || !string.Equals(request.Username, settings.Sample.Username, StringComparison.Ordinal))
        return Results.Unauthorized();

    var mapId = settings.Azimap.MapId.ToString(CultureInfo.InvariantCulture);
    var claims = new List<Claim>
    {
        new(ClaimTypes.NameIdentifier, "sample-user"),
        new(ClaimTypes.Name, settings.Sample.Username),
        new("azimap-map", mapId)
    };
    // Grants come from server configuration, never from login/session request fields.
    claims.AddRange(settings.Azimap.LayerIds.Select(id => new Claim("azimap-layer", $"{mapId}:{id}")));
    if (settings.Sample.AllowEditing) claims.Add(new Claim("azimap-edit-map", mapId));
    if (settings.Sample.AllowExport) claims.Add(new Claim("azimap-export-map", mapId));
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
    return Results.NoContent();
}).RequireRateLimiting("login");

app.MapPost("/api/logout", async (HttpContext context, IAntiforgery antiforgery) =>
{
    try { await antiforgery.ValidateRequestAsync(context); }
    catch (AntiforgeryValidationException) { return Results.BadRequest(); }
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.NoContent();
}).RequireAuthorization();

app.MapAzimapCustomerBackend();
app.Run();

public partial class Program { }
public sealed record LoginRequest(string Username, string Password);

public sealed class SampleSettings
{
    public AzimapSettings Azimap { get; set; } = new();
    public LoginSettings Sample { get; set; } = new();

    public string[] MissingSettings()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Azimap.ApiKey)) errors.Add("Azimap:ApiKey");
        if (Azimap.MapId <= 0) errors.Add("Azimap:MapId (positive integer)");
        if (Azimap.LayerIds.Length == 0 || Azimap.LayerIds.Any(id => id <= 0)) errors.Add("Azimap:LayerIds (positive integers)");
        if (!ValidUrl(Azimap.IssuerBaseUrl)) errors.Add("Azimap:IssuerBaseUrl");
        if (!ValidUrl(Azimap.ApiBaseUrl)) errors.Add("Azimap:ApiBaseUrl");
        if (!ValidUrl(Azimap.AssetBaseUrl)) errors.Add("Azimap:AssetBaseUrl");
        if (!ValidUrl(Azimap.LayerContentApiUrl)) errors.Add("Azimap:LayerContentApiUrl");
        if (string.IsNullOrWhiteSpace(Sample.Username)) errors.Add("Sample:Username");
        if (string.IsNullOrEmpty(Sample.Password) || Sample.Password.Length < 12) errors.Add("Sample:Password (at least 12 characters)");
        return errors.ToArray();
    }

    private static bool ValidUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == "https" || (uri.Scheme == "http" && uri.IsLoopback))
        && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);
}

public sealed class AzimapSettings
{
    public string ApiKey { get; set; } = "";
    public int MapId { get; set; }
    public int[] LayerIds { get; set; } = [];
    public string IssuerBaseUrl { get; set; } = "https://app.azimap.com/";
    public string ApiBaseUrl { get; set; } = "https://api.azimap.com/";
    public string AssetBaseUrl { get; set; } = "https://app.azimap.com/azimap-runtime/";
    public string LayerContentApiUrl { get; set; } = "https://tiles.azimap.com/";
}

public sealed class LoginSettings
{
    public string Username { get; set; } = "demo";
    public string Password { get; set; } = "";
    public bool AllowEditing { get; set; }
    public bool AllowExport { get; set; }
}

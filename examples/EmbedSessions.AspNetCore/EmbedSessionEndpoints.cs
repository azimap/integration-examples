using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Azimap.CustomerExample;

public sealed record CustomerWriteOperation(int LayerId, string Operation);
public sealed record CustomerMapGrant(int MapId, IReadOnlyList<int> LayerIds, bool Editable,
    bool CanExport, string ExternalUserId);
public sealed record CustomerWriteRequest(string SessionToken, List<CustomerWriteOperation> Operations);

public sealed class AzimapCustomerBackendOptions
{
    // The callback must consult trusted permissions on EVERY acquisition.
    // An empty operation list means a request to view the map.
    public Func<HttpContext, int, IReadOnlyList<CustomerWriteOperation>, CancellationToken,
        Task<CustomerMapGrant?>>? AuthorizeAsync { get; set; }
}

public static class EmbedSessionEndpoints
{
    private static readonly HashSet<string> Operations = new(StringComparer.Ordinal)
        { "Create", "CreateGeometry", "Edit", "Delete" };

    public static IServiceCollection AddAzimapCustomerBackend(this IServiceCollection services,
        Action<AzimapCustomerBackendOptions> configure)
    {
        services.Configure(configure);
        services.AddAntiforgery(options => options.HeaderName = "X-CSRF-Token");
        services.AddHttpClient("AzimapSessions", client => client.Timeout = TimeSpan.FromSeconds(20))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        return services;
    }

    public static void MapAzimapCustomerBackend(this IEndpointRouteBuilder endpoints, string prefix = "/azimap")
    {
        var group = endpoints.MapGroup(prefix).RequireAuthorization();
        group.MapGet("/csrf", (HttpContext context, IAntiforgery antiforgery) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(new { csrfToken = antiforgery.GetAndStoreTokens(context).RequestToken });
        });
        group.MapPost("/maps/{mapId:int}/session", (int mapId, HttpContext context,
            IAntiforgery antiforgery, IConfiguration configuration, IHttpClientFactory clients,
            IOptions<AzimapCustomerBackendOptions> options) =>
            Issue(mapId, null, context, antiforgery, configuration, clients, options.Value));
        group.MapPost("/maps/{mapId:int}/write-token", (int mapId, CustomerWriteRequest request, HttpContext context,
            IAntiforgery antiforgery, IConfiguration configuration, IHttpClientFactory clients,
            IOptions<AzimapCustomerBackendOptions> options) =>
            Issue(mapId, request, context, antiforgery, configuration, clients, options.Value));
    }

    private static async Task<IResult> Issue(int mapId, CustomerWriteRequest? write, HttpContext context,
        IAntiforgery antiforgery, IConfiguration configuration, IHttpClientFactory clients,
        AzimapCustomerBackendOptions options)
    {
        context.Response.Headers.CacheControl = "no-store";
        try { await antiforgery.ValidateRequestAsync(context); }
        catch (AntiforgeryValidationException) { return Results.BadRequest(); }
        if (mapId <= 0 || write != null && (string.IsNullOrWhiteSpace(write.SessionToken)
            || write.Operations == null || write.Operations.Count == 0 || write.Operations.Count > 128
            || write.Operations.Any(x => x == null || x.LayerId <= 0 || !Operations.Contains(x.Operation))))
            return Results.BadRequest();
        var operations = (IReadOnlyList<CustomerWriteOperation>?)write?.Operations ?? Array.Empty<CustomerWriteOperation>();
        var grant = options.AuthorizeAsync == null ? null
            : await options.AuthorizeAsync(context, mapId, operations, context.RequestAborted);
        if (grant == null || grant.MapId != mapId || grant.LayerIds == null || grant.LayerIds.Count == 0
            || grant.LayerIds.Any(id => id <= 0) || string.IsNullOrWhiteSpace(grant.ExternalUserId)
            || write != null && (!grant.Editable || operations.Any(x => !grant.LayerIds.Contains(x.LayerId))))
            return Results.Forbid();

        var issuer = configuration["Azimap:IssuerBaseUrl"];
        var key = configuration["Azimap:ApiKey"];
        if (string.IsNullOrWhiteSpace(key) || !Uri.TryCreate(issuer, UriKind.Absolute, out var issuerUri)
            || !(issuerUri.Scheme == "https" || issuerUri.Scheme == "http" && issuerUri.IsLoopback)
            || !string.IsNullOrEmpty(issuerUri.UserInfo) || !string.IsNullOrEmpty(issuerUri.Query) || !string.IsNullOrEmpty(issuerUri.Fragment))
            return Results.StatusCode(503);
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(issuer!.TrimEnd('/') + "/"),
            write == null ? "api/embed/sessions" : "api/embed/write-tokens"));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        message.Content = write == null
            ? JsonContent.Create(new { mapId, layerIds = grant.LayerIds.Distinct().ToArray(), readWrite = grant.Editable, canExport = grant.CanExport, externalUserId = grant.ExternalUserId })
            : JsonContent.Create(new { mapId, sessionToken = write.SessionToken, operations, externalUserId = grant.ExternalUserId });
        try
        {
            using var response = await clients.CreateClient("AzimapSessions").SendAsync(message, context.RequestAborted);
            if (!response.IsSuccessStatusCode) return Results.StatusCode((int)response.StatusCode);
            // Explicit DTOs prevent accidental forwarding of upstream internals or parent credentials.
            if (write != null)
            {
                var token = await response.Content.ReadFromJsonAsync<WriteResponse>(cancellationToken: context.RequestAborted);
                return token == null || string.IsNullOrWhiteSpace(token.AccessToken) || token.ExpiresInSeconds <= 0
                    ? Results.StatusCode(502) : Results.Json(token);
            }
            var session = await response.Content.ReadFromJsonAsync<SessionResponse>(cancellationToken: context.RequestAborted);
            return session == null || string.IsNullOrWhiteSpace(session.AccessToken) || session.MapId != mapId || session.ExpiresInSeconds <= 0
                ? Results.StatusCode(502) : Results.Json(session);
        }
        catch (HttpRequestException) { return Results.StatusCode(503); }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested) { return Results.StatusCode(503); }
        catch (JsonException) { return Results.StatusCode(502); }
    }

    private sealed record WriteResponse(string AccessToken, int ExpiresInSeconds);
    private sealed record SessionResponse(string AccessToken, int ExpiresInSeconds, int MapId,
        bool Editable, bool CanExport, string ScopeKey);
}

using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Azimap.CustomerExample;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

public sealed class MvcIntegrationTests
{
    [Fact]
    public async Task MvcControllersAndAdapterShareAuthenticationCsrfAndLivePermissionChecks()
    {
        var allowed = true;
        var checks = 0;
        var issuer = new Issuer();
        using var server = new TestServer(new WebHostBuilder()
            .ConfigureAppConfiguration((_, builder) => builder.AddInMemoryCollection(new Dictionary<string, string?> {
                ["Azimap:IssuerBaseUrl"] = "https://issuer.example/", ["Azimap:ApiKey"] = "server-secret"
            }))
            .ConfigureServices(services =>
            {
                services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthentication>("Test", _ => { });
                services.AddAuthorization();
                services.AddControllersWithViews().AddApplicationPart(typeof(MvcMapTestController).Assembly);
                services.AddAzimapCustomerBackend(options => options.AuthorizeAsync = (context, mapId, operations, cancellation) =>
                {
                    checks++;
                    return Task.FromResult<CustomerMapGrant?>(allowed && mapId == 12 && operations.All(x => x.LayerId == 31 && x.Operation == "Edit")
                        ? new CustomerMapGrant(12, new[] { 31 }, true, false, context.User.FindFirstValue(ClaimTypes.NameIdentifier)!) : null);
                });
                services.AddHttpClient("AzimapSessions").ConfigurePrimaryHttpMessageHandler(() => issuer);
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(endpoints => { endpoints.MapControllers(); endpoints.MapAzimapCustomerBackend(); });
            }));
        using var client = server.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/mvc-map/12")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/azimap/maps/12/session", null)).StatusCode);
        Assert.Equal(0, checks);
        client.DefaultRequestHeaders.Add("X-Test-User", "alice");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/mvc-map/12")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/azimap/maps/12/session", null)).StatusCode);
        Assert.Equal(0, checks);
        var csrf = await client.GetAsync("/azimap/csrf");
        var data = await csrf.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Add("Cookie", string.Join("; ", csrf.Headers.GetValues("Set-Cookie").Select(x => x.Split(';')[0])));
        client.DefaultRequestHeaders.Add("X-CSRF-Token", data.GetProperty("csrfToken").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/azimap/maps/12/session", null)).StatusCode);
        var request = new { sessionToken = "read", operations = new[] { new { layerId = 31, operation = "Edit" } } };
        var write = await client.PostAsJsonAsync("/azimap/maps/12/write-token", request);
        Assert.Equal(HttpStatusCode.OK, write.StatusCode);
        Assert.DoesNotContain("server-secret", await write.Content.ReadAsStringAsync());
        Assert.True(write.Headers.CacheControl!.NoStore);
        Assert.Equal(2, issuer.Calls);
        allowed = false;
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/azimap/maps/12/write-token", request)).StatusCode);
        Assert.Equal(3, checks);
        Assert.Equal(2, issuer.Calls);
    }

    private sealed class Issuer : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal("Bearer server-secret", request.Headers.Authorization!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = JsonContent.Create(new { accessToken = "child", expiresInSeconds = 120, mapId = 12, editable = true, canExport = false, scopeKey = "scope" })
            });
        }
    }

    // Test-only authentication. Real customers keep their existing cookie/OIDC login.
    private sealed class TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-User", out var user)) return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, user.ToString()) }, "Test");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
        }
    }
}

[Authorize]
public sealed class MvcMapTestController : Controller
{
    [HttpGet("mvc-map/{mapId:int}")]
    public IActionResult Map(int mapId) => Json(new { mapId });
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public sealed class SampleAppTests
{
    [Fact]
    public async Task UnconfiguredAppShowsSetupAndHelpWithoutExposingSecrets()
    {
        using var app = new SampleFactory(configured: false);
        using var client = app.Client();
        var state = await client.GetFromJsonAsync<JsonElement>("/api/bootstrap");
        Assert.False(state.GetProperty("configured").GetBoolean());
        Assert.Contains("Azimap:ApiKey", state.GetProperty("missingSettings").ToString());
        Assert.Contains("Setup and integration help", await client.GetStringAsync("/"));
        Assert.Contains("any backend", await client.GetStringAsync("/help.html"));
        Assert.DoesNotContain(SampleFactory.Secret, state.ToString());
    }

    [Fact]
    public async Task AnonymousSessionRequestIsDeniedWithoutCallingAzimap()
    {
        using var app = new SampleFactory();
        using var client = app.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/azimap/maps/123/session", null)).StatusCode);
        Assert.Equal(0, app.Issuer.Calls);
    }

    [Fact]
    public async Task LoginRejectsMissingCsrfAndIncorrectPassword()
    {
        using var app = new SampleFactory();
        using var client = app.Client();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/login", new { username = "demo", password = SampleFactory.Password })).StatusCode);
        await Csrf(client);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/login", new { username = "demo", password = "incorrect" })).StatusCode);
        Assert.Equal(0, app.Issuer.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionForwardsOnlyServerGrantsAndKeepsParentKeyOnBackend(bool editing)
    {
        using var app = new SampleFactory(editing: editing);
        using var client = app.Client();
        await Login(client);
        // The browser cannot override any grant by supplying its own request body.
        var response = await client.PostAsJsonAsync("/azimap/maps/123/session", new { mapId = 999, layerIds = new[] { 888 }, readWrite = true, canExport = true });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("browser-session", json);
        Assert.DoesNotContain(SampleFactory.Secret, json);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal("Bearer " + SampleFactory.Secret, app.Issuer.Authorization);
        Assert.Equal("https://issuer.example/api/embed/sessions", app.Issuer.Url);
        var sent = app.Issuer.Body;
        Assert.Equal(123, sent.GetProperty("mapId").GetInt32());
        Assert.Equal(new[] { 456, 457 }, sent.GetProperty("layerIds").EnumerateArray().Select(x => x.GetInt32()));
        Assert.Equal(editing, sent.GetProperty("readWrite").GetBoolean());
        Assert.False(sent.GetProperty("canExport").GetBoolean());
        Assert.Equal("sample-user", sent.GetProperty("externalUserId").GetString());
        var state = await client.GetStringAsync("/api/bootstrap");
        Assert.DoesNotContain(SampleFactory.Secret, state);
        Assert.DoesNotContain(SampleFactory.Password, state);
    }

    [Fact]
    public async Task SessionRequiresCsrfAndAuthorizedMap()
    {
        using var app = new SampleFactory();
        using var client = app.Client();
        await Login(client);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/azimap/maps/999/session", null)).StatusCode);
        client.DefaultRequestHeaders.Remove("X-CSRF-Token");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/azimap/maps/123/session", null)).StatusCode);
        Assert.Equal(0, app.Issuer.Calls);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(503)]
    public async Task UpstreamDenialIsPreservedWithoutReturningItsBody(int status)
    {
        using var app = new SampleFactory();
        app.Issuer.Status = (HttpStatusCode)status;
        using var client = app.Client();
        await Login(client);
        var response = await client.PostAsync("/azimap/maps/123/session", null);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.DoesNotContain(SampleFactory.Secret, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task LogoutRequiresCsrfAndPreventsFurtherSessions()
    {
        using var app = new SampleFactory();
        using var client = app.Client();
        await Login(client);
        client.DefaultRequestHeaders.Remove("X-CSRF-Token");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/logout", null)).StatusCode);
        await Csrf(client);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/azimap/maps/123/session", null)).StatusCode);
        Assert.Equal(0, app.Issuer.Calls);
    }

    [Theory]
    [InlineData(false, 456, "Edit", 403)]
    [InlineData(true, 999, "Edit", 403)]
    [InlineData(true, 456, "Admin", 400)]
    [InlineData(true, 456, "Edit", 200)]
    public async Task WritesRequireAuthenticatedServerApprovedIntent(bool editing, int layerId, string operation, int expected)
    {
        using var app = new SampleFactory(editing: editing);
        using var client = app.Client();
        await Login(client);
        var response = await client.PostAsJsonAsync("/azimap/maps/123/write-token", new {
            sessionToken = "read-session", externalUserId = "forged", operations = new[] { new { layerId, operation } }
        });
        Assert.Equal(expected, (int)response.StatusCode);
        Assert.Equal(expected == 200 ? 1 : 0, app.Issuer.Calls);
        if (expected == 200)
        {
            Assert.Equal("https://issuer.example/api/embed/write-tokens", app.Issuer.Url);
            Assert.Equal("sample-user", app.Issuer.Body.GetProperty("externalUserId").GetString());
            Assert.Equal("read-session", app.Issuer.Body.GetProperty("sessionToken").GetString());
            var json = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(SampleFactory.Secret, json);
            Assert.DoesNotContain("mapId", json);
        }
    }

    [Fact]
    public async Task DashboardSessionUsesTrustedReadOnlyGrantsOnEveryAcquisition()
    {
        using var app = new SampleFactory(editing: true, dashboardId: 789);
        using var client = app.Client();
        const string path = "/azimap/dashboards/789/session";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync(path, null)).StatusCode);
        await Login(client);
        var bootstrap = await client.GetFromJsonAsync<JsonElement>("/api/bootstrap");
        Assert.Equal(789, bootstrap.GetProperty("dashboardId").GetInt32());

        for (var acquisition = 1; acquisition <= 2; acquisition++)
        {
            var response = await client.PostAsJsonAsync(path, new
            {
                dashboardId = 999, mapId = 999, layerIds = new[] { 888 }, readWrite = true, canExport = true,
                externalUserId = "forged"
            });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl!.NoStore);
            Assert.Equal(acquisition, app.Issuer.Calls);
            Assert.Equal("Bearer " + SampleFactory.Secret, app.Issuer.Authorization);
            Assert.Equal("https://issuer.example/api/embed/sessions", app.Issuer.Url);
            Assert.Equal(789, app.Issuer.Body.GetProperty("dashboardId").GetInt32());
            Assert.Equal(123, app.Issuer.Body.GetProperty("mapId").GetInt32());
            Assert.Equal(new[] { 456, 457 }, app.Issuer.Body.GetProperty("layerIds").EnumerateArray().Select(x => x.GetInt32()));
            Assert.False(app.Issuer.Body.GetProperty("readWrite").GetBoolean());
            Assert.False(app.Issuer.Body.GetProperty("canExport").GetBoolean());
            Assert.Equal("sample-user", app.Issuer.Body.GetProperty("externalUserId").GetString());

            var session = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal($"dashboard-session-{acquisition}", session.GetProperty("accessToken").GetString());
            Assert.Equal(300, session.GetProperty("expiresInSeconds").GetInt32());
            Assert.Equal(789, session.GetProperty("dashboardId").GetInt32());
            Assert.Equal(123, session.GetProperty("mapId").GetInt32());
            Assert.False(session.GetProperty("editable").GetBoolean());
            Assert.False(session.GetProperty("canExport").GetBoolean());
            Assert.Equal("sample-dashboard-scope", session.GetProperty("scopeKey").GetString());
            Assert.DoesNotContain(SampleFactory.Secret, session.ToString());
        }

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync(path, null)).StatusCode);
        Assert.Equal(2, app.Issuer.Calls);
    }

    [Theory]
    [InlineData(0, 789, true, 403)]
    [InlineData(789, 798, true, 403)]
    [InlineData(789, 789, false, 400)]
    public async Task DashboardSessionRejectsUnconfiguredDashboardWrongDashboardOrMissingCsrf(
        int configuredDashboardId, int requestedDashboardId, bool sendCsrf, int expected)
    {
        using var app = new SampleFactory(dashboardId: configuredDashboardId);
        using var client = app.Client();
        await Login(client);
        if (!sendCsrf) client.DefaultRequestHeaders.Remove("X-CSRF-Token");
        var response = await client.PostAsync($"/azimap/dashboards/{requestedDashboardId}/session", null);
        Assert.Equal(expected, (int)response.StatusCode);
        Assert.Equal(0, app.Issuer.Calls);
    }

    [Theory]
    [InlineData(798, 123, false, false, "scope")]
    [InlineData(789, 999, false, false, "scope")]
    [InlineData(789, 123, true, false, "scope")]
    [InlineData(789, 123, false, true, "scope")]
    [InlineData(789, 123, false, false, "")]
    public async Task DashboardSessionRejectsWrongResourceOrUnexpectedCapabilities(
        int dashboardId, int mapId, bool editable, bool canExport, string scopeKey)
    {
        using var app = new SampleFactory(dashboardId: 789);
        app.Issuer.DashboardResponse = new { accessToken = SampleFactory.Secret, expiresInSeconds = 300, dashboardId, mapId, editable, canExport, scopeKey };
        using var client = app.Client();
        await Login(client);

        var response = await client.PostAsync("/azimap/dashboards/789/session", null);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.DoesNotContain(SampleFactory.Secret, await response.Content.ReadAsStringAsync());
    }

    private static async Task Csrf(HttpClient client)
    {
        var state = await client.GetFromJsonAsync<JsonElement>("/api/bootstrap");
        client.DefaultRequestHeaders.Remove("X-CSRF-Token");
        client.DefaultRequestHeaders.Add("X-CSRF-Token", state.GetProperty("csrfToken").GetString());
    }

    private static async Task Login(HttpClient client)
    {
        await Csrf(client);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/login", new { username = "demo", password = SampleFactory.Password })).StatusCode);
        await Csrf(client);
    }

    private sealed class SampleFactory(bool configured = true, bool editing = false, int dashboardId = 0) : WebApplicationFactory<Program>
    {
        public const string Secret = "test-server-key-never-returned";
        public const string Password = "test-password-for-local-sample";
        public IssuerHandler Issuer { get; } = new();
        public HttpClient Client() => CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../EmbedSessions.SampleApp")));
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Azimap:ApiKey"] = configured ? Secret : "",
                ["Azimap:MapId"] = "123",
                ["Azimap:DashboardId"] = dashboardId.ToString(),
                ["Azimap:LayerIds:0"] = "456",
                ["Azimap:LayerIds:1"] = "457",
                ["Azimap:IssuerBaseUrl"] = "https://issuer.example/",
                ["Sample:Username"] = "demo",
                ["Sample:Password"] = Password,
                ["Sample:AllowEditing"] = editing.ToString(),
                ["Sample:AllowExport"] = "false"
            }));
            builder.ConfigureServices(services => services.AddHttpClient("AzimapSessions").ConfigurePrimaryHttpMessageHandler(() => Issuer));
        }
    }

    private sealed class IssuerHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Authorization { get; private set; }
        public string? Url { get; private set; }
        public JsonElement Body { get; private set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public object? DashboardResponse { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Authorization = request.Headers.Authorization?.ToString();
            Url = request.RequestUri?.ToString();
            Body = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);
            if (Status == HttpStatusCode.OK && Body.TryGetProperty("dashboardId", out var dashboardId))
            {
                return new HttpResponseMessage(Status)
                {
                    Content = JsonContent.Create(DashboardResponse ?? new
                    {
                        accessToken = $"dashboard-session-{Calls}", expiresInSeconds = 300,
                        dashboardId = dashboardId.GetInt32(), mapId = 123,
                        editable = false, canExport = false, scopeKey = "sample-dashboard-scope", internalField = SampleFactory.Secret
                    })
                };
            }
            return new HttpResponseMessage(Status)
            {
                Content = Status == HttpStatusCode.OK
                    ? JsonContent.Create(new { accessToken = "browser-session", expiresInSeconds = 300, mapId = 123, editable = Body.TryGetProperty("readWrite", out var writable) && writable.GetBoolean(), canExport = false, scopeKey = "sample-scope" })
                    : new StringContent(SampleFactory.Secret)
            };
        }
    }
}

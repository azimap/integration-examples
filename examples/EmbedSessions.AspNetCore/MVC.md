# ASP.NET Core MVC integration (.NET 10)

Use the existing `EmbedSessions.AspNetCore` adapter. ASP.NET Core MVC and the
adapter's endpoint routes can run in the same application, with the same login,
authorization services and antiforgery support. There is no separate API-key or
token implementation to maintain.

This targets ASP.NET Core MVC on .NET 10. Classic ASP.NET MVC 5 / System.Web is a
different host and cannot reference this library; it would need its own bridge to
the documented HTTP contract.

## Register the backend

Reference `EmbedSessions.AspNetCore.csproj` and import `Azimap.CustomerExample`.
Keep the application's existing cookie/OIDC login configuration. Add:

```csharp
builder.Services.AddControllersWithViews();
builder.Services.AddAzimapCustomerBackend(options =>
{
    options.AuthorizeAsync = (context, mapId, operations, cancellation) =>
        context.RequestServices.GetRequiredService<ICustomerMapPermissions>()
            .AuthorizeAsync(context.User, mapId, operations, cancellation);
});

var app = builder.Build();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapAzimapCustomerBackend();
app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
app.Run();
```

`ICustomerMapPermissions` is your application service, registered with DI. Its
method returns `Task<CustomerMapGrant?>`. Return null to deny. Otherwise return
`new CustomerMapGrant(mapId, allowedLayerIds, editable, canExport, trustedUserId)`.
An empty operations list requests map viewing. For writes, check EVERY requested
layer/operation against current permissions. Keep `Azimap:ApiKey` and
`Azimap:IssuerBaseUrl` in server configuration; never include the key in a viewmodel.
See the [adapter README](README.md) for the complete callback contract.

The adapter uses the default authentication scheme. If your application selects
schemes through MVC-only filters, also configure the adapter's default scheme or
a default authorization policy covering that scheme. Preserve the app's existing
antiforgery convention: both this helper and adapter use `X-CSRF-Token`.

For cookie authentication, ensure `/azimap` requests return 401/403 instead of an
HTML login or access-denied redirect. Review your cookie redirect handlers and
configure API status responses for this path. Preserve normal login
redirects for MVC page requests.

## Render the map in a Razor view

Use your existing authorized controller to choose the map and supply a numeric
map ID to its viewmodel. Authorize the selection on the server; the adapter also
checks it for every session and write request. For example, a view using your
`MapPageViewModel`:

```cshtml
@model MapPageViewModel
<div id="customer-map" data-map-id="@Model.MapId" style="height: 600px"></div>
<script src="https://app.azimap.com/azimap-runtime/dist/azimap-embed-loader.min.js"></script>
<script src="~/js/customer-map.js" asp-append-version="true"></script>
```

`wwwroot/js/customer-map.js`:

```javascript
(async () => {
    const element = document.getElementById('customer-map');
    const map = await Azimap.init({
        target: '#customer-map',
        mapId: Number(element.dataset.mapId),
        apiBaseUrl: 'https://api.azimap.com/',
        assetBaseUrl: 'https://app.azimap.com/azimap-runtime/',
        layerContentApiUrl: 'https://tiles.azimap.com/',
        editable: true,
        auth: Azimap.customerBackend('/azimap')
    });
    // Retain map if this page needs renewSession() or destroy().
})();
```

The service URLs above are Azimap Cloud endpoints. If your app is hosted under an application
path, include that path in the helper URL, for example `/customer-app/azimap`.
The adapter internally handles the CSRF endpoint and protects both POSTs; the view
needs no separate hidden antiforgery form. Keep antiforgery protection for the
rest of your MVC application.

`MvcIntegrationTests` verifies MVC controller routes and the existing adapter
coexist, share authentication, enforce CSRF and recheck write permissions:

```bash
dotnet test ../EmbedSessions.SampleApp.Tests/EmbedSessions.SampleApp.Tests.csproj -f net10.0
```

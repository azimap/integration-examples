# Customer backend adapter

This .NET 10 library adds authenticated session and write-token endpoints to an
existing ASP.NET Core application. For a runnable login and map, see
[the sample app](../EmbedSessions.SampleApp/README.md). A
[Node adapter](../EmbedSessions.Node/README.md) and
[Python adapter](../EmbedSessions.Python/README.md) implement the same HTTP contract.
Users sign in to the containing application and need no Azimap account.
For ASP.NET Core MVC, use this same adapter with the [MVC integration guide](MVC.md).

Reference this project and import `Azimap.CustomerExample`:

```csharp
builder.Services.AddAzimapCustomerBackend(options =>
{
    options.AuthorizeAsync = async (context, mapId, operations, cancellation) =>
    {
        // Your implementation queries current permissions for context.User.
        // Empty operations means a view-session request. For a write, check
        // EVERY layer/operation pair before returning a grant, or return null.
        return await permissions.AuthorizeMapAsync(
            context.User, mapId, operations, cancellation);
    };
});
// Keep your existing authentication and authorization middleware.
app.MapAzimapCustomerBackend();
```

`permissions` above is your application's permission service. It returns
`CustomerMapGrant(mapId, layerIds, editable, canExport, externalUserId)` or null.
Do not derive this grant or identity from browser input. Check current permission
on every callback, including each write; a cached login claim alone does not
provide immediate permission revocation. The adapter checks map/layer membership
and the edit flag, while your callback enforces the requested operations.
Azimap also limits every request to the parent API key's grants.

Set `Azimap:IssuerBaseUrl` to `https://app.azimap.com/` and put `Azimap:ApiKey` in your server's
secret store. Use HTTPS for Azimap Cloud requests. Configure
the key's whitelist for the browser origin and the backend's actual egress IP.
The adapter does not forward upstream error bodies, follow issuer redirects,
or expose the permanent key.

Browser configuration:

```javascript
const instance = await Azimap.init({
    target: '#map', mapId: 123,
    apiBaseUrl: 'https://api.azimap.com/',
    assetBaseUrl: 'https://app.azimap.com/azimap-runtime/',
    layerContentApiUrl: 'https://tiles.azimap.com/',
    editable: true,
    auth: Azimap.customerBackend('/azimap')
});
```

The helper uses the app's login cookie and obtains antiforgery tokens internally:

| Endpoint | Purpose |
| --- | --- |
| `GET /azimap/csrf` | Authenticated CSRF request token |
| `POST /azimap/maps/{mapId}/session` | Reusable read session, up to five minutes |
| `POST /azimap/maps/{mapId}/write-token` | One mutation request, scoped to specified layer/operation pairs |

Both POSTs require `X-CSRF-Token`. The write body contains `sessionToken` and
`operations`, for example `[{"layerId":456,"operation":"Edit"}]`.
Operations are `Create`, `CreateGeometry`, `Edit`, and `Delete`.
Responses are `Cache-Control: no-store`. The helper does not replay failed writes.

The adapter connects to Azimap Cloud at `https://app.azimap.com/`. Configure your
API key for the permitted browser origins and backend egress IP. See the
[Cloud integration guide](../../README.md) for scope, expiry and supported operations.
Layer permissions cover all records in the layer; this adapter does not implement
row-level authorization.

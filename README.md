# Azimap Embed Integrations

Embed native Azimap JavaScript maps in your application using your existing login and permissions. Your users do not need an Azimap account.

This repository provides backend adapters, integration guides and a runnable sample application.

## Available integrations

| Integration | Description |
|---|---|
| ASP.NET Core / MVC | .NET 10 adapter, with MVC and Razor integration guidance |
| Node.js | Reference adapter using your existing authentication and CSRF services |
| Python | Dependency-free, synchronous WSGI adapter |
| Sample application | ASP.NET Core application demonstrating login, map loading, renewal and editing authorization |

The .NET adapter targets ASP.NET Core. Classic ASP.NET MVC 5 / System.Web requires a separate bridge.

## How it works

Your application keeps its permanent Azimap API key on its backend.

1. A user signs in through your application.
2. Your backend checks their permissions and requests a short-lived, read-only map session from Azimap.
3. The browser loads the map directly from Azimap.
4. Before each supported edit, the Azimap JavaScript library requests a write token through your backend.
5. Your backend checks the user’s current permission for the requested layer and operation.
6. Azimap issues a scoped, single-use write token. The browser submits the edit directly to Azimap, which validates the actual operation and consumes the token.

Your backend handles authorization requests. Map data flows directly between the browser and Azimap.

## Frontend setup

Load the Azimap embed loader and provide a map container:

```html
<div id="map" style="height: 600px"></div>

<script src="https://YOUR_AZIMAP_HOST/azimap-runtime/dist/azimap-embed-loader.min.js"></script>
```

Initialize the map:

```javascript
const map = await Azimap.init({
    target: '#map',
    mapId: 123,
    apiBaseUrl: 'https://YOUR_AZIMAP_API/',
    assetBaseUrl: 'https://YOUR_AZIMAP_HOST/azimap-runtime/',
    editable: true,
    auth: Azimap.customerBackend('/azimap')
});
```

Replace the example URLs and map ID with your deployment settings.

The helper handles CSRF, read-session renewal and write-token acquisition. Setting `editable: true` requests editing controls; server-side permissions determine which operations are allowed.

## Backend setup

Choose an adapter and connect it to your application’s existing authentication and permission services.

Configure:

- **Issuer URL:** your Azimap GeoAppCore endpoint.
- **API key:** a persistent Azimap API key stored securely on your backend.
- **Authentication:** identify the logged-in application user.
- **Authorization:** check current map, layer and operation permissions on every token request.
- **CSRF protection:** validate requests against the authenticated application session.

Configure the API key’s whitelist for the permitted browser origins and your backend’s outbound IP address.

The adapters expose the same browser-facing contract:

| Endpoint | Purpose |
|---|---|
| `GET /azimap/csrf` | Obtain a session-bound CSRF token |
| `POST /azimap/maps/{mapId}/session` | Authorize viewing and obtain a read session |
| `POST /azimap/maps/{mapId}/write-token` | Authorize an edit and obtain a write token |

Both POST endpoints require `X-CSRF-Token`.

A write-token request looks like:

```json
{
  "sessionToken": "CURRENT_READ_SESSION",
  "operations": [
    {
      "layerId": 456,
      "operation": "Edit"
    }
  ]
}
```

Supported operation names are `Create`, `CreateGeometry`, `Edit` and `Delete`. Treat these browser-supplied values as requests for permission, never as proof of permission.

## Security model

- The permanent API key stays on your backend.
- Browser read sessions last at most five minutes.
- Write tokens permit one mutation request and last at most two minutes.
- Neither token can outlive its parent credential; a write token cannot outlive its read session.
- Your application authorizes the user, and Azimap independently enforces the API key’s scope and the actual requested operation.
- Tokens authorize layers and operation types. They do not provide row-level or exact-payload authorization.
- Signing out prevents further token issuance. Already-issued credentials remain usable until expiry or key revocation, subject to their restrictions.
- Failed or uncertain writes are not automatically replayed.

Do not log credentials or cache token responses.

## Azimap deployment requirements

Deploy compatible GeoAppCore, APICore and browser-library versions before enabling `EmbedSessions:Enabled`.

Write-token replay protection supports:

- **Shared Redis:** the default for cloud deployments, multiple API instances and separate mutation-serving roles.
- **Explicit single-instance mode:** for one combined API process and GeoAppCore on the same machine. An exclusive local lock prevents overlapping API owners, and an API restart invalidates earlier write tokens.

Replay protection is configured within Azimap. The customer’s frontend integration remains the same.

## Testing your integration

Before deployment, verify:

- Authentication and CSRF enforcement.
- Allowed and denied map/layer operations.
- Permission changes taking effect on subsequent token requests.
- Read-session renewal and expiry.
- Rejection of reused write tokens.
- Logout and parent-key revocation behavior.

Use a designated test map and layer when exercising mutations.

## Compatibility

The adapters implement a shared HTTP contract, so other backend languages can integrate using the same flow.

The Python adapter targets WSGI applications. ASGI applications require an appropriate framework bridge.

Check the release notes for compatible Azimap API and browser-library versions before upgrading.

# Azimap Cloud Embed Integrations

Embed native Azimap JavaScript maps in your application using your existing login and permissions. Your users do not need an Azimap account.

This repository provides backend adapters, integration guides and a sample application for Azimap Cloud.

## Available integrations

| Integration | Description |
|---|---|
| ASP.NET Core / MVC | .NET 10 adapter with MVC and Razor integration guidance |
| Node.js | Adapter using your existing authentication and CSRF services |
| Python | Dependency-free, synchronous WSGI adapter |
| Sample application | Demonstrates login, map loading, session renewal and editing authorization |

ASP.NET Core MVC uses the same .NET adapter. Classic ASP.NET MVC 5 / System.Web requires a separate integration.

## How it works

Your application keeps its permanent Azimap API key on its backend.

1. A user signs in to your application.
2. Your backend checks their permissions and requests a short-lived, read-only map session from Azimap Cloud.
3. The browser loads the map directly from Azimap.
4. Before each supported edit, the Azimap JavaScript library requests a write token through your backend.
5. Your backend checks the user’s current permission for the requested layer and operation.
6. Azimap issues a scoped, single-use write token. The browser submits the edit directly to Azimap, which validates the operation and consumes the token.

Your backend handles authorization requests. Map data flows directly between the browser and Azimap Cloud.

## Cloud endpoints

| Service | URL |
|---|---|
| Application and token issuer | `https://app.azimap.com/` |
| API | `https://api.azimap.com/` |
| Tiles and layer content | `https://tiles.azimap.com/` |
| Embed assets | `https://app.azimap.com/azimap-runtime/` |

## Getting started

### 1. Configure your Azimap API key

Create an Azimap API key with access to the maps, layers and operations your application needs.

Configure its whitelist for:

- Your application’s browser origin.
- Your backend’s outbound IP address.

Store the key securely on your backend. Never include it in frontend JavaScript, HTML, a viewmodel or a browser response.

### 2. Add a backend adapter

Choose the adapter for your backend and configure:

- **Issuer URL:** `https://app.azimap.com/`
- **API key:** your private Azimap API key.
- **Authentication:** your existing application login.
- **Authorization:** your current map, layer and operation permissions.
- **CSRF protection:** your application’s authenticated-session protection.

The adapter’s authorization callback must check permissions on every session and write-token request. Return only the map, layers and capabilities that the logged-in user is allowed to access.

The adapters expose these endpoints within your application:

| Endpoint | Purpose |
|---|---|
| `GET /azimap/csrf` | Obtain a session-bound CSRF token |
| `POST /azimap/maps/{mapId}/session` | Authorize viewing and obtain a read session |
| `POST /azimap/maps/{mapId}/write-token` | Authorize an edit and obtain a write token |

Both POST endpoints require `X-CSRF-Token`. The browser helper handles these requests automatically.

### 3. Add the map to your page

Provide a map container and load the Azimap embed loader:

```html
<div id="map" style="height: 600px"></div>

<script src="https://app.azimap.com/azimap-runtime/dist/azimap-embed-loader.min.js"></script>
```

Initialize the map:

```javascript
const map = await Azimap.init({
    target: '#map',
    mapId: 123,
    apiBaseUrl: 'https://api.azimap.com/',
    assetBaseUrl: 'https://app.azimap.com/azimap-runtime/',
    layerContentApiUrl: 'https://tiles.azimap.com/',
    editable: true,
    auth: Azimap.customerBackend('/azimap')
});
```

Replace `123` with your Azimap map ID.

`/azimap` refers to the adapter endpoints in **your application**. The helper uses the user’s existing application session.

Setting `editable: true` requests editing controls. Your backend’s permissions and the Azimap API key determine which operations are allowed. Use `editable: false` for a read-only map interface.

## Authorizing edits

You do not need to listen for editing events or classify map requests yourself. Before a supported mutation is submitted, the Azimap JavaScript library asks your backend for permission.

For example:

```http
POST /azimap/maps/123/write-token
Content-Type: application/json
X-CSRF-Token: ...
```

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

Your backend checks the logged-in user’s current permission for every requested layer and operation.

| Operation | Purpose |
|---|---|
| `Create` | Create a feature with attributes |
| `CreateGeometry` | Create a geometry-only feature |
| `Edit` | Modify an existing feature or its attachments |
| `Delete` | Remove a feature |

Browser-supplied operations are requests for permission, not proof of permission. Azimap independently checks the actual mutation against the issued token.

Supported edits made through the Azimap map library acquire write tokens automatically. If your application makes its own direct API mutation calls, it must perform the same authorization and token-acquisition step.

## Backend integrations

### ASP.NET Core / MVC

Register the .NET 10 adapter alongside your existing authentication and authorization middleware.

ASP.NET Core MVC applications use the same adapter alongside their controllers and Razor views.

Configure these server-side settings:

```text
Azimap:IssuerBaseUrl = https://app.azimap.com/
Azimap:ApiKey = YOUR_PRIVATE_SERVER_API_KEY
```

Connect the authorization callback to your application’s permission service. Keep the API key out of viewmodels and rendered pages.

### Node.js

Connect the Node adapter to your existing authentication, CSRF and permission services.

Set its issuer URL to `https://app.azimap.com/` and load the API key from your server’s secret store or environment configuration.

### Python

The Python adapter is a synchronous WSGI callable using only the standard library.

Connect its authentication, CSRF and authorization hooks to your existing application services. ASGI applications require an appropriate framework bridge.

## Session renewal

Read sessions renew automatically through your authenticated backend.

If renewal fails, map interactions are suspended while unsaved edits are retained. Changed permissions may require the map to be reinitialized after your application resolves unsaved work.

Your application should stop further issuance after logout and destroy the displayed map.

## Security

- The permanent API key remains on your backend.
- Browser read sessions last at most five minutes.
- Write tokens permit one mutation request and last at most two minutes.
- Tokens cannot outlive their parent credential. A write token cannot outlive its read session.
- Your application checks user permissions; Azimap also enforces the API key’s scope and the actual requested operation.
- Permissions cover layers and operation types. They do not provide row-level or exact-payload authorization.
- Signing out prevents further token issuance. Already-issued credentials remain usable until expiry or key revocation, subject to their restrictions.
- Token responses must not be cached, and credentials must not be logged.

Check current permissions on every acquisition. Permissions copied into a login session can become stale.

## Handling failed writes

Failed or uncertain writes are not automatically replayed.

A write token remains consumed even if subsequent processing fails. If a request times out or its outcome is unclear, inspect the result before deliberately submitting another edit.

## Integration checklist

Before making your integration available to users, verify:

- Unauthenticated users cannot obtain sessions or write tokens.
- CSRF protection is enforced.
- Users can access only their permitted maps and layers.
- Allowed edits succeed and denied operations fail.
- Permission changes affect subsequent token requests.
- Session renewal and logout behave correctly.
- API-key revocation prevents further authorized access.

Use a designated test map and layer when exercising mutations.

## Compatibility

All adapters implement the same HTTP contract and use the same browser helper:

```javascript
auth: Azimap.customerBackend('/azimap')
```

Other backend languages can implement this contract using the documented session and write-token endpoints. Review release notes before updating your adapter or integration.

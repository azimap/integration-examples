# Renewable dashboard sessions

A saved dashboard can use your application's login to obtain and renew a short-lived
session. Each session is read-only and scoped to one dashboard and its parent map.
Use Azimap 11.3.1 or later with embed session issuance enabled. The
[runnable sample](../EmbedSessions.SampleApp/README.md#try-a-dashboard) includes
this flow alongside the map sample.

## Authorize the dashboard on each request

Expose an authenticated, CSRF-protected `POST /azimap/dashboards/{dashboardId}/session`
in your application. Use the route ID to look up the dashboard, then check the
signed-in user's current access to that dashboard and its permitted layers. Get
the parent map ID, layer IDs and external user ID from trusted server data. Two
dashboards on the same map can have different audiences.

With the ASP.NET Core adapter, register a dashboard authorization callback in
`AddAzimapCustomerBackend` and call `MapAzimapCustomerBackend()` as usual.

```csharp
options.AuthorizeDashboardAsync = (context, dashboardId, cancellation) =>
    permissions.AuthorizeDashboardAsync(context.User, dashboardId, cancellation);
```

`permissions` is your application's permission service. Return a
`CustomerDashboardGrant(dashboardId, mapId, allowedLayerIds, externalUserId)`
or return `null` to deny access. The adapter handles authentication, CSRF and
the issuer exchange. The sample's single account uses a configured dashboard
and login claims. Connect your own app to its live permission service.

### Issuer contract for other backends

Use the same authentication and CSRF setup as the [map examples](../../README.md).
After checking dashboard access, the server calls `POST /api/embed/sessions` on
the Azimap application host with `Authorization: Bearer YOUR_SERVER_API_KEY` and
a JSON body built from the authorized grant.

```json
{
  "dashboardId": 789,
  "mapId": 123,
  "layerIds": [456],
  "readWrite": false,
  "canExport": false,
  "externalUserId": "customer-user-42"
}
```

Verify the issuer response contains the expected `dashboardId` and `mapId`,
a nonempty `accessToken` and `scopeKey`, a positive `expiresInSeconds`, and
`editable: false` and `canExport: false`. Return those fields with
`Cache-Control: no-store`. The permanent API key stays on your server.
The same endpoint issues a fresh grant on every renewal. Dashboards use this
read session for data requests and have no write-token route.

## Load the dashboard

Load the embed runtime from `/azimap-runtime/dist/azimap-embed-loader.min.js`
on the Azimap application host and give the target element a visible height.
For an app using the adapters' CSRF endpoint, the browser code looks like this.

```javascript
const dashboard = await Azimap.initDashboard({
    target: '#dashboard',
    dashboardId: 789,
    apiBaseUrl: 'https://api.azimap.com/',
    assetBaseUrl: 'https://app.azimap.com/azimap-runtime/',
    async sessionProvider({ dashboardId }) {
        const csrf = await fetch('/azimap/csrf', {
            credentials: 'same-origin', cache: 'no-store'
        });
        if (!csrf.ok) throw new Error('Sign in to open the dashboard.');
        const { csrfToken } = await csrf.json();
        const response = await fetch('/azimap/dashboards/' + dashboardId + '/session', {
            method: 'POST', credentials: 'same-origin', cache: 'no-store',
            headers: { 'X-CSRF-Token': csrfToken }
        });
        if (!response.ok) throw new Error('Could not authorize the dashboard (HTTP ' + response.status + ').');
        return response.json();
    }
});
```

The dashboard loader handles renewal automatically. Call
`dashboard.renewSession()` to renew explicitly and `dashboard.destroy()` on
sign-out or before reopening the dashboard. If renewal fails or the grant
changes, the loader stops further authenticated reads and shows a message.
Use the optional `onSessionError(error)` callback to start your app's recovery
flow. Saved dashboard startup parameters can filter widgets for presentation.
Enforce record-level access separately using your API or datastore permissions.

# Runnable Azimap Cloud sample

This .NET 10 web app uses its own cookie login to host an Azimap JavaScript map
and an optional dashboard. Users sign in to this application and do not need an
Azimap account.
ASP.NET Core is only this sample's implementation. The [integration guide](../../README.md)
and Node/Python adapters support other customer backends.

## Configure and run

Create or select an Azimap Cloud API key restricted to your map, layers and
required operations. Allow the sample browser origin (`http://localhost:5098`)
and your backend's observed egress IP in the key's whitelist.
Confirm that the Azimap deployment has session issuance enabled. The Cloud
loader can be available when the issuer still returns HTTP 404.

Copy `appsettings.example.json` to `appsettings.Local.json` beside the project.
Replace `ApiKey`, `MapId`, `LayerIds` and the sample password with your values.
The password must contain at least 12 characters. The Cloud service URLs are
already configured. The local settings file is ignored by Git; never commit it.
Environment variables override it, using double underscores such as
`Azimap__ApiKey` and `Azimap__LayerIds__0`.

From this directory:

```bash
dotnet run -f net10.0 --no-launch-profile --urls http://localhost:5098
```

Open `http://localhost:5098` and sign in with the account you configured.
The app shows missing settings when configuration is incomplete. **Setup and
integration help** is available without signing in.

The sample defaults to viewing only. To try editing, use a designated test layer,
set `Sample:AllowEditing` to `true`, and ensure your key and map permit the desired
operations. Export is independently opt-in. Restart the sample after changing
configuration, then sign out and sign back in to refresh its grants.

## Try a dashboard

Use Azimap 11.3.1 or later. Set `Azimap:DashboardId` to the ID of a saved
dashboard whose parent map is the configured `Azimap:MapId`. Include the
layers its widgets need in `Azimap:LayerIds` and grant your key read access to
those layers. Restart the app and sign in again. Select **Open dashboard** to
open it in a new tab, then use **Renew session** to exercise the provider.
The dashboard session stays read-only even when the map sample allows editing
or export. See the [dashboard integration guide](../EmbedSessions.AspNetCore/Dashboard.md).

## What the sample demonstrates

- A public bootstrap response exposes only explicit browser configuration and a
  CSRF token; the permanent API key and password remain on the server.
- The sample authenticates its configured account and issues an HTTP-only cookie.
- `wwwroot/sample.js` initializes the map with
  `auth: Azimap.customerBackend('/azimap')`.
- The adapter checks permission before obtaining a reusable read session and
  before obtaining each single-use write token from Azimap Cloud.
- Map data and mutations travel directly between the browser and Azimap.
- Renewal failures suspend interaction and retain unsaved edits. Signing out
  closes the map and prevents new issuance; existing tokens elsewhere can remain
  usable until expiry or key revocation.

This one-account login demonstrates the flow. For your real application, use
its existing identity provider and check live per-user layer and operation
permissions in the authorization callback. This sample's fixed login claims do
not track later permission changes. Layer grants do not implement row-level
access rules.

## Tests

```bash
dotnet test ../EmbedSessions.SampleApp.Tests/EmbedSessions.SampleApp.Tests.csproj -f net10.0
```

Tests use a fake issuer and synthetic credentials. They cover authentication,
CSRF, trusted grants, upstream failures, write-token acquisition, dashboard
session acquisition and renewal, logout and integration with MVC. They do not
modify Cloud map data or replace testing your
actual map and application policy. Sample HTML, CSS and JavaScript are served
directly from `wwwroot` and require no asset build.

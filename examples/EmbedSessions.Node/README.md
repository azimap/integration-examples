# Node customer backend reference

`adapter.mjs` implements the same HTTP contract as the ASP.NET Core adapter,
using Node 22+ with no package dependencies. Set `AZIMAP_ISSUER_URL` to
`https://app.azimap.com/`. Mount it under `/azimap` in the
containing application. Keep the API key on that server.

```javascript
import { createAzimapBackend } from './adapter.mjs';

const handleAzimap = createAzimapBackend({
    issuerBaseUrl: process.env.AZIMAP_ISSUER_URL,
    apiKey: process.env.AZIMAP_API_KEY,
    authenticate: request => applicationSessions.authenticate(request),
    csrf: {
        issue: (request, response, user) => applicationCsrf.issue(request, response, user),
        validate: (request, user) => applicationCsrf.validate(request, user)
    },
    authorize: (user, mapId, operations) => permissions.authorizeMap(user, mapId, operations)
});

// In your application's existing HTTP handler, before reading the request body:
if (await handleAzimap(request, response)) return;
```

`applicationSessions`, `applicationCsrf`, and `permissions` are your existing
application services, not functions supplied by Azimap. Authentication must
return the trusted user or null. CSRF validation must verify `X-CSRF-Token` against
the authenticated session; never accept mere presence of a header.

`authorize` must query current permissions on EVERY request. Empty operations
means a read-session request. Otherwise authorize every requested operation and
layer. Return null to deny, or a trusted object:

```javascript
{ mapId: 123, layerIds: [456], editable: true, canExport: false,
  externalUserId: user.id }
```

Use the same browser setup as the ASP.NET adapter:
`auth: Azimap.customerBackend('/azimap')`. The browser helper handles CSRF,
renewal and separate write-token acquisition. No Azimap login is involved.

The adapter expects an unconsumed Node request stream. If mounting after a
framework JSON-body parser, adapt its body-reading section accordingly. It limits
request bodies to 64 KiB, refuses issuer redirects, times out after 20 seconds,
returns explicit response DTOs, and suppresses upstream error bodies.

Run `node adapter.test.mjs` to execute the seven built-in Node tests.
See the [full integration guide](../../README.md)
for Cloud endpoints, scope, expiry and supported operations.

# Python customer backend reference

`azimap_backend.py` is a synchronous WSGI adapter for Python 3.10+ using only the
standard library. Set `AZIMAP_ISSUER_URL` to `https://app.azimap.com/`. It exposes the same endpoints as the .NET and Node adapters.
Use it with a WSGI host, or bridge it from routes in your existing Python web
framework. Keep the parent API key exclusively on the server.

## Connect your application's services

```python
import os
from azimap_backend import AzimapBackend

azimap = AzimapBackend(
    issuer_base_url=os.environ["AZIMAP_ISSUER_URL"],
    api_key=os.environ["AZIMAP_API_KEY"],
    authenticate=lambda environ: application_sessions.authenticate(environ),
    csrf_issue=lambda environ, user: application_csrf.issue(environ, user),
    csrf_validate=lambda environ, user: application_csrf.validate(environ, user),
    authorize=lambda user, map_id, operations: permissions.authorize_map(
        user, map_id, operations),
)

# In your WSGI dispatcher, with the existing login/session middleware available:
def application(environ, start_response):
    path = environ.get("SCRIPT_NAME", "") + environ.get("PATH_INFO", "")
    if path.startswith("/azimap/"):
        return azimap(environ, start_response)
    return existing_application(environ, start_response)
```

`application_sessions`, `application_csrf`, `permissions` and
`existing_application` above belong to your application; they are not supplied by
Azimap. Authentication and CSRF middleware must run before the adapter, or the
hooks must call your existing services directly. Do not bypass framework
middleware and then assume it authenticated the request. In a Flask/Django route
bridge, invoke this WSGI callable using your framework's response adapter while
the existing authenticated request context is active.

All four hooks are mandatory:

- `authenticate(environ)` returns the trusted logged-in user, or None to deny.
  Never accept a user ID supplied in an HTTP header as proof of identity.
- `csrf_issue(environ, user)` returns a token bound to that authenticated session.
  Use the existing application's CSRF service and cookie/session handling.
- `csrf_validate(environ, user)` returns True only after validating the submitted
  `environ["HTTP_X_CSRF_TOKEN"]` against that session. Header presence is insufficient.
- `authorize(user, map_id, operations)` checks current permissions on EVERY call.
  Empty operations means a read-session request. For a write, check every layer
  and operation. Return None to deny or a trusted grant dictionary:

```python
{"mapId": 123, "layerIds": [456], "editable": True,
 "canExport": False, "externalUserId": str(user.id)}
```

The adapter checks map/layer membership and the edit flag. Your permission hook
must additionally decide whether that user may perform each requested operation.
Azimap checks the parent key's grants independently. Neither browser fields nor
possession of a read-session token is a user permission grant.

## Browser and HTTP contract

Use `auth: Azimap.customerBackend('/azimap')` in `Azimap.init` exactly as with the
other adapters. No browser changes or Azimap user login are needed.

| Endpoint | Behavior |
| --- | --- |
| `GET /azimap/csrf` | Authenticated session-bound CSRF token |
| `POST /azimap/maps/{mapId}/session` | Recheck viewing permission and obtain a read session |
| `POST /azimap/maps/{mapId}/write-token` | Recheck write intent and obtain a single-use write token |

Both POSTs require `X-CSRF-Token`. The write body contains `sessionToken` and
`operations`, such as `[{"layerId":456,"operation":"Edit"}]`. Supported operation
names are `Create`, `CreateGeometry`, `Edit` and `Delete`.

Mount on the same origin as the application. If hosting below an application
path, configure `prefix` to the full path (for example `/customer/azimap`) and use
that same full path in the browser helper. WSGI `SCRIPT_NAME` mounts are supported.
Supply an unconsumed `wsgi.input` stream and correct `CONTENT_LENGTH`; when bridging
a framework which already read the body, supply a copy of the WSGI environment
with a fresh `io.BytesIO(raw_body)` stream and its byte length. The adapter never
changes the host environment. It is a synchronous WSGI callable, not an ASGI or
FastAPI endpoint; those applications need a framework bridge to this contract.

Requests are limited to 64 KiB and upstream calls time out after 20 seconds. Use HTTPS
for Azimap Cloud requests. The transport refuses
redirects, bounds upstream response size, strips extra response fields and never
returns upstream error bodies or credentials. Responses use `Cache-Control:
no-store`. The adapter does not automatically retry writes. Layer/operation scope
does not provide row-level or exact-payload authorization.

Configure the Azimap key's whitelist for your backend's actual egress IP and your
browser origin. See the [Cloud integration guide](../../README.md)
for expiry, renewal, key revocation and supported operations.

## Tests

From this directory, without installing dependencies:

```bash
python3 -m unittest -v
```

Eleven tests use synthetic credentials. Unit tests cover policy and transport
failures; a loopback HTTP test runs a real WSGI server and the actual urllib
transport against a fake issuer. No external services or map data are used.
Connect your real login/CSRF services and perform a deployment smoke test before
publishing an application.

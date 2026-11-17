"""Customer embed adapter for Python WSGI applications; no third-party dependencies."""
import json
import re
from collections.abc import Mapping
from http import HTTPStatus
from urllib.error import HTTPError
from urllib.parse import urlsplit, urlunsplit
from urllib.request import HTTPRedirectHandler, Request, build_opener

_OPERATIONS = frozenset(("Create", "CreateGeometry", "Edit", "Delete"))
_MAX_BYTES = 65536


def _text(value):
    return isinstance(value, str) and bool(value.strip())


def _id(value):
    return type(value) is int and 0 < value <= 2147483647


class _InvalidUpstream(Exception):
    pass


class _NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, request, response, code, message, headers, new_url):
        return None


def _post_json(url, headers, body):
    request = Request(url, data=json.dumps(body).encode("utf-8"), headers=headers, method="POST")
    # Refuse redirects: the private key must only be sent to the configured issuer.
    try:
        with build_opener(_NoRedirect()).open(request, timeout=20) as response:
            payload = response.read(_MAX_BYTES + 1)
            if len(payload) > _MAX_BYTES:
                raise _InvalidUpstream()
            return response.status, payload
    except HTTPError as error:
        with error:
            return error.code, b""  # Do not forward upstream error bodies or headers.


class AzimapBackend:
    """Mount under /azimap using the customer's existing session and CSRF services.

    authenticate(environ) -> trusted user or None
    csrf_issue(environ, user) -> session-bound request token (string)
    csrf_validate(environ, user) -> bool; validate HTTP_X_CSRF_TOKEN
    authorize(user, map_id, operations) -> trusted grant dictionary or None

    Hooks are synchronous. Operations are empty for read-session acquisition.
    post_json is a transport injection point for tests, not browser input.
    """

    def __init__(self, *, issuer_base_url, api_key, authenticate, csrf_issue,
                 csrf_validate, authorize, prefix="/azimap", post_json=_post_json):
        issuer = urlsplit(issuer_base_url)
        # Accessing port also validates malformed port numbers before any request.
        issuer.port
        if (not issuer.hostname or issuer.username is not None or issuer.password is not None
                or issuer.query or issuer.fragment or not _text(api_key)
                or any(c in api_key for c in "\r\n")
                or any(ord(c) < 33 for c in issuer_base_url)
                or not (issuer.scheme == "https" or issuer.scheme == "http"
                        and issuer.hostname in ("localhost", "127.0.0.1", "::1"))):
            raise ValueError("Configure a private API key and HTTPS issuer; loopback HTTP is allowed.")
        if not all(callable(hook) for hook in (authenticate, csrf_issue, csrf_validate, authorize, post_json)):
            raise ValueError("Authentication, CSRF and authorization hooks are required.")
        if not isinstance(prefix, str) or not re.fullmatch(r"/[A-Za-z0-9_/-]+", prefix):
            raise ValueError("Use an absolute application path for the adapter prefix.")
        self.prefix = prefix.rstrip("/")
        self.issuer = urlunsplit((issuer.scheme, issuer.netloc, issuer.path.rstrip("/") + "/", "", ""))
        self.api_key = api_key
        self.authenticate = authenticate
        self.csrf_issue = csrf_issue
        self.csrf_validate = csrf_validate
        self.authorize = authorize
        self.post_json = post_json

    def __call__(self, environ, start_response):
        try:
            status, body = self._handle(environ)
        except _InvalidUpstream:
            status, body = 502, None
        except Exception:
            # Customer hooks and transport errors may contain credentials. Never expose them.
            status, body = 503, None
        encoded = b"" if body is None else json.dumps(body).encode("utf-8")
        start_response("%d %s" % (status, HTTPStatus(status).phrase), [
            ("Content-Type", "application/json"), ("Cache-Control", "no-store"),
            ("Content-Length", str(len(encoded)))])
        return [encoded]

    def _handle(self, environ):
        # Supports both a direct route bridge and a WSGI mount which moves the
        # prefix into SCRIPT_NAME. Never alter the host application's environ.
        path = environ.get("SCRIPT_NAME", "") + environ.get("PATH_INFO", "")
        if not path.startswith(self.prefix + "/"):
            return 404, None
        user = self.authenticate(environ)
        if user is None or user is False:
            return 401, None
        if environ.get("REQUEST_METHOD") == "GET" and path == self.prefix + "/csrf":
            token = self.csrf_issue(environ, user)
            return (200, {"csrfToken": token}) if _text(token) else (503, None)
        match = re.fullmatch(re.escape(self.prefix) + r"/maps/([0-9]+)/(session|write-token)", path)
        if not match or environ.get("REQUEST_METHOD") != "POST":
            return 404, None
        if self.csrf_validate(environ, user) is not True:
            return 400, None
        map_id = int(match[1])
        if not _id(map_id):
            return 400, None
        writing = match[2] == "write-token"
        try:
            length = int(environ.get("CONTENT_LENGTH") or 0)
            if length < 0:
                return 400, None
            if length > _MAX_BYTES:
                return 413, None
            raw = environ["wsgi.input"].read(length) if length else b""
            if len(raw) != length:
                return 400, None
            data = json.loads(raw.decode("utf-8") or "{}")
        except (ValueError, UnicodeError):
            return 400, None
        operations = data.get("operations") if writing and isinstance(data, dict) else []
        if writing and (not isinstance(data, dict) or not _text(data.get("sessionToken"))
                        or not isinstance(operations, list) or not 1 <= len(operations) <= 128
                        or any(not isinstance(x, dict) or not _id(x.get("layerId"))
                               or not isinstance(x.get("operation"), str)
                               or x["operation"] not in _OPERATIONS for x in operations)):
            return 400, None
        operations = [{"layerId": x["layerId"], "operation": x["operation"]} for x in operations]
        # Treat browser operations only as requested actions; the callback authorizes all of them.
        grant = self.authorize(user, map_id, operations)
        if (not isinstance(grant, Mapping) or not _id(grant.get("mapId")) or grant["mapId"] != map_id
                or not isinstance(grant.get("layerIds"), (list, tuple)) or not grant["layerIds"]
                or any(not _id(x) for x in grant["layerIds"])
                or not _text(grant.get("externalUserId")) or len(grant["externalUserId"]) > 200
                or writing and (grant.get("editable") is not True
                                or any(x["layerId"] not in grant["layerIds"] for x in operations))):
            return 403, None
        body = {"mapId": map_id, "externalUserId": grant["externalUserId"]}
        if writing:
            body.update(sessionToken=data["sessionToken"], operations=operations)
        else:
            body.update(layerIds=list(dict.fromkeys(grant["layerIds"])),
                        readWrite=grant.get("editable") is True, canExport=grant.get("canExport") is True)
        status, raw = self.post_json(self.issuer + ("api/embed/write-tokens" if writing else "api/embed/sessions"),
                                    {"Authorization": "Bearer " + self.api_key, "Content-Type": "application/json"}, body)
        if not 200 <= status < 300:
            return (status if status in (400, 401, 403, 404, 409, 429, 500, 502, 503, 504) else 502), None
        try:
            result = json.loads(raw)
        except (ValueError, UnicodeError):
            return 502, None
        if (not isinstance(result, dict) or not _text(result.get("accessToken"))
                or type(result.get("expiresInSeconds")) is not int or result["expiresInSeconds"] <= 0
                or not writing and (type(result.get("mapId")) is not int or result["mapId"] != map_id
                                    or not _text(result.get("scopeKey")))):
            return 502, None
        response = {"accessToken": result["accessToken"], "expiresInSeconds": result["expiresInSeconds"]}
        if not writing:
            response.update(mapId=result["mapId"], scopeKey=result["scopeKey"],
                            editable=result.get("editable") is True, canExport=result.get("canExport") is True)
        return 200, response

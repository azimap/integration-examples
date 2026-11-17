import io
import json
import unittest
from unittest.mock import patch
from urllib.error import HTTPError
from azimap_backend import AzimapBackend, _NoRedirect, _post_json


class AdapterTests(unittest.TestCase):
    def setUp(self):
        self.calls = []
        self.allowed = True
        self.adapter = AzimapBackend(
            issuer_base_url="https://issuer.example/", api_key="server-secret",
            authenticate=lambda env: {"id": "trusted-user"} if env.get("HTTP_COOKIE") == "authenticated" else None,
            csrf_issue=lambda env, user: "csrf",
            csrf_validate=lambda env, user: env.get("HTTP_X_CSRF_TOKEN") == "csrf",
            authorize=self.authorize, post_json=self.send)

    def authorize(self, user, map_id, operations):
        if not self.allowed or map_id != 12 or any(x["layerId"] != 31 or x["operation"] != "Edit" for x in operations):
            return None
        return {"mapId": 12, "layerIds": [31], "editable": True, "canExport": False, "externalUserId": user["id"]}

    def send(self, url, headers, body):
        self.calls.append((url, headers, body))
        return 200, json.dumps({"accessToken": "child", "expiresInSeconds": 120, "mapId": 12,
                                "editable": True, "canExport": False, "scopeKey": "scope", "apiKey": "must-not-leak"}).encode()

    def invoke(self, path="/azimap/maps/12/session", data=None, **overrides):
        raw = json.dumps(data or {}).encode()
        env = {"REQUEST_METHOD": "POST", "PATH_INFO": path, "CONTENT_LENGTH": str(len(raw)),
               "wsgi.input": io.BytesIO(raw), "HTTP_COOKIE": "authenticated", "HTTP_X_CSRF_TOKEN": "csrf"}
        env.update(overrides)
        captured = []
        body = b"".join(self.adapter(env, lambda status, headers: captured.append((int(status[:3]), dict(headers)))))
        return captured[0][0], body, captured[0][1]

    @staticmethod
    def write():
        return {"sessionToken": "read-session", "operations": [{"layerId": 31, "operation": "Edit"}]}

    def test_authentication_and_csrf_before_upstream(self):
        self.assertEqual(401, self.invoke(HTTP_COOKIE="")[0])
        self.assertEqual(400, self.invoke(HTTP_X_CSRF_TOKEN="")[0])
        self.assertEqual([], self.calls)

    def test_csrf_and_wsgi_mount(self):
        status, body, headers = self.invoke("/csrf", REQUEST_METHOD="GET", SCRIPT_NAME="/azimap")
        self.assertEqual(200, status)
        self.assertEqual({"csrfToken": "csrf"}, json.loads(body))
        self.assertEqual("no-store", headers["Cache-Control"])

    def test_session_uses_only_trusted_grants(self):
        status, body, _ = self.invoke(data={"mapId": 999, "layerIds": [999], "externalUserId": "forged", "canExport": True})
        self.assertEqual(200, status)
        url, headers, sent = self.calls[0]
        self.assertEqual("https://issuer.example/api/embed/sessions", url)
        self.assertEqual("Bearer server-secret", headers["Authorization"])
        self.assertEqual({"mapId": 12, "layerIds": [31], "externalUserId": "trusted-user", "readWrite": True, "canExport": False}, sent)
        self.assertNotIn(b"server-secret", body)
        self.assertNotIn(b"must-not-leak", body)

    def test_each_write_rechecks_permission_and_returns_explicit_dto(self):
        status, body, _ = self.invoke("/azimap/maps/12/write-token", self.write())
        self.assertEqual(200, status)
        self.assertEqual({"accessToken": "child", "expiresInSeconds": 120}, json.loads(body))
        self.assertEqual("https://issuer.example/api/embed/write-tokens", self.calls[0][0])
        self.assertEqual("trusted-user", self.calls[0][2]["externalUserId"])
        self.allowed = False
        self.assertEqual(403, self.invoke("/azimap/maps/12/write-token", self.write())[0])
        self.assertEqual(1, len(self.calls))

    def test_invalid_or_out_of_scope_operations(self):
        for operations, expected in [([], 400), ([None], 400), ([{"layerId": True, "operation": "Edit"}], 400),
                                     ([{"layerId": 31, "operation": ["Edit"]}], 400),
                                     ([{"layerId": 31, "operation": "Admin"}], 400),
                                     ([{"layerId": 999, "operation": "Edit"}], 403)]:
            with self.subTest(operations=operations):
                self.assertEqual(expected, self.invoke("/azimap/maps/12/write-token", {"sessionToken": "read", "operations": operations})[0])
        self.assertEqual([], self.calls)

    def test_body_limits_and_malformed_json(self):
        self.assertEqual(413, self.invoke(CONTENT_LENGTH="65537")[0])
        self.assertEqual(400, self.invoke(CONTENT_LENGTH="-1")[0])
        self.assertEqual(400, self.invoke(CONTENT_LENGTH="1", **{"wsgi.input": io.BytesIO(b"{")})[0])
        self.assertEqual([], self.calls)

    def test_upstream_denial_and_transport_failures_never_leak(self):
        for code in (401, 403, 429, 503):
            self.adapter.post_json = lambda *args: (code, b"server-secret")
            status, body, headers = self.invoke()
            self.assertEqual(code, status)
            self.assertEqual(b"", body)
            self.assertEqual("no-store", headers["Cache-Control"])
        def fail(*args):
            raise OSError("server-secret")
        self.adapter.post_json = fail
        self.assertEqual((503, b""), self.invoke()[:2])

    def test_bad_success_payloads_return_bad_gateway(self):
        for result in (b"not-json", b"null", b"[]", b'{"accessToken":"token","expiresInSeconds":true}',
                       b'{"accessToken":"token","expiresInSeconds":120,"mapId":999}'):
            self.adapter.post_json = lambda *args: (200, result)
            self.assertEqual(502, self.invoke()[0])

    def test_missing_hooks_and_insecure_remote_issuer_are_rejected(self):
        for url in ("http://issuer.example/", "https://user:password@issuer.example/", "https://issuer.example/?key=secret"):
            with self.assertRaises(ValueError):
                AzimapBackend(issuer_base_url=url, api_key="key", authenticate=None, csrf_issue=None, csrf_validate=None, authorize=None)
        with self.assertRaises(ValueError):
            AzimapBackend(issuer_base_url="https://issuer.example/", api_key="key", authenticate=None, csrf_issue=None, csrf_validate=None, authorize=None)

    def test_transport_refuses_redirects_and_discards_error_body(self):
        self.assertIsNone(_NoRedirect().redirect_request(None, None, 302, "", {}, "https://other.example"))
        with patch("azimap_backend.build_opener") as factory:
            factory.return_value.open.side_effect = HTTPError("https://issuer.example", 302, "redirect", {}, io.BytesIO(b"secret"))
            self.assertEqual((302, b""), _post_json("https://issuer.example", {}, {}))
            self.assertIsInstance(factory.call_args.args[0], _NoRedirect)
            self.assertEqual(20, factory.return_value.open.call_args.kwargs["timeout"])
        self.adapter.post_json = lambda *args: (302, b"secret")
        self.assertEqual((502, b""), self.invoke()[:2])


if __name__ == "__main__":
    unittest.main()

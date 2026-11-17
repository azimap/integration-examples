"""Loopback HTTP smoke test: real WSGI host and standard-library issuer transport."""
import json
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.error import HTTPError
from urllib.request import Request, urlopen
from wsgiref.simple_server import WSGIRequestHandler, make_server
from azimap_backend import AzimapBackend


class HttpFlowTests(unittest.TestCase):
    def test_real_wsgi_and_issuer_transport(self):
        received = []

        class Issuer(BaseHTTPRequestHandler):
            def do_POST(self):
                body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
                received.append((self.path, self.headers["Authorization"], body))
                payload = json.dumps({"accessToken": "short-lived", "expiresInSeconds": 120,
                                      "mapId": 12, "editable": True, "canExport": False,
                                      "scopeKey": "scope", "parentKey": "do-not-forward"}).encode()
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(payload)))
                self.end_headers()
                self.wfile.write(payload)

            def log_message(self, *args):
                pass

        class Quiet(WSGIRequestHandler):
            def log_message(self, *args):
                pass

        issuer = ThreadingHTTPServer(("127.0.0.1", 0), Issuer)
        allowed = [True]
        adapter = AzimapBackend(
            issuer_base_url="http://127.0.0.1:%d/" % issuer.server_port, api_key="test-private-key",
            authenticate=lambda env: {"id": "alice"} if env.get("HTTP_COOKIE") == "test-session" else None,
            csrf_issue=lambda env, user: "test-csrf",
            csrf_validate=lambda env, user: env.get("HTTP_X_CSRF_TOKEN") == "test-csrf",
            authorize=lambda user, map_id, operations: {
                "mapId": 12, "layerIds": [31], "editable": True, "externalUserId": user["id"]
            } if allowed[0] else None)
        server = make_server("127.0.0.1", 0, adapter, handler_class=Quiet)
        threads = [threading.Thread(target=host.serve_forever, daemon=True) for host in (issuer, server)]
        for thread in threads:
            thread.start()
        base = "http://127.0.0.1:%d/azimap" % server.server_port
        try:
            def request(path, body=None, headers=None):
                return urlopen(Request(base + path, data=None if body is None else json.dumps(body).encode(),
                                       headers=headers or {}), timeout=5)
            with self.assertRaises(HTTPError) as denied:
                request("/csrf")
            self.assertEqual(401, denied.exception.code)
            denied.exception.close()
            headers = {"Cookie": "test-session", "Content-Type": "application/json"}
            with request("/csrf", headers=headers) as response:
                headers["X-CSRF-Token"] = json.load(response)["csrfToken"]
            with request("/maps/12/session", {}, headers) as response:
                session = json.load(response)
                self.assertEqual("no-store", response.headers["Cache-Control"])
                self.assertNotIn("parentKey", session)
            body = {"sessionToken": session["accessToken"], "operations": [{"layerId": 31, "operation": "Edit"}]}
            with request("/maps/12/write-token", body, headers) as response:
                self.assertEqual({"accessToken": "short-lived", "expiresInSeconds": 120}, json.load(response))
            self.assertEqual(["/api/embed/sessions", "/api/embed/write-tokens"], [x[0] for x in received])
            self.assertTrue(all(x[1] == "Bearer test-private-key" for x in received))
            self.assertEqual("alice", received[1][2]["externalUserId"])
            allowed[0] = False
            with self.assertRaises(HTTPError) as denied:
                request("/maps/12/write-token", body, headers)
            self.assertEqual(403, denied.exception.code)
            denied.exception.close()
            self.assertEqual(2, len(received))
        finally:
            for host in (server, issuer):
                host.shutdown()
                host.server_close()
            for thread in threads:
                thread.join(timeout=5)


if __name__ == "__main__":
    unittest.main()

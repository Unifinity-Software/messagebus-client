"""Disposable-container-only authenticated dumb Git fixture; never logs authorization."""
import base64
import functools
import http.server
import ssl
import sys


class Handler(http.server.SimpleHTTPRequestHandler):
    def do_GET(self):
        expected = "Basic " + base64.b64encode(b"fixture:fixture-value").decode()
        if self.headers.get("Authorization") != expected:
            self.send_response(401)
            self.send_header("WWW-Authenticate", 'Basic realm="fixture"')
            self.end_headers()
            return
        super().do_GET()

    def log_message(self, *_):
        pass


server = http.server.ThreadingHTTPServer(
    ("127.0.0.1", 443), functools.partial(Handler, directory=sys.argv[1]))
context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
context.load_cert_chain(sys.argv[2], sys.argv[3])
server.socket = context.wrap_socket(server.socket, server_side=True)
server.serve_forever()

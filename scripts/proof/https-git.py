"""Disposable-container-only authenticated dumb Git fixture; never logs authorization."""
import base64
import functools
import http.server
import ssl
import socketserver
import struct
import threading
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


class Dns(socketserver.BaseRequestHandler):
    def handle(self):
        query, sock = self.request
        if len(query) < 17 or len(query) > 512:
            return
        end = 12
        while end < len(query) and query[end]:
            end += query[end] + 1
        if end + 5 > len(query):
            return
        question = query[12:end + 5]
        is_fixture = query[12:end + 1] == b'\x07fixture\x04test\x00'
        answer = is_fixture and query[end + 1:end + 3] == b'\x00\x01'
        header = query[:2] + struct.pack('!HHHHH', 0x8180, 1, int(answer), 0, 0)
        record = b'\xc0\x0c\x00\x01\x00\x01\x00\x00\x00\x01\x00\x04\x7f\x00\x00\x01' if answer else b''
        sock.sendto(header + question + record, self.client_address)


dns = socketserver.ThreadingUDPServer(('127.0.0.1', 53), Dns)
threading.Thread(target=dns.serve_forever, daemon=True).start()
server = http.server.ThreadingHTTPServer(
    ("127.0.0.1", 443), functools.partial(Handler, directory=sys.argv[1]))
context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
context.load_cert_chain(sys.argv[2], sys.argv[3])
server.socket = context.wrap_socket(server.socket, server_side=True)
server.serve_forever()

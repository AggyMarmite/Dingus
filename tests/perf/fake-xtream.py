#!/usr/bin/env python3
"""A stand-in Xtream server for the speed test: answers player_api.php and xmltv.php from the files make-big.py
wrote, and serves a tiny picture for every logo and poster.

    fake-xtream.py <folder> [port]
"""
import http.server, os, socketserver, struct, sys, time, urllib.parse, json, zlib

DIR = os.path.abspath(sys.argv[1])
PORT = int(sys.argv[2]) if len(sys.argv) > 2 else 8765


def make_png(w=200, h=300):
    """A plain grey picture, poster-sized (posters and logos all look the same – it's the loading that's timed)."""
    raw = b''.join(b'\x00' + bytes([40, 48, 64]) * w for _ in range(h))
    def chunk(t, d):
        return struct.pack('>I', len(d)) + t + d + struct.pack('>I', zlib.crc32(t + d) & 0xffffffff)
    return (b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 2, 0, 0, 0))
            + chunk(b'IDAT', zlib.compress(raw, 9)) + chunk(b'IEND', b''))


PNG = make_png()
FILES = {
    'get_live_categories': 'live_categories.json', 'get_live_streams': 'live_streams.json',
    'get_vod_categories': 'vod_categories.json', 'get_vod_streams': 'vod_streams.json',
    'get_series_categories': 'series_categories.json', 'get_series': 'series.json',
}


class Handler(http.server.BaseHTTPRequestHandler):
    protocol_version = 'HTTP/1.1'

    def log_message(self, *a):
        pass

    def send_bytes(self, body, ctype):
        self.send_response(200)
        self.send_header('Content-Type', ctype)
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def send_file(self, name, ctype):
        path = os.path.join(DIR, name)
        self.send_response(200)
        self.send_header('Content-Type', ctype)
        self.send_header('Content-Length', str(os.path.getsize(path)))
        self.end_headers()
        with open(path, 'rb') as f:
            while True:
                chunk = f.read(1 << 20)
                if not chunk:
                    break
                self.wfile.write(chunk)

    def do_GET(self):
        u = urllib.parse.urlparse(self.path)
        q = urllib.parse.parse_qs(u.query)
        try:
            if u.path.startswith('/img/'):
                return self.send_bytes(PNG, 'image/png')
            if u.path == '/xmltv.php':
                return self.send_file('xmltv.xml', 'application/xml')
            if u.path == '/player_api.php':
                action = (q.get('action') or [''])[0]
                if not action:
                    exp = int(time.time()) + 200 * 86400
                    return self.send_bytes(json.dumps({
                        'user_info': {'username': 'perf', 'auth': 1, 'status': 'Active', 'exp_date': str(exp), 'is_trial': '0',
                                      'active_cons': '0', 'max_connections': '2'},
                        'server_info': {'url': '127.0.0.1', 'port': str(PORT), 'timezone': 'Europe/London'},
                    }).encode(), 'application/json')
                if action in FILES:
                    return self.send_file(FILES[action], 'application/json')
                if action in ('get_vod_info', 'get_series_info', 'get_short_epg'):
                    return self.send_bytes(b'{}', 'application/json')
            self.send_response(404)
            self.send_header('Content-Length', '0')
            self.end_headers()
        except (BrokenPipeError, ConnectionResetError):
            pass


class Server(socketserver.ThreadingMixIn, http.server.HTTPServer):
    daemon_threads = True
    allow_reuse_address = True


print(f'Stand-in Xtream server on http://127.0.0.1:{PORT} serving {DIR}', flush=True)
Server(('127.0.0.1', PORT), Handler).serve_forever()

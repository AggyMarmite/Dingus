"""Stand-in provider for the Android self-test: an M3U playlist, an XMLTV guide and a few test streams.
The emulator reaches the runner at 10.0.2.2."""
import http.server, os, sys, time, datetime

ROOT = sys.argv[1]
HOST = "http://10.0.2.2:8000"

def guide():
    now = int(time.time()) // 1800 * 1800
    def t(s): return datetime.datetime.utcfromtimestamp(s).strftime("%Y%m%d%H%M%S +0000")
    out = ['<?xml version="1.0" encoding="UTF-8"?><tv>']
    for n in range(1, 4):
        out.append(f'<channel id="test{n}.uk"><display-name>Test {n}</display-name></channel>')
    for n in range(1, 4):
        for k in range(-1, 10):
            s = now + k * 1800
            out.append(f'<programme start="{t(s)}" stop="{t(s+1800)}" channel="test{n}.uk">'
                       f'<title>Programme {k+2} on Test {n}</title><desc>A made-up programme for the test.</desc></programme>')
    out.append('</tv>')
    return "\n".join(out).encode()

def playlist():
    return f"""#EXTM3U url-tvg="{HOST}/guide.xml"
#EXTINF:-1 tvg-id="test1.uk" tvg-chno="101" group-title="UK: Entertainment",Test 1 HD
{HOST}/live/1.ts
#EXTINF:-1 tvg-id="test2.uk" tvg-chno="102" group-title="UK: Entertainment",Test 2
{HOST}/hls/index.m3u8
#EXTINF:-1 tvg-id="test3.uk" tvg-chno="201" group-title="Sport",Test 3 Sport
{HOST}/live/1.ts
""".encode()

class H(http.server.SimpleHTTPRequestHandler):
    def __init__(self, *a, **k): super().__init__(*a, directory=ROOT, **k)
    def do_GET(self):
        if self.path.startswith("/playlist.m3u"): return self.send(playlist(), "audio/x-mpegurl")
        if self.path.startswith("/guide.xml"): return self.send(guide(), "application/xml")
        if self.path.startswith("/release.json"):
            body = ('{"tag_name":"v99.0.0","body":"- Test update\\n- Another line","assets":[{"name":"Dingus.apk",'
                    f'"browser_download_url":"{HOST}/Dingus.apk"' + '}]}').encode()
            return self.send(body, "application/json")
        if self.path.startswith("/live/"): self.path = "/test.ts"
        return super().do_GET()
    def send(self, body, ctype):
        self.send_response(200); self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body))); self.end_headers(); self.wfile.write(body)
    def log_message(self, *a): pass

http.server.ThreadingHTTPServer(("0.0.0.0", 8000), H).serve_forever()

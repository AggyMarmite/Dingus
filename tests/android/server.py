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
        for k in range(-6, 10):
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

import json, urllib.parse

def xtream(q):
    """A stand-in Xtream Codes panel: 3 live channels, 3 films, 2 shows."""
    a = q.get("action", [""])[0]
    if not a:
        return {"user_info": {"auth": 1, "username": "test", "status": "Active", "exp_date": "1893456000", "max_connections": "2", "active_cons": "0"}, "server_info": {"timezone": "Europe/London"}}
    if a == "get_live_categories": return [{"category_id": "1", "category_name": "UK: Entertainment"}, {"category_id": "2", "category_name": "Sport"}]
    if a == "get_live_streams":
        return [{"num": 101, "name": "Test 1 HD", "stream_id": 1, "category_id": "1", "epg_channel_id": "test1.uk", "stream_icon": "", "tv_archive": 1, "tv_archive_duration": "3"},
                {"num": 102, "name": "Test 2", "stream_id": 2, "category_id": "1", "epg_channel_id": "test2.uk", "stream_icon": ""},
                {"num": 201, "name": "Test 3 Sport", "stream_id": 3, "category_id": "2", "epg_channel_id": "test3.uk", "stream_icon": ""}]
    if a == "get_vod_categories": return [{"category_id": "10", "category_name": "Action"}, {"category_id": "11", "category_name": "Comedy"}]
    if a == "get_vod_streams":
        return [{"stream_id": 501, "name": "The Test Pattern (2021)", "category_id": "10", "rating": "7.4", "container_extension": "mp4", "added": "1700000000", "stream_icon": f"{HOST}/poster1.png"},
                {"stream_id": 502, "name": "Colour Bars Returns", "category_id": "10", "rating": "6.1", "year": "2023", "container_extension": "mp4", "added": "1700000500", "stream_icon": f"{HOST}/poster2.png"},
                {"stream_id": 503, "name": "A Very Long Sine Wave", "category_id": "11", "rating": 8, "container_extension": "mp4", "added": 1700001000, "stream_icon": None}]
    if a == "get_series_categories": return [{"category_id": "20", "category_name": "Drama"}]
    if a == "get_series":
        return [{"series_id": 701, "name": "Signal Lost", "category_id": "20", "cover": f"{HOST}/poster2.png", "rating": "8.2", "releaseDate": "2022-01-01", "last_modified": "1700002000"},
                {"series_id": 702, "name": "The Calibration", "category_id": "20", "cover": f"{HOST}/poster1.png", "rating": "7", "last_modified": "1700001500"}]
    if a == "get_vod_info":
        return {"info": {"name": "The Test Pattern", "plot": "A colourful journey through every bar of the spectrum, with a steady 440 Hz soundtrack.",
                         "cast": "Red, Green, Blue", "director": "Sine Wave", "genre": "Action", "releasedate": "2021-05-01",
                         "duration": "00:02:00", "rating": "7.4", "backdrop_path": [f"{HOST}/poster1.png"]}, "movie_data": {}}
    if a == "get_series_info":
        eps = {str(s): [{"id": str(800 + s * 10 + e), "episode_num": e, "title": f"Signal Lost - S0{s}E0{e} - Part {e}", "container_extension": "mp4",
                         "info": {"plot": f"Episode {e} of season {s}.", "duration": "00:02:00"}} for e in (1, 2)] for s in (1, 2)}
        return {"info": {"name": "Signal Lost", "plot": "A test card goes missing.", "genre": "Drama", "releaseDate": "2022-01-01", "rating": "8.2"},
                "seasons": [], "episodes": eps}
    return []

class H(http.server.SimpleHTTPRequestHandler):
    def __init__(self, *a, **k): super().__init__(*a, directory=ROOT, **k)
    def do_GET(self):
        if self.path.startswith("/playlist.m3u"): return self.send(playlist(), "audio/x-mpegurl")
        if self.path.startswith("/guide.xml"): return self.send(guide(), "application/xml")
        u = urllib.parse.urlparse(self.path)
        if u.path == "/player_api.php": return self.send(json.dumps(xtream(urllib.parse.parse_qs(u.query))).encode(), "application/json")
        if u.path == "/xmltv.php": return self.send(guide(), "application/xml")
        if u.path.startswith("/live/") and u.path.endswith(".ts"): return self.live()
        if u.path.startswith("/timeshift/"): self.path = "/test.ts"; return super().do_GET()
        if u.path.startswith("/movie/") or u.path.startswith("/series/"): self.path = "/test.mp4"; return super().do_GET()
        if self.path.startswith("/release.json"):
            body = ('{"tag_name":"v99.0.0","body":"- Test update\\n- Another line","assets":[{"name":"Dingus.apk",'
                    f'"browser_download_url":"{HOST}/Dingus.apk"' + '}]}').encode()
            return self.send(body, "application/json")
        return super().do_GET()
    def live(self):
        """Plays the test video like a real live channel: at its own speed, round and round, until the app hangs up."""
        path = os.path.join(ROOT, "test.ts")
        size = os.path.getsize(path)
        rate = size / 120.0  # the test video is 2 minutes long
        self.send_response(200); self.send_header("Content-Type", "video/mp2t"); self.end_headers()
        start = time.time(); sent = 0
        try:
            while True:
                with open(path, "rb") as f:
                    while True:
                        chunk = f.read(188 * 350)
                        if not chunk: break
                        self.wfile.write(chunk); sent += len(chunk)
                        ahead = sent / rate - (time.time() - start)
                        if ahead > 1.5: time.sleep(ahead - 1.0)
        except (BrokenPipeError, ConnectionResetError):
            pass

    def send(self, body, ctype):
        self.send_response(200); self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body))); self.end_headers(); self.wfile.write(body)
    def log_message(self, *a): pass

http.server.ThreadingHTTPServer(("0.0.0.0", 8000), H).serve_forever()

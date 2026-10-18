#!/usr/bin/env python3
"""Makes a big made-up Xtream playlist for the speed test: channels, films, shows (JSON, as player_api.php returns
them) and an XMLTV guide, in the folder given. Sizes are like a large real provider's.

    make-big.py <folder> [channels] [films] [shows] [guide channels] [guide hours]
"""
import json, os, random, sys, datetime

out = os.path.abspath(sys.argv[1])
N_CH = int(sys.argv[2]) if len(sys.argv) > 2 else 30000
N_FILM = int(sys.argv[3]) if len(sys.argv) > 3 else 80000
N_SHOW = int(sys.argv[4]) if len(sys.argv) > 4 else 15000
N_EPG = int(sys.argv[5]) if len(sys.argv) > 5 else 6000
HOURS = int(sys.argv[6]) if len(sys.argv) > 6 else 48
os.makedirs(out, exist_ok=True)
rnd = random.Random(42)
IMG = 'http://127.0.0.1:8765/img/'

countries = ['UK', 'US', 'CA', 'IE', 'AU', 'DE', 'FR', 'ES', 'IT', 'NL', 'PT', 'PL', 'SE', 'NO', 'DK', 'IN', 'PK', 'AR', 'TR', 'GR']
kinds = ['Entertainment', 'News', 'Sport', 'Films', 'Kids', 'Documentary', 'Music', 'Lifestyle', 'Local', 'Religious']
words = ['Prime', 'One', 'Max', 'Plus', 'Gold', 'Star', 'Nova', 'Vision', 'Central', 'Classic', 'Action', 'Family', 'Arena',
         'World', 'Live', 'City', 'Coast', 'Metro', 'Super', 'Life', 'Comedy', 'Drama', 'Cinema', 'Premier', 'Select']
qual = ['HD', 'FHD', 'SD', '4K', 'HEVC', '']

def dump(name, data):
    with open(os.path.join(out, name), 'w', encoding='utf-8') as f:
        json.dump(data, f, ensure_ascii=False, separators=(',', ':'))

# --- live
live_cats = []
for c in countries:
    for k in kinds:
        live_cats.append({'category_id': str(len(live_cats) + 1), 'category_name': f'{c}| {k}', 'parent_id': 0})
dump('live_categories.json', live_cats)

base_names = []
channels = []
epg_ids = []
for i in range(N_CH):
    cat = live_cats[rnd.randrange(len(live_cats))]
    country = cat['category_name'].split('|')[0]
    # Several versions of many channels (HD / FHD / backup), as real providers have.
    if base_names and rnd.random() < 0.45:
        base = rnd.choice(base_names[-400:])
    else:
        base = f'{rnd.choice(words)} {rnd.choice(words)} {len(base_names) + 1}'
        base_names.append(base)
    q = rnd.choice(qual)
    epg = f'{base.lower().replace(" ", ".")}.{country.lower()}' if len(epg_ids) < N_EPG or rnd.random() < 0.2 else ''
    if epg and epg not in epg_ids[-5:]: epg_ids.append(epg)
    channels.append({
        'num': i + 1, 'name': f'{country}| {base} {q}'.strip(), 'stream_type': 'live', 'stream_id': 100000 + i,
        'stream_icon': f'{IMG}ch{i % 5000}.png', 'epg_channel_id': epg, 'added': str(1700000000 + i * 37),
        'category_id': cat['category_id'], 'custom_sid': '', 'tv_archive': 1 if rnd.random() < 0.15 else 0,
        'direct_source': '', 'tv_archive_duration': 3 if rnd.random() < 0.15 else 0,
    })
dump('live_streams.json', channels)
epg_ids = list(dict.fromkeys(epg_ids))[:N_EPG]

# --- films
genres = ['Action', 'Comedy', 'Drama', 'Horror', 'Thriller', 'Romance', 'Sci-Fi', 'Animation', 'Family', 'Documentary', 'Crime', 'War']
vod_cats = [{'category_id': str(1000 + i), 'category_name': f'{c} | {g}', 'parent_id': 0}
            for i, (c, g) in enumerate((c, g) for c in ['EN', '4K', 'DE', 'FR', 'ES', 'IT', 'NL', 'TR', 'IN', 'AR'] for g in genres)]
dump('vod_categories.json', vod_cats)
adj = ['Last', 'Dark', 'Silent', 'Broken', 'Hidden', 'Golden', 'Lost', 'Final', 'Long', 'Little', 'Wild', 'Secret', 'Red', 'Cold']
noun = ['Journey', 'Summer', 'Harbour', 'Night', 'River', 'Kingdom', 'Promise', 'Mountain', 'Letter', 'Road', 'Garden', 'Storm']
films = []
for i in range(N_FILM):
    year = rnd.randint(1960, 2026)
    films.append({
        'num': i + 1, 'name': f'The {rnd.choice(adj)} {rnd.choice(noun)} {i + 1} ({year})', 'stream_type': 'movie',
        'stream_id': 500000 + i, 'stream_icon': f'{IMG}m{i % 8000}.jpg', 'rating': f'{rnd.uniform(3, 9):.1f}',
        'rating_5based': 3, 'added': str(1600000000 + i * 311), 'category_id': rnd.choice(vod_cats)['category_id'],
        'container_extension': rnd.choice(['mp4', 'mkv']), 'custom_sid': '', 'direct_source': '',
    })
dump('vod_streams.json', films)

# --- shows
ser_cats = [{'category_id': str(3000 + i), 'category_name': f'{c} | {g}', 'parent_id': 0}
            for i, (c, g) in enumerate((c, g) for c in ['EN', 'DE', 'FR', 'ES', 'TR', 'IN', 'AR', 'KIDS'] for g in genres)]
dump('series_categories.json', ser_cats)
shows = []
for i in range(N_SHOW):
    shows.append({
        'num': i + 1, 'name': f'{rnd.choice(adj)} {rnd.choice(noun)} {i + 1}', 'series_id': 800000 + i,
        'cover': f'{IMG}s{i % 4000}.jpg', 'plot': 'A made-up show for the Dingus speed test. ' * 3,
        'cast': 'Alex Example, Sam Sample', 'director': 'Jo Director', 'genre': rnd.choice(genres),
        'releaseDate': f'{rnd.randint(1990, 2026)}-01-01', 'last_modified': str(1650000000 + i * 97),
        'rating': f'{rnd.uniform(3, 9):.1f}', 'rating_5based': 3, 'backdrop_path': [], 'youtube_trailer': '',
        'episode_run_time': '45', 'category_id': rnd.choice(ser_cats)['category_id'],
    })
dump('series.json', shows)

# --- guide (XMLTV), times around now
now = datetime.datetime.now().replace(minute=0, second=0, microsecond=0) - datetime.timedelta(hours=6)
tz = datetime.datetime.now().astimezone().strftime('%z')
titles = ['News Tonight', 'The Village', 'Quiz Masters', 'Live: Rovers v United', 'Film: The Long Journey', 'Evening Live',
          'Little Explorers', 'Planet Wild', 'Kitchen Heroes', 'Home Rescue', 'Late Show', 'The Briefing', 'Football Review']
cats = ['News', 'Entertainment', 'Sport', 'Film', 'Children', 'Documentary']
n_prog = 0
with open(os.path.join(out, 'xmltv.xml'), 'w', encoding='utf-8') as f:
    f.write('<?xml version="1.0" encoding="UTF-8"?>\n<tv generator-info-name="dingus-speed-test">\n')
    for e in epg_ids:
        f.write(f'<channel id="{e}"><display-name>{e}</display-name><icon src="{IMG}g.png"/></channel>\n')
    for e in epg_ids:
        t = now
        end = now + datetime.timedelta(hours=HOURS)
        while t < end:
            ln = rnd.choice([15, 30, 30, 45, 60, 60, 90, 120])
            t2 = t + datetime.timedelta(minutes=ln)
            title = rnd.choice(titles)
            f.write(f'<programme start="{t:%Y%m%d%H%M%S} {tz}" stop="{t2:%Y%m%d%H%M%S} {tz}" channel="{e}">'
                    f'<title lang="en">{title}</title><desc lang="en">A made-up programme description for {title}, '
                    f'long enough to be like a real guide entry with a sentence or two of detail.</desc>'
                    f'<category lang="en">{rnd.choice(cats)}</category></programme>\n')
            t = t2
            n_prog += 1
    f.write('</tv>\n')

sizes = {n: os.path.getsize(os.path.join(out, n)) // (1024 * 1024) for n in os.listdir(out)}
print(f'{N_CH} channels, {N_FILM} films, {N_SHOW} shows, guide: {len(epg_ids)} channels / {n_prog} programmes · MB: {sizes}')

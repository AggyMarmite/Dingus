#!/usr/bin/env python3
"""Builds a sample playlist + TV guide (times around "now") and Dingus settings for the Linux self-test.
Same content as make-sample.ps1 (Windows)."""
import json, os, sys, datetime, urllib.parse

d = os.path.abspath(sys.argv[1])
os.makedirs(d, exist_ok=True)
channels = [
    ('one.uk', 'UK| Channel One HD', 'UK Entertainment'), ('one.uk', 'UK| Channel One FHD', 'UK Entertainment'),
    ('two.uk', 'UK| Channel Two HD', 'UK Entertainment'), ('three.uk', 'UK| Channel Three HD', 'UK Entertainment'),
    ('four.uk', 'UK| Channel Four HD', 'UK Entertainment'), ('news24', 'UK| News 24', 'UK News'),
    ('world.news', 'UK| World News HD', 'UK News'), ('sports.main', 'UK| Sports Main Event 4K', 'UK Sport'),
    ('sports.football', 'UK| Football Plus HD', 'UK Sport'), ('sports2', 'UK| Sports Two HD', 'UK Sport'),
    ('films1', 'UK| Film Classics HD', 'UK Films'), ('cinema.prem', 'UK| Cinema Premiere HD', 'UK Films'),
    ('kids1', 'UK| Kids Zone', 'UK Kids'), ('adult1', 'XXX Channel 1', 'Adult XXX'),
]
m3u = ['#EXTM3U']
for i, (cid, name, group) in enumerate(channels, 1):
    m3u.append(f'#EXTINF:-1 tvg-id="{cid}" tvg-name="{name}" group-title="{group}",{name}')
    m3u.append(f'http://127.0.0.1:9/live/{i}.ts')
for f in ['The Long Journey (2021)', 'Night Shift (2019)', 'Blue Harbour (2023)', 'The Last Summer (2018)']:
    m3u.append(f'#EXTINF:-1 group-title="Films | Drama",{f}')
    m3u.append('http://127.0.0.1:9/movie/u/p/' + urllib.parse.quote(f) + '.mp4')
for e in range(1, 5):
    m3u.append(f'#EXTINF:-1 group-title="Shows | Comedy",Office Life S01E0{e}')
    m3u.append(f'http://127.0.0.1:9/series/u/p/1{e}.mkv')
open(os.path.join(d, 'sample.m3u'), 'w', encoding='utf-8').write('\n'.join(m3u) + '\n')

titles = {
    'UK Entertainment': ['The Village', 'Evening Live', 'Quiz Masters', 'Sofa Critics', 'Dance Off', 'Football Highlights', 'Back Street', 'Panel of Fools'],
    'UK News': ['News Tonight', 'News at Six', 'The Briefing', 'World News Today'],
    'UK Sport': ['Live: Rovers v United', 'Football Review', 'Live: Test Cricket – Day 2', 'Super Sunday', 'Live: Grand Prix Qualifying'],
    'UK Films': ['The Long Journey', 'Night Shift', 'Blue Harbour', 'The Last Summer'],
    'UK Kids': ['Puppy Club', 'Little Explorers', 'Counting Friends'],
    'Adult XXX': ['Late Show'],
}
genre = {'UK Entertainment': 'Entertainment', 'UK News': 'News', 'UK Sport': 'Sport', 'UK Films': 'Film', 'UK Kids': 'Children', 'Adult XXX': 'Adult'}
now = datetime.datetime.now().astimezone()
tz = now.strftime('%z')
start = now.replace(minute=0, second=0, microsecond=0) - datetime.timedelta(hours=3)
x = ['<?xml version="1.0" encoding="UTF-8"?><tv>']
seen = set()
for cid, name, group in channels:
    if cid in seen: continue
    seen.add(cid)
    x.append(f'<channel id="{cid}"><display-name>{name}</display-name></channel>')
seen = set()
for cid, name, group in channels:
    if cid in seen: continue
    seen.add(cid)
    t, n = start, 0
    lst = titles[group]
    while t < start + datetime.timedelta(hours=30):
        ln = [30, 60, 90, 60, 45][n % 5]
        title = lst[n % len(lst)]
        e = t + datetime.timedelta(minutes=ln)
        x.append(f'<programme start="{t:%Y%m%d%H%M%S} {tz}" stop="{e:%Y%m%d%H%M%S} {tz}" channel="{cid}"><title>{title}</title>'
                 f'<desc>A sample description for {title}, used by the Dingus self-test.</desc><category>{genre[group]}</category></programme>')
        t, n = e, n + 1
x.append('</tv>')
open(os.path.join(d, 'sample.xml'), 'w', encoding='utf-8').write('\n'.join(x) + '\n')

settings = {
    'Playlists': [{'Id': 'selftest', 'Name': 'Sample', 'Type': 1, 'M3uUrl': os.path.join(d, 'sample.m3u'), 'EpgUrl': os.path.join(d, 'sample.xml'),
                   'Favourites': ['http://127.0.0.1:9/live/8.ts', 'http://127.0.0.1:9/live/1.ts']}],
    'ActivePlaylistId': 'selftest',
    'LastChannelId': 'http://127.0.0.1:9/live/1.ts',
    'AutoUpdate': False,
    'Timeshift': False,
}
data = os.environ.get('XDG_DATA_HOME') or os.path.join(os.path.expanduser('~'), '.local', 'share')
app = os.path.join(data, 'Dingus')
os.makedirs(app, exist_ok=True)
json.dump(settings, open(os.path.join(app, 'settings.json'), 'w'), indent=2)
print('sample in', d, '· settings in', app)

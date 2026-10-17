# Builds a sample playlist + TV guide (times around "now") and Dingus settings for the self-test.
param([string]$Dir)
New-Item -ItemType Directory -Force $Dir | Out-Null
$channels = @(
  @{id='bbc1.uk'; name='UK| BBC One HD'; group='UK Entertainment'}, @{id='bbc1.uk'; name='UK| BBC One FHD'; group='UK Entertainment'},
  @{id='bbc2.uk'; name='UK| BBC Two HD'; group='UK Entertainment'}, @{id='itv1.uk'; name='UK| ITV 1 HD'; group='UK Entertainment'},
  @{id='ch4.uk'; name='UK| Channel 4 HD'; group='UK Entertainment'}, @{id='sky.news'; name='UK| Sky News'; group='UK News'},
  @{id='bbc.news'; name='UK| BBC News HD'; group='UK News'}, @{id='sky.sports.main'; name='UK| Sky Sports Main Event 4K'; group='UK Sport'},
  @{id='sky.sports.pl'; name='UK| Sky Sports Premier League HD'; group='UK Sport'}, @{id='tnt1'; name='UK| TNT Sports 1 HD'; group='UK Sport'},
  @{id='film4'; name='UK| Film4 HD'; group='UK Films'}, @{id='sky.cinema'; name='UK| Sky Cinema Premiere HD'; group='UK Films'},
  @{id='cbeebies'; name='UK| CBeebies'; group='UK Kids'}, @{id='adult1'; name='XXX Channel 1'; group='Adult XXX'}
)
$m3u = New-Object System.Text.StringBuilder
[void]$m3u.AppendLine('#EXTM3U')
$i = 0
foreach ($c in $channels) {
  $i++
  [void]$m3u.AppendLine("#EXTINF:-1 tvg-id=`"$($c.id)`" tvg-name=`"$($c.name)`" group-title=`"$($c.group)`",$($c.name)")
  [void]$m3u.AppendLine("http://127.0.0.1:9/live/$i.ts")
}
foreach ($f in 'The Long Journey (2021)','Night Shift (2019)','Blue Harbour (2023)','The Last Summer (2018)') {
  [void]$m3u.AppendLine("#EXTINF:-1 group-title=`"Films | Drama`",$f")
  [void]$m3u.AppendLine("http://127.0.0.1:9/movie/u/p/$([uri]::EscapeDataString($f)).mp4")
}
foreach ($e in 1..4) {
  [void]$m3u.AppendLine("#EXTINF:-1 group-title=`"Shows | Comedy`",Office Life S01E0$e")
  [void]$m3u.AppendLine("http://127.0.0.1:9/series/u/p/1$e.mkv")
}
Set-Content -Path "$Dir\sample.m3u" -Value $m3u.ToString() -Encoding UTF8

$titles = @{
  'UK Entertainment' = @('EastEnders','The One Show','Pointless','Gogglebox','Strictly Come Dancing','Match of the Day','Coronation Street','Have I Got News for You')
  'UK News' = @('Sky News Tonight','BBC News at Six','The Briefing','World News Today')
  'UK Sport' = @('Live: Arsenal v Chelsea','Premier League Review','Live: England v India – Test Cricket','Super Sunday','Live F1: Qualifying')
  'UK Films' = @('The Long Journey','Night Shift','Blue Harbour','The Last Summer')
  'UK Kids' = @('Hey Duggee','Bluey','Numberblocks')
  'Adult XXX' = @('Late Show')
}
$genre = @{ 'UK Entertainment'='Entertainment'; 'UK News'='News'; 'UK Sport'='Sport'; 'UK Films'='Film'; 'UK Kids'='Children'; 'Adult XXX'='Adult' }
$x = New-Object System.Text.StringBuilder
[void]$x.AppendLine('<?xml version="1.0" encoding="UTF-8"?><tv>')
$seen = @{}
foreach ($c in $channels) { if (-not $seen[$c.id]) { $seen[$c.id]=1; [void]$x.AppendLine("<channel id=`"$($c.id)`"><display-name>$($c.name)</display-name></channel>") } }
$start = (Get-Date).Date.AddHours((Get-Date).Hour - 3)
$seen = @{}
foreach ($c in $channels) {
  if ($seen[$c.id]) { continue }; $seen[$c.id]=1
  $t = $start; $n = 0
  $list = $titles[$c.group]
  while ($t -lt $start.AddHours(30)) {
    $len = @(30,60,90,60,45)[$n % 5]
    $title = $list[$n % $list.Count]
    $s = $t.ToString('yyyyMMddHHmmss') + ' ' + (Get-Date).ToString('zzz').Replace(':','')
    $e = $t.AddMinutes($len).ToString('yyyyMMddHHmmss') + ' ' + (Get-Date).ToString('zzz').Replace(':','')
    [void]$x.AppendLine("<programme start=`"$s`" stop=`"$e`" channel=`"$($c.id)`"><title>$title</title><desc>A sample description for $title, used by the Dingus self-test.</desc><category>$($genre[$c.group])</category></programme>")
    $t = $t.AddMinutes($len); $n++
  }
}
[void]$x.AppendLine('</tv>')
Set-Content -Path "$Dir\sample.xml" -Value $x.ToString() -Encoding UTF8

$settings = @{
  Playlists = @(@{ Id='selftest'; Name='Sample'; Type=1; M3uUrl="$Dir\sample.m3u"; EpgUrl="$Dir\sample.xml"; Favourites=@('http://127.0.0.1:9/live/8.ts','http://127.0.0.1:9/live/1.ts') })
  ActivePlaylistId = 'selftest'
  LastChannelId = 'http://127.0.0.1:9/live/1.ts'
  LastChannelId = 'http://127.0.0.1:9/live/1.ts'
  AutoUpdate = $false
  Timeshift = $false
}
$appData = "$env:LOCALAPPDATA\Dingus"
New-Item -ItemType Directory -Force $appData | Out-Null
$settings | ConvertTo-Json -Depth 6 | Set-Content -Path "$appData\settings.json" -Encoding UTF8

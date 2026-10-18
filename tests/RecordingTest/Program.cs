using Dingus.Services;

// RecordingTest <file.ts>…: repairs a copy of each file (as Dingus does for old recordings), and also feeds it
// through in small, uneven pieces like a live recording does, with a reconnect at every joined-up segment.
// Prints the number of jumps found; the build checks the lengths with ffprobe.
foreach (var path in args)
{
    var fixedCopy = Path.ChangeExtension(path, ".fixed.ts");
    File.Copy(path, fixedCopy, true);
    int jumps = await TsTimeline.RepairFileAsync(fixedCopy);
    Console.WriteLine($"{Path.GetFileName(path)}: repaired, {jumps} jump(s)");

    // Live: random chunk sizes through Process (keeping partial packets), as RecordingService does.
    var live = Path.ChangeExtension(path, ".live.ts");
    var data = await File.ReadAllBytesAsync(path);
    var tl = new TsTimeline();
    var rnd = new Random(1);
    using var outFile = File.Create(live);
    var buf = new byte[256 * 1024];
    int have = 0, pos = 0;
    int s0 = TsTimeline.FindSync(data);
    if (s0 > 0) { outFile.Write(data, 0, s0); pos = s0; }
    tl.NewConnection();
    while (pos < data.Length)
    {
        int n = Math.Min(rnd.Next(1, 40000), Math.Min(buf.Length - have, data.Length - pos));
        Array.Copy(data, pos, buf, have, n);
        pos += n; have += n;
        int done = tl.Process(buf.AsSpan(0, have));
        outFile.Write(buf, 0, done);
        Buffer.BlockCopy(buf, done, buf, 0, have - done);
        have -= done;
    }
    outFile.Write(buf, 0, have);
    Console.WriteLine($"{Path.GetFileName(path)}: live, {tl.Jumps} jump(s)");
}

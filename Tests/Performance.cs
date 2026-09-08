using System.Diagnostics;
using System.Text;
using TgrepGui.Core;

internal static class Performance
{
    public static async Task RunAsync()
    {
        foreach (int size in new[] { 1024 * 1024, 8 * 1024 * 1024 })
        {
            var data = Encoding.UTF8.GetBytes(new string('x', size) + "\r\nlast");
            var samples = new List<double>();
            for (int iteration = 0; iteration < 6; iteration++)
            {
                using var stream = new ChunkStream(data, 4096);
                int lines = 0;
                var clock = Stopwatch.StartNew();
                await ProcessRunner.PumpUtf8LinesAsync(stream, line =>
                {
                    int expected = lines++ == 0 ? size : 4;
                    if (line.Length != expected) throw new Exception("Corrupt streamed line");
                    return ValueTask.CompletedTask;
                }, default);
                if (lines != 2) throw new Exception("Lost streamed line");
                if (iteration > 0) samples.Add(clock.Elapsed.TotalMilliseconds);
            }
            samples.Sort();
            Console.WriteLine($"pump {size / 1024} KiB, 4 KiB reads: median {samples[2]:F2} ms");
        }
        byte[] json = Encoding.UTF8.GetBytes("""{"type":"match","data":{"path":{"text":"sample.cs"},"lines":{"text":"needle\n"},"line_number":1,"submatches":[{"start":0,"end":6}]}}""");
        for (int i = 0; i < 10000; i++) TgrepJsonParser.TryReadHit(json, Environment.CurrentDirectory, out _);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < 100000; i++)
            if (!TgrepJsonParser.TryReadHit(json, Environment.CurrentDirectory, out var hit) || hit.MatchCount != 1)
                throw new Exception("Incorrect hit count");
        Console.WriteLine($"100,000 hit parses: {watch.Elapsed.TotalMilliseconds:F2} ms; allocated {GC.GetAllocatedBytesForCurrentThread() - before:N0} bytes");
    }

    private sealed class ChunkStream(byte[] bytes, int chunkSize) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
            => base.ReadAsync(buffer[..Math.Min(buffer.Length, chunkSize)], token);
    }
}

using System.Text;
using TgrepGui.Core;

internal static class PerformanceTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        foreach (int chunk in new[] { 1, 2, 7, 4096, 65536 })
        {
            byte[] input = Encoding.UTF8.GetBytes("\r\nè😀\r\n\n" + new string('x', 140000) + "\nfinal\r");
            using var stream = new FragmentedStream(input, chunk);
            var output = new List<string>();
            await ProcessRunner.PumpUtf8LinesAsync(stream, async bytes =>
            {
                await Task.Yield(); // The borrowed buffer must remain valid until the consumer completes.
                output.Add(Encoding.UTF8.GetString(bytes.Span));
            }, default);
            check(output.SequenceEqual(new[] { "è😀", new string('x', 140000), "final" }),
                $"UTF-8 pump preserves split CRLF, Unicode, long lines and EOF (chunk {chunk})");
        }
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try
            {
                await ProcessRunner.PumpUtf8LinesAsync(new MemoryStream([1]), _ => ValueTask.CompletedTask, cancelled.Token);
                throw new Exception("Cancellation ignored");
            }
            catch (OperationCanceledException) { check(true, "UTF-8 pump respects cancellation"); }
        }

        var hits = new List<FileHit>();
        var batcher = new FileHitBatcher(hit => { hits.Add(hit); return ValueTask.CompletedTask; });
        await batcher.AddAsync(new("a", "a", 2));
        check(hits.Count == 1, "first hit is published immediately");
        for (int i = 1; i < 100000; i++) await batcher.AddAsync(new("a", "a", 2));
        await batcher.AddAsync(new("b", "b", 3));
        await batcher.AddAsync(new("a", "a", 5));
        await batcher.FlushAsync();
        check(hits.Where(h => h.FullPath == "a").Sum(h => h.MatchCount) == 200005
            && hits.Where(h => h.FullPath == "b").Sum(h => h.MatchCount) == 3,
            "batched hits preserve counts across file changes and final partial batch");
        check(hits.Count < 2000, $"100,002 hits require only {hits.Count} callbacks");
        int before = hits.Count;
        await batcher.FlushAsync();
        check(hits.Count == before, "empty flush never duplicates results");

        var evicted = new List<object>();
        var cache = new PreviewCache<object>(2, 100, evicted.Add);
        object a = new(), b = new(), c = new();
        cache.Keep(a, 30); cache.Keep(b, 30);
        cache.Take(a); cache.Keep(a, 30); cache.Keep(c, 30);
        check(evicted.SequenceEqual(new[] { b }) && cache.Count == 2 && cache.RetainedBytes == 60,
            "preview cache evicts least recently used entry");
        cache.Take(a);
        check(cache.Count == 1 && cache.RetainedBytes == 30 && !evicted.Contains(a),
            "selected preview leaves cache without being cleared");
        cache.Keep(a, 90);
        check(evicted.Contains(c) && cache.RetainedBytes == 90, "preview cache enforces byte budget");
        cache.Keep(b, 101);
        check(cache.Count == 0 && cache.RetainedBytes == 0, "oversized inactive preview is not retained");
        cache.Keep(a, 20); cache.Clear();
        check(cache.Count == 0 && cache.RetainedBytes == 0, "new search releases cached previews");
    }

    private sealed class FragmentedStream(byte[] data, int chunk) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
            => base.ReadAsync(buffer[..Math.Min(buffer.Length, chunk)], token);
    }
}

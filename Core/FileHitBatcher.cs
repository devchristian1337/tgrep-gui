using System.Diagnostics;

namespace TgrepGui.Core;

// Coalesce adjacent hits, keeping the first result immediate and the buffer bounded.
internal sealed class FileHitBatcher(Func<FileHit, ValueTask> publish)
{
    private FileHit pending;
    private int rows;
    private bool published;
    private long lastFlush = Stopwatch.GetTimestamp();

    public async ValueTask AddAsync(FileHit hit)
    {
        if (rows > 0 && !StringComparer.OrdinalIgnoreCase.Equals(pending.FullPath, hit.FullPath))
            await FlushAsync().ConfigureAwait(false);
        pending = rows == 0 ? hit : pending with { MatchCount = checked(pending.MatchCount + hit.MatchCount) };
        rows++;
        if (!published || rows >= 128 || Stopwatch.GetElapsedTime(lastFlush).TotalMilliseconds >= 16)
            await FlushAsync().ConfigureAwait(false);
    }

    public async ValueTask FlushAsync()
    {
        if (rows == 0) return;
        var hit = pending;
        rows = 0;
        pending = default;
        published = true;
        await publish(hit).ConfigureAwait(false);
        lastFlush = Stopwatch.GetTimestamp();
    }
}

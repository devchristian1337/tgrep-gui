using System.Buffers;
using System.Text;
using System.Text.Json;

namespace TgrepGui.Core;

public static class TgrepJsonParser
{
    [ThreadStatic] private static string? lastFolder;
    [ThreadStatic] private static string? lastRawPath;
    [ThreadStatic] private static string? lastFullPath;
    [ThreadStatic] private static string? lastRelativePath;

    public static JsonEvent Parse(string json, string folder)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var type = root.GetProperty("type").GetString() ?? "";
        if (type is not ("begin" or "match" or "end")) return new(type, null, null);
        var data = root.GetProperty("data");
        string rawPath = Decode(data.GetProperty("path"));
        string fullPath = ResolveFullPath(rawPath, folder);
        if (type != "match") return new(type, fullPath, null);
        string original = Decode(data.GetProperty("lines"));
        string display = original.TrimEnd('\r', '\n');
        var spans = MapHighlights(original, display, data);
        long line = data.GetProperty("line_number").ValueKind == JsonValueKind.Null
            ? 1 : data.GetProperty("line_number").GetInt64();
        return new(type, fullPath, new(fullPath, ResolveRelativePath(folder, fullPath), line,
            display, spans.Spans, Math.Max(spans.Matches, 1)));
    }

    private static string ResolveFullPath(string rawPath, string folder)
    {
        if (lastFullPath is not null && lastRawPath is not null && lastFolder is not null
            && string.Equals(lastFolder, folder, StringComparison.Ordinal)
            && string.Equals(lastRawPath, rawPath, StringComparison.OrdinalIgnoreCase))
            return lastFullPath;
        lastFolder = folder;
        lastRawPath = rawPath;
        lastFullPath = Path.GetFullPath(rawPath, folder);
        lastRelativePath = null;
        return lastFullPath;
    }

    private static string ResolveRelativePath(string folder, string fullPath)
    {
        if (lastRelativePath is not null) return lastRelativePath;
        lastRelativePath = Path.GetRelativePath(folder, fullPath);
        return lastRelativePath;
    }

    private static (TextSpan[] Spans, int Matches) MapHighlights(string original, string display, JsonElement data)
    {
        if (!data.TryGetProperty("submatches", out var submatches) || submatches.GetArrayLength() == 0)
            return (Array.Empty<TextSpan>(), 0);
        int maxBytes = Encoding.UTF8.GetMaxByteCount(original.Length);
        byte[] rented = ArrayPool<byte>.Shared.Rent(maxBytes);
        try
        {
            int byteLength = Encoding.UTF8.GetBytes(original, rented);
            var utf8 = rented.AsSpan(0, byteLength);
            int count = submatches.GetArrayLength();
            var spans = new TextSpan[count];
            int written = 0, matches = 0, bytePos = 0, charPos = 0;
            foreach (var item in submatches.EnumerateArray())
            {
                matches++;
                int start = Math.Clamp(item.GetProperty("start").GetInt32(), 0, utf8.Length);
                int end = Math.Clamp(item.GetProperty("end").GetInt32(), start, utf8.Length);
                if (start < bytePos) { bytePos = 0; charPos = 0; }
                Advance(utf8, ref bytePos, ref charPos, start);
                int charStart = Math.Min(charPos, display.Length);
                Advance(utf8, ref bytePos, ref charPos, end);
                int charEnd = Math.Min(charPos, display.Length);
                if (charEnd > charStart) spans[written++] = new(charStart, charEnd - charStart);
            }
            if (written == 0) return (Array.Empty<TextSpan>(), matches);
            if (written < spans.Length) Array.Resize(ref spans, written);
            return (spans, matches);
        }
        finally { ArrayPool<byte>.Shared.Return(rented); }
    }

    // ripgrep spans use UTF-8 bytes; XAML TextRange uses UTF-16 code units.
    private static void Advance(ReadOnlySpan<byte> utf8, ref int bytePos, ref int charPos, int target)
    {
        if (target <= bytePos) return;
        charPos += Encoding.UTF8.GetCharCount(utf8.Slice(bytePos, target - bytePos));
        bytePos = target;
    }

    private static string Decode(JsonElement value)
    {
        if (value.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            return text.GetString()!;
        if (value.TryGetProperty("bytes", out var bytes))
            return Encoding.UTF8.GetString(Convert.FromBase64String(bytes.GetString()!));
        throw new JsonException("The JSON field contains neither text nor bytes.");
    }
}

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
        int max = Encoding.UTF8.GetMaxByteCount(json.Length);
        byte[] rented = ArrayPool<byte>.Shared.Rent(max);
        try { return Parse(rented.AsSpan(0, Encoding.UTF8.GetBytes(json, rented)), folder); }
        finally { ArrayPool<byte>.Shared.Return(rented); }
    }

    public static JsonEvent Parse(ReadOnlySpan<byte> utf8, string folder) => Read(utf8, folder, includeLine: true).Event;

    public static bool TryReadHit(ReadOnlySpan<byte> utf8, string folder, out FileHit hit)
    {
        var parsed = Read(utf8, folder, includeLine: false);
        if (parsed.Event.Type != "match" || parsed.Event.Path is null)
        {
            hit = default;
            return false;
        }
        hit = new(parsed.Event.Path, parsed.RelativePath ?? "", parsed.MatchCount);
        return true;
    }

    private readonly struct Parsed(JsonEvent evt, string? relativePath, int matchCount)
    {
        public JsonEvent Event { get; } = evt;
        public string? RelativePath { get; } = relativePath;
        public int MatchCount { get; } = matchCount;
    }

    private static Parsed Read(ReadOnlySpan<byte> utf8, string folder, bool includeLine)
    {
        var reader = new Utf8JsonReader(utf8);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected a JSON object.");
        string type = "";
        string? rawPath = null, lineText = null;
        long lineNumber = 1;
        var submatches = includeLine ? new List<(int Start, int End)>() : null;
        int matches = 0;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) break;
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            if (reader.ValueTextEquals("type"u8))
            {
                reader.Read();
                type = reader.GetString() ?? "";
            }
            else if (reader.ValueTextEquals("data"u8))
            {
                reader.Read();
                ReadData(ref reader, includeLine, ref rawPath, ref lineText, ref lineNumber, submatches, ref matches);
            }
            else reader.Skip();
        }
        if (type is not ("begin" or "match" or "end")) return new(new(type, null, null), null, 0);
        if (rawPath is null) throw new JsonException("The JSON event has no path.");
        string fullPath = ResolveFullPath(rawPath, folder);
        if (type != "match") return new(new(type, fullPath, null), null, 0);
        string relative = ResolveRelativePath(folder, fullPath);
        int matchCount = Math.Max(matches, 1);
        if (!includeLine) return new(new(type, fullPath, null), relative, matchCount);
        string original = lineText ?? "";
        string display = original.TrimEnd('\r', '\n');
        var spans = MapHighlights(original, display, submatches);
        return new(new(type, fullPath, new(fullPath, relative, lineNumber, display, spans, matchCount)), relative, matchCount);
    }

    private static void ReadData(ref Utf8JsonReader reader, bool includeLine, ref string? rawPath, ref string? lineText,
        ref long lineNumber, List<(int Start, int End)>? submatches, ref int matches)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Expected a data object.");
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) return;
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            if (reader.ValueTextEquals("path"u8))
            {
                reader.Read();
                rawPath = Decode(ref reader);
            }
            else if (reader.ValueTextEquals("lines"u8))
            {
                reader.Read();
                if (includeLine) lineText = Decode(ref reader);
                else reader.Skip();
            }
            else if (reader.ValueTextEquals("line_number"u8))
            {
                reader.Read();
                lineNumber = reader.TokenType == JsonTokenType.Null ? 1 : reader.GetInt64();
            }
            else if (reader.ValueTextEquals("submatches"u8))
            {
                reader.Read();
                ReadSubmatches(ref reader, includeLine, submatches, ref matches);
            }
            else reader.Skip();
        }
    }

    private static void ReadSubmatches(ref Utf8JsonReader reader, bool includeLine,
        List<(int Start, int End)>? submatches, ref int matches)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            reader.Skip();
            return;
        }
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject) continue;
            matches++;
            if (!includeLine) { reader.Skip(); continue; }
            int start = 0, end = 0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) continue;
                if (reader.ValueTextEquals("start"u8)) { reader.Read(); start = reader.GetInt32(); }
                else if (reader.ValueTextEquals("end"u8)) { reader.Read(); end = reader.GetInt32(); }
                else reader.Skip();
            }
            submatches!.Add((start, end));
        }
    }

    private static string Decode(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("The JSON field contains neither text nor bytes.");
        string? text = null, bytes = null;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) break;
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            if (reader.ValueTextEquals("text"u8)) { reader.Read(); text = reader.GetString(); }
            else if (reader.ValueTextEquals("bytes"u8)) { reader.Read(); bytes = reader.GetString(); }
            else reader.Skip();
        }
        if (text is not null) return text;
        if (bytes is not null) return Encoding.UTF8.GetString(Convert.FromBase64String(bytes));
        throw new JsonException("The JSON field contains neither text nor bytes.");
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

    private static TextSpan[] MapHighlights(string original, string display, List<(int Start, int End)>? submatches)
    {
        if (submatches is not { Count: > 0 }) return [];
        int maxBytes = Encoding.UTF8.GetMaxByteCount(original.Length);
        byte[] rented = ArrayPool<byte>.Shared.Rent(maxBytes);
        try
        {
            int byteLength = Encoding.UTF8.GetBytes(original, rented);
            var utf8 = rented.AsSpan(0, byteLength);
            var spans = new TextSpan[submatches.Count];
            int written = 0, bytePos = 0, charPos = 0;
            foreach (var item in submatches)
            {
                int start = Math.Clamp(item.Start, 0, utf8.Length);
                int end = Math.Clamp(item.End, start, utf8.Length);
                if (start < bytePos) { bytePos = 0; charPos = 0; }
                Advance(utf8, ref bytePos, ref charPos, start);
                int charStart = Math.Min(charPos, display.Length);
                Advance(utf8, ref bytePos, ref charPos, end);
                int charEnd = Math.Min(charPos, display.Length);
                if (charEnd > charStart) spans[written++] = new(charStart, charEnd - charStart);
            }
            if (written == 0) return [];
            if (written < spans.Length) Array.Resize(ref spans, written);
            return spans;
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
}

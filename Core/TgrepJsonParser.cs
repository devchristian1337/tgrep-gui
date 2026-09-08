using System.Text;
using System.Text.Json;

namespace TgrepGui.Core;

public static class TgrepJsonParser
{
    public static JsonEvent Parse(string json, string folder)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var type = root.GetProperty("type").GetString() ?? "";
        if (type is not ("begin" or "match" or "end")) return new(type, null, null);
        var data = root.GetProperty("data");
        string rawPath = Decode(data.GetProperty("path"));
        string fullPath = Path.GetFullPath(rawPath, folder);
        if (type != "match") return new(type, fullPath, null);
        string original = Decode(data.GetProperty("lines"));
        string display = original.TrimEnd('\r', '\n');
        byte[] bytes = Encoding.UTF8.GetBytes(original);
        var spans = new List<TextSpan>();
        int matches = 0;
        if (data.TryGetProperty("submatches", out var submatches))
        {
            foreach (var item in submatches.EnumerateArray())
            {
                matches++;
                int start = Math.Clamp(item.GetProperty("start").GetInt32(), 0, bytes.Length);
                int end = Math.Clamp(item.GetProperty("end").GetInt32(), start, bytes.Length);
                // ripgrep spans use UTF-8 bytes; XAML TextRange uses UTF-16 code units.
                int charStart = Math.Min(Encoding.UTF8.GetCharCount(bytes.AsSpan(0, start)), display.Length);
                int charEnd = Math.Min(Encoding.UTF8.GetCharCount(bytes.AsSpan(0, end)), display.Length);
                if (charEnd > charStart) spans.Add(new(charStart, charEnd - charStart));
            }
        }
        long line = data.GetProperty("line_number").ValueKind == JsonValueKind.Null
            ? 1 : data.GetProperty("line_number").GetInt64();
        return new(type, fullPath, new(fullPath, Path.GetRelativePath(folder, fullPath), line,
            display, spans, Math.Max(matches, 1)));
    }

    private static string Decode(JsonElement value)
    {
        if (value.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            return text.GetString()!;
        if (value.TryGetProperty("bytes", out var bytes))
            return Encoding.UTF8.GetString(Convert.FromBase64String(bytes.GetString()!));
        throw new JsonException("Il campo JSON non contiene text o bytes.");
    }
}

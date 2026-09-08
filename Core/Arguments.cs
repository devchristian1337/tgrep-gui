using System.Text;

namespace TgrepGui.Core;

public static class Arguments
{
    // Preserve brace alternatives and quoted globs containing spaces.
    public static IReadOnlyList<string> SplitGlobs(string input)
    {
        var result = new List<string>();
        var token = new StringBuilder();
        char quote = '\0';
        int braces = 0, brackets = 0;
        foreach (char ch in input)
        {
            if (quote != '\0')
            {
                if (ch == quote) quote = '\0'; else token.Append(ch);
                continue;
            }
            if (ch is '\'' or '"') { quote = ch; continue; }
            if (ch == '{') braces++;
            if (ch == '}') braces = Math.Max(0, braces - 1);
            if (ch == '[') brackets++;
            if (ch == ']') brackets = Math.Max(0, brackets - 1);
            if ((char.IsWhiteSpace(ch) || ch is ',' or ';') && braces == 0 && brackets == 0)
            {
                if (token.Length > 0) { result.Add(token.ToString()); token.Clear(); }
            }
            else token.Append(ch);
        }
        if (quote != '\0') throw new ArgumentException("Unclosed quotes in file filters.");
        if (token.Length > 0) result.Add(token.ToString());
        return result;
    }

    public static List<string> Command(string verb, string folder, string? indexPath)
    {
        var args = new List<string> { verb, folder };
        AddIndex(args, indexPath);
        return args;
    }

    public static List<string> Search(SearchOptions options, string? indexPath)
    {
        var args = new List<string> { "--json", "--line-buffered", "--color", "never", "-n" };
        if (options.IgnoreCase) args.Add("-i");
        if (options.Literal) args.Add("-F");
        if (options.WholeWord) args.Add("-w");
        if (!options.UseIndex) args.Add("--no-index");
        foreach (var glob in SplitGlobs(options.Include)) { args.Add("-g"); args.Add(glob); }
        foreach (var glob in SplitGlobs(options.Exclude))
        {
            var normalized = glob.TrimStart('!').Replace('\\', '/');
            if (normalized.EndsWith('/')) normalized += "**";
            args.Add("-g"); args.Add("!" + normalized);
        }
        AddIndex(args, indexPath);
        // End option parsing: a query such as '-test' or 'serve' remains a pattern.
        args.Add("--"); args.Add(options.Pattern); args.Add(options.Folder);
        return args;
    }

    private static void AddIndex(List<string> args, string? indexPath)
    {
        if (!string.IsNullOrEmpty(indexPath)) { args.Add("--index-path"); args.Add(indexPath); }
    }

    public static Dictionary<string, string> ParsePrefill(string[] args)
    {
        var result = new Dictionary<string, string>();
        for (int i = 0; i < args.Length; i++)
        {
            var pair = args[i].Split('=', 2);
            string key = pair[0] switch
            {
                "--folder" or "-f" => "folder", "--include-files" or "-i" => "include",
                "--exclude-files" or "-e" => "exclude", "--text" or "-t" => "text",
                _ => throw new ArgumentException($"Unknown option: {pair[0]}")
            };
            if (pair.Length == 2) result[key] = pair[1];
            else if (++i < args.Length) result[key] = args[i];
            else throw new ArgumentException($"Missing value for {pair[0]}.");
        }
        return result;
    }
}

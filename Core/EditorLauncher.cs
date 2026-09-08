using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TgrepGui.Core;

public static class EditorLauncher
{
    public static void Open(string file, long line, AppSettings settings)
    {
        if (!File.Exists(file)) throw new FileNotFoundException("The file no longer exists.", file);
        if (string.IsNullOrWhiteSpace(settings.EditorPath))
        {
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
            return;
        }
        var info = new ProcessStartInfo(Environment.ExpandEnvironmentVariables(settings.EditorPath.Trim().Trim('"')))
        { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(file)! };
        foreach (var argument in SplitWindowsArguments(settings.EditorArguments))
            info.ArgumentList.Add(argument.Replace("$FILE", file).Replace("$LINE", line.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Process.Start(info);
    }

    // Split BEFORE substituting, so quotes/spaces/metacharacters in filenames cannot add arguments.
    public static IReadOnlyList<string> SplitWindowsArguments(string template)
    {
        IntPtr argv = CommandLineToArgvW("editor.exe " + template, out int count);
        if (argv == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        try
        {
            return Enumerable.Range(1, count - 1)
                .Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!).ToArray();
        }
        finally { LocalFree(argv); }
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr pointer);
}

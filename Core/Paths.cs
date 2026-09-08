using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TgrepGui.Core;

public static class Paths
{
    public static string Normalize(string path)
    {
        string full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        if (OperatingSystem.IsWindows() && Directory.Exists(full))
        {
            using var handle = CreateFileW(full, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
            if (!handle.IsInvalid)
            {
                var buffer = new StringBuilder(32768);
                uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
                if (length > 0 && length < buffer.Capacity) full = buffer.ToString();
            }
        }
        if (full.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) full = @"\\" + full[8..];
        else if (full.StartsWith(@"\\?\")) full = full[4..];
        return Path.TrimEndingDirectorySeparator(full);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
}

using System.Runtime.InteropServices;

namespace Sonic.Core.Helpers;

/// <summary>Windows known folders that have no <see cref="Environment.SpecialFolder"/> value.</summary>
public static class KnownFolders
{
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <summary>The user's Downloads folder, honoring redirection (e.g. moved to another drive).</summary>
    public static string Downloads
    {
        get
        {
            try
            {
                if (SHGetKnownFolderPath(DownloadsId, 0, IntPtr.Zero, out var ptr) == 0)
                {
                    try
                    {
                        var path = Marshal.PtrToStringUni(ptr);
                        if (!string.IsNullOrWhiteSpace(path)) return path;
                    }
                    finally
                    {
                        Marshal.FreeCoTaskMem(ptr);
                    }
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                // Not on Windows (tests on other platforms): fall through.
            }

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);
}

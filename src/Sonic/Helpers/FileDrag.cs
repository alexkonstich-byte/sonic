using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Sonic.Helpers;

/// <summary>
/// Puts real files on a list drag so tracks can be dropped into Explorer, Telegram, Discord, a DAW, etc.
/// </summary>
public static partial class FileDrag
{
    private static readonly string StagingRoot = Path.Combine(Path.GetTempPath(), "Sonic-drag");

    /// <summary>
    /// Adds the files to the drag.
    /// Plain lists offer only "copy", so dropping on the same drive never moves a file out of the library.
    /// Reorderable lists must keep "move" for reordering; they hand out hard links (or copies) in a staging
    /// folder instead, so an app that moves the dropped file only moves the link.
    /// </summary>
    public static void Attach(DragItemsStartingEventArgs e, IEnumerable<string?> paths, bool keepMove = false)
    {
        var files = paths.Where(p => !string.IsNullOrEmpty(p) && File.Exists(p)).Select(p => p!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count == 0) return;

        e.Data.RequestedOperation = keepMove ? DataPackageOperation.Copy | DataPackageOperation.Move : DataPackageOperation.Copy;
        e.Data.Properties.Title = files.Count == 1 ? Path.GetFileName(files[0]) : $"{files.Count} files";
        e.Data.SetDataProvider(StandardDataFormats.StorageItems, async request =>
        {
            var deferral = request.GetDeferral();
            try
            {
                var sources = keepMove ? await Task.Run(() => Stage(files)) : files;
                var items = new List<IStorageItem>();
                foreach (var path in sources)
                {
                    try
                    {
                        items.Add(await StorageFile.GetFileFromPathAsync(path));
                    }
                    catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException or IOException)
                    {
                    }
                }

                request.SetData(items);
                Serilog.Log.Debug("Drag out: provided {Count} file(s)", items.Count);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Drag out failed");
            }
            finally
            {
                deferral.Complete();
            }
        });
    }

    private static List<string> Stage(IReadOnlyList<string> files)
    {
        CleanupOldStaging();
        var dir = Path.Combine(StagingRoot, DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        var staged = new List<string>();
        foreach (var file in files)
        {
            var target = Path.Combine(dir, Path.GetFileName(file));
            if (File.Exists(target)) continue;
            // A hard link is instant and takes no space; fall back to a copy across drives.
            if (!CreateHardLink(target, file, IntPtr.Zero)) File.Copy(file, target);
            staged.Add(target);
        }

        return staged;
    }

    private static void CleanupOldStaging()
    {
        try
        {
            if (!Directory.Exists(StagingRoot)) return;
            foreach (var dir in Directory.EnumerateDirectories(StagingRoot))
            {
                if (Directory.GetCreationTimeUtc(dir) >= DateTime.UtcNow.AddHours(-6)) continue;
                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
}

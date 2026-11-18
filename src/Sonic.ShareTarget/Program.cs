using System.Diagnostics;
using System.Text.RegularExpressions;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.DataTransfer;

namespace Sonic.ShareTarget;

/// <summary>Share menu entry point: forwards the shared link to Sonic.exe and exits.</summary>
internal static partial class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var command = "sonic://show";
        try
        {
            if (Windows.ApplicationModel.AppInstance.GetActivatedEventArgs() is ShareTargetActivatedEventArgs share)
            {
                var operation = share.ShareOperation;
                var link = Task.Run(() => ReadLinkAsync(operation.Data)).GetAwaiter().GetResult();
                operation.ReportCompleted();
                if (link is not null) command = "sonic://open?url=" + Uri.EscapeDataString(link);
            }
        }
        catch (Exception)
        {
            // Not started from the Share menu, or the data couldn't be read: just open Sonic.
        }

        var sonic = Path.Combine(AppContext.BaseDirectory, "Sonic.exe");
        if (!File.Exists(sonic)) return;
        var start = new ProcessStartInfo(sonic) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
        start.ArgumentList.Add(command);
        Process.Start(start)?.Dispose();
    }

    private static async Task<string?> ReadLinkAsync(DataPackageView data)
    {
        if (data.Contains(StandardDataFormats.WebLink)) return (await data.GetWebLinkAsync()).AbsoluteUri;
        if (data.Contains(StandardDataFormats.Text))
        {
            var match = UrlPattern().Match(await data.GetTextAsync());
            return match.Success ? match.Value : null;
        }

        return null;
    }

    [GeneratedRegex(@"https?://\S+")]
    private static partial Regex UrlPattern();
}

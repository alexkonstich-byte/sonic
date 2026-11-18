using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sonic.Core.Services;

/// <summary>What Discord shows under the user's name ("Listening to …").</summary>
public sealed record DiscordActivity(
    string Details,
    string? State,
    string? LargeImage,
    string? LargeText,
    DateTimeOffset? Start,
    DateTimeOffset? End);

/// <summary>
/// Minimal Discord Rich Presence client over the local IPC pipe (\\.\pipe\discord-ipc-N) used by the Discord
/// desktop app. Needs the Application ID of an app created in the Discord Developer Portal; its name is what
/// Discord displays. Nothing is sent anywhere except to the local Discord client.
/// </summary>
public sealed class DiscordRpcClient : IAsyncDisposable
{
    private const int OpHandshake = 0;
    private const int OpFrame = 1;
    private const int OpClose = 2;

    private readonly string _clientId;
    private readonly string _pipePrefix;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NamedPipeClientStream? _pipe;

    public DiscordRpcClient(string clientId, string pipePrefix = "discord-ipc-")
    {
        if (string.IsNullOrWhiteSpace(clientId) || !clientId.All(char.IsAsciiDigit))
            throw new ArgumentException("The Discord Application ID is a number from the Developer Portal.", nameof(clientId));
        _clientId = clientId.Trim();
        _pipePrefix = pipePrefix;
    }

    public bool IsConnected => _pipe?.IsConnected == true;

    /// <summary>Connects and performs the handshake. Returns false when Discord isn't running.</summary>
    public async Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsConnected) return true;
            await DisconnectCoreAsync().ConfigureAwait(false);
            for (var i = 0; i < 10; i++)
            {
                var pipe = new NamedPipeClientStream(".", _pipePrefix + i, PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    await pipe.ConnectAsync(150, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is TimeoutException or IOException)
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    continue;
                }

                _pipe = pipe;
                await WriteAsync(OpHandshake, new JsonObject { ["v"] = 1, ["client_id"] = _clientId }, cancellationToken).ConfigureAwait(false);
                var (op, body) = await ReadAsync(cancellationToken).ConfigureAwait(false);
                if (op == OpFrame && body?["evt"]?.GetValue<string>() == "READY") return true;

                // Wrong ID or another error: Discord answers with a close frame.
                var message = body?["message"]?.GetValue<string>() ?? "Discord refused the connection.";
                await DisconnectCoreAsync().ConfigureAwait(false);
                throw new InvalidOperationException(message);
            }

            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Sets (or with null clears) the activity. Throws <see cref="IOException"/> when Discord went away.</summary>
    public async Task SetActivityAsync(DiscordActivity? activity, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsConnected) throw new IOException("Not connected to Discord.");
            var args = new JsonObject { ["pid"] = Environment.ProcessId };
            if (activity is not null) args["activity"] = BuildActivity(activity);
            await WriteAsync(OpFrame, new JsonObject
            {
                ["cmd"] = "SET_ACTIVITY",
                ["args"] = args,
                ["nonce"] = Guid.NewGuid().ToString(),
            }, cancellationToken).ConfigureAwait(false);
            var (op, body) = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (op == OpClose) throw new IOException(body?["message"]?.GetValue<string>() ?? "Discord closed the connection.");
            if (body?["evt"]?.GetValue<string>() == "ERROR")
                throw new InvalidOperationException(body["data"]?["message"]?.GetValue<string>() ?? "Discord rejected the status.");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            await DisconnectCoreAsync().ConfigureAwait(false);
            throw new IOException("The connection to Discord was lost.", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    public static JsonObject BuildActivity(DiscordActivity a)
    {
        var activity = new JsonObject
        {
            ["type"] = 2, // Listening
            ["details"] = Clip(a.Details),
        };
        if (!string.IsNullOrWhiteSpace(a.State)) activity["state"] = Clip(a.State);
        if (a.Start is { } start)
        {
            var timestamps = new JsonObject { ["start"] = start.ToUnixTimeMilliseconds() };
            if (a.End is { } end) timestamps["end"] = end.ToUnixTimeMilliseconds();
            activity["timestamps"] = timestamps;
        }

        if (!string.IsNullOrWhiteSpace(a.LargeImage))
        {
            var assets = new JsonObject { ["large_image"] = a.LargeImage };
            if (!string.IsNullOrWhiteSpace(a.LargeText)) assets["large_text"] = Clip(a.LargeText);
            activity["assets"] = assets;
        }

        return activity;
    }

    /// <summary>Discord requires 2–128 characters.</summary>
    private static string Clip(string? s)
    {
        var text = (s ?? string.Empty).Trim();
        if (text.Length < 2) text = text.PadRight(2, ' ');
        return text.Length <= 128 ? text : text[..127] + "…";
    }

    private async Task WriteAsync(int op, JsonNode payload, CancellationToken ct)
    {
        var json = Encoding.UTF8.GetBytes(payload.ToJsonString());
        var frame = new byte[8 + json.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, op);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), json.Length);
        json.CopyTo(frame, 8);
        await _pipe!.WriteAsync(frame, ct).ConfigureAwait(false);
        await _pipe.FlushAsync(ct).ConfigureAwait(false);
    }

    private async Task<(int Op, JsonNode? Body)> ReadAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var header = new byte[8];
        await _pipe!.ReadExactlyAsync(header, timeout.Token).ConfigureAwait(false);
        var op = BinaryPrimitives.ReadInt32LittleEndian(header);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        if (length is < 0 or > 1 << 20) throw new IOException("Invalid frame from Discord.");
        var body = new byte[length];
        await _pipe.ReadExactlyAsync(body, timeout.Token).ConfigureAwait(false);
        try
        {
            return (op, JsonNode.Parse(body));
        }
        catch (JsonException)
        {
            return (op, null);
        }
    }

    private async Task DisconnectCoreAsync()
    {
        if (_pipe is null) return;
        try
        {
            await _pipe.DisposeAsync().ConfigureAwait(false);
        }
        catch (IOException)
        {
        }

        _pipe = null;
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisconnectCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        _gate.Dispose();
    }
}

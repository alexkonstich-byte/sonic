using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using Sonic.Core.Services;
using Xunit;

namespace Sonic.Core.Tests;

/// <summary>Talks to a fake Discord IPC server that speaks the same framing.</summary>
public class DiscordRpcTests
{
    private static async Task<(int, JsonNode?)> ReadFrame(Stream s)
    {
        var header = new byte[8];
        await s.ReadExactlyAsync(header);
        var body = new byte[BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4))];
        await s.ReadExactlyAsync(body);
        return (BinaryPrimitives.ReadInt32LittleEndian(header), JsonNode.Parse(body));
    }

    private static async Task WriteFrame(Stream s, int op, string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var header = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(header, op);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), body.Length);
        await s.WriteAsync(header);
        await s.WriteAsync(body);
        await s.FlushAsync();
    }

    [Fact]
    public async Task Handshake_and_set_activity()
    {
        var prefix = "sonic-test-ipc-" + Guid.NewGuid().ToString("N")[..8] + "-";
        await using var server = new NamedPipeServerStream(prefix + "0", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var received = new List<JsonNode?>();
        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            var (op, hello) = await ReadFrame(server);
            received.Add(hello);
            Assert.Equal(0, op);
            await WriteFrame(server, 1, """{"cmd":"DISPATCH","evt":"READY","data":{"v":1}}""");
            var (op2, set) = await ReadFrame(server);
            received.Add(set);
            Assert.Equal(1, op2);
            await WriteFrame(server, 1, """{"cmd":"SET_ACTIVITY","evt":null,"data":{}}""");
        });

        await using var client = new DiscordRpcClient("123456789012345678", prefix);
        Assert.True(await client.ConnectAsync());
        var start = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        await client.SetActivityAsync(new DiscordActivity("Never Gonna Give You Up", "Rick Astley", "https://i.ytimg.com/vi/x/hqdefault.jpg", "Whenever You Need Somebody", start, start.AddSeconds(213)));
        await serverTask;

        Assert.Equal("123456789012345678", received[0]!["client_id"]!.GetValue<string>());
        var activity = received[1]!["args"]!["activity"]!;
        Assert.Equal("SET_ACTIVITY", received[1]!["cmd"]!.GetValue<string>());
        Assert.Equal(2, activity["type"]!.GetValue<int>());
        Assert.Equal("Never Gonna Give You Up", activity["details"]!.GetValue<string>());
        Assert.Equal("Rick Astley", activity["state"]!.GetValue<string>());
        Assert.Equal(1_700_000_213_000, activity["timestamps"]!["end"]!.GetValue<long>());
        Assert.Equal("https://i.ytimg.com/vi/x/hqdefault.jpg", activity["assets"]!["large_image"]!.GetValue<string>());
    }

    [Fact]
    public async Task Returns_false_when_discord_is_not_running()
    {
        await using var client = new DiscordRpcClient("123", "sonic-missing-" + Guid.NewGuid().ToString("N") + "-");
        Assert.False(await client.ConnectAsync());
    }

    [Fact]
    public void Rejects_non_numeric_id() => Assert.Throws<ArgumentException>(() => new DiscordRpcClient("my app"));

    [Fact]
    public void Clips_long_and_short_texts()
    {
        var a = DiscordRpcClient.BuildActivity(new DiscordActivity("x", new string('y', 300), null, null, null, null));
        Assert.Equal(2, a["details"]!.GetValue<string>().Length);
        Assert.Equal(128, a["state"]!.GetValue<string>().Length);
        Assert.Null(a["timestamps"]);
    }
}

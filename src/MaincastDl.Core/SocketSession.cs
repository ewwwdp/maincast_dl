using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace MaincastDl.Core;

public sealed class SocketSession : IAsyncDisposable
{
    private const string Origin = "https://vod-maincast.cosmonova-broadcast.tv";

    private readonly ClientWebSocket _ws;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _heartbeat;

    private SocketSession(ClientWebSocket ws, string mediaId)
    {
        _ws = ws;
        _heartbeat = Heartbeat(ws, mediaId, _cts.Token);
    }

    public static async Task<SocketSession> ConnectAsync(string host, string mediaId)
    {
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Origin", Origin);
        await ws.ConnectAsync(new Uri($"wss://{host}/ws"), CancellationToken.None);
        return new SocketSession(ws, mediaId);
    }

    public async Task<string> GetHashAsync()
    {
        var buf = new StringBuilder();
        byte[] chunk = new byte[65536];
        WebSocketReceiveResult r;
        do
        {
            r = await _ws.ReceiveAsync(new ArraySegment<byte>(chunk), CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(15));
            buf.Append(Encoding.UTF8.GetString(chunk, 0, r.Count));
        } while (!r.EndOfMessage);

        return ExtractHash(buf.ToString());
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try { await _heartbeat; }
        catch { }
        await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        _ws.Dispose();
    }

    private static string ExtractHash(string message)
    {
        JsonElement payload = JsonDocument.Parse(message).RootElement.GetProperty("payload");
        string json = payload.ValueKind == JsonValueKind.String
            ? payload.GetString()!
            : payload.GetRawText();
        return JsonDocument.Parse(json).RootElement.GetProperty("hash").GetString()!;
    }

    private static async Task Heartbeat(ClientWebSocket ws, string mediaId, CancellationToken token)
    {
        byte[] msg = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            type = "currentTime",
            payload = new { profile_id = "", media_id = mediaId, time_stamp = 0.0 },
        }));
        try
        {
            while (!token.IsCancellationRequested)
            {
                await ws.SendAsync(msg, WebSocketMessageType.Text, true, token);
                await Task.Delay(TimeSpan.FromSeconds(30), token);
            }
        }
        catch { }
    }
}

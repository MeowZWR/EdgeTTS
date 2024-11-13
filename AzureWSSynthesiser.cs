using System.Net.WebSockets;
using System.Text;

namespace EdgeTTS;

internal static class AzureWSSynthesiser
{
    private const string SPEECH_CONFIG_PATH = "Path:speech.config";
    private const string SSML_PATH = "Path:ssml";
    private const string TURN_START_PATH = "Path:turn.start";
    private const string TURN_END_PATH = "Path:turn.end";
    private const string AUDIO_PATH = "Path:audio";

    private enum ProtocolState
    {
        NotStarted,
        TurnStarted,
        Streaming
    }

    public static async Task<byte[]> SynthesisAsync(
        WebSocket ws,
        CancellationToken cancellationToken,
        string text,
        int speed,
        int pitch,
        int volume,
        string voice,
        string? style = null,
        int styleDegree = 100,
        string? role = null)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffK");

        try
        {
            await SendConfigurationAsync(ws, requestId, timestamp, cancellationToken);
            await SendSpeechRequestAsync(ws, requestId, timestamp, text, speed, pitch, volume, voice, style,
                                         styleDegree, role, cancellationToken);

            return await ReceiveAudioDataAsync(ws, requestId, cancellationToken);
        }
        catch (Exception ex)
        {
            await SafeCloseWebSocketAsync(ws);
            throw new IOException("Synthesis failed", ex);
        }
    }

    private static async Task SendConfigurationAsync(
        WebSocket ws, string requestId, string timestamp, CancellationToken cancellationToken)
    {
        var config = new StringBuilder()
                     .AppendLine($"{SPEECH_CONFIG_PATH}")
                     .AppendLine($"X-RequestId:{requestId}")
                     .AppendLine($"X-Timestamp:{timestamp}")
                     .AppendLine("Content-Type:application/json")
                     .AppendLine()
                     .AppendLine(
                         "{\"context\":{\"synthesis\":{\"audio\":{\"metadataoptions\":{\"sentenceBoundaryEnabled\":\"false\",\"wordBoundaryEnabled\":\"false\"},\"outputFormat\":\"audio-24khz-48kbitrate-mono-mp3\"}}}}")
                     .ToString();

        await SendWebSocketTextAsync(ws, config, cancellationToken);
    }

    private static async Task SendSpeechRequestAsync(
        WebSocket ws,
        string requestId,
        string timestamp,
        string text,
        int speed,
        int pitch,
        int volume,
        string voice,
        string? style,
        int styleDegree,
        string? role,
        CancellationToken cancellationToken)
    {
        var ssml = CreateSSML(text, speed, pitch, volume, voice, style, styleDegree, role);
        var request = new StringBuilder()
                      .AppendLine($"{SSML_PATH}")
                      .AppendLine($"X-RequestId:{requestId}")
                      .AppendLine($"X-Timestamp:{timestamp}")
                      .AppendLine("Content-Type:application/ssml+xml")
                      .AppendLine()
                      .AppendLine(ssml)
                      .ToString();

        await SendWebSocketTextAsync(ws, request, cancellationToken);
    }

    private static async Task<byte[]> ReceiveAudioDataAsync(WebSocket ws, string requestId, CancellationToken cancellationToken)
    {
        using var session = new WebSocketHelper.WebSocketSession(ws);
        using var buffer = new MemoryStream();
        var state = ProtocolState.NotStarted;

        while (!cancellationToken.IsCancellationRequested)
        {
            var message = await session.ReceiveNextMessageAsync(cancellationToken);

            switch (message.Type)
            {
                case WebSocketMessageType.Text:
                    state = await HandleTextMessageAsync(message.MessageStr, requestId, state);
                    if (state == ProtocolState.Streaming && message.MessageStr.Contains(TURN_END_PATH))
                    {
                        return buffer.ToArray();
                    }
                    break;

                case WebSocketMessageType.Binary:
                    await HandleBinaryMessageAsync(message.MessageBinary, requestId, state, buffer);
                    state = ProtocolState.Streaming;
                    break;

                case WebSocketMessageType.Close:
                    throw new IOException("Connection closed unexpectedly");
            }
        }

        throw new OperationCanceledException();
    }

    private static async Task<ProtocolState> HandleTextMessageAsync(string message, string requestId, ProtocolState state)
    {
        if (!message.Contains(requestId)) return state;

        return state switch
        {
            ProtocolState.NotStarted when message.Contains(TURN_START_PATH) => ProtocolState.TurnStarted,
            ProtocolState.TurnStarted when message.Contains(TURN_END_PATH) =>
                throw new IOException("Unexpected turn.end"),
            ProtocolState.Streaming when message.Contains(TURN_END_PATH) => state,
            _ => state
        };
    }

    private static async Task HandleBinaryMessageAsync(
        byte[] data, string requestId, ProtocolState state, MemoryStream buffer)
    {
        if (data.Length < 2) throw new IOException("Message too short");

        var headerLen = (data[0] << 8) + data[1];
        if (data.Length < 2 + headerLen) throw new IOException("Message too short");

        var header = Encoding.UTF8.GetString(data, 2, headerLen);
        if (!header.EndsWith($"{AUDIO_PATH}\r\n")) return;
        if (!header.Contains(requestId)) throw new IOException("Unexpected request id during streaming");

        await buffer.WriteAsync(data.AsMemory(2 + headerLen));
    }

    private static async Task SendWebSocketTextAsync(WebSocket ws, string message, CancellationToken cancellationToken)
    {
        var buffer = Encoding.UTF8.GetBytes(message);
        await ws.SendAsync(buffer, WebSocketMessageType.Text, true, cancellationToken);
    }

    private static async Task SafeCloseWebSocketAsync(WebSocket ws)
    {
        try
        {
            if (ws.State == WebSocketState.Open)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Synthesis completed", CancellationToken.None);
        }
        catch { ws.Abort(); } finally { ws.Dispose(); }
    }

    public static string CreateSSML(
        string text,
        int speed,
        int pitch,
        int volume,
        string voice,
        string? style = null,
        int styleDegree = 100,
        string? role = null)
    {
        var expressAs = BuildExpressAs(text, style, styleDegree, role);

        return new StringBuilder()
               .Append(
                   "<speak xmlns=\"http://www.w3.org/2001/10/synthesis\" xmlns:mstts=\"http://www.w3.org/2001/mstts\" version=\"1.0\" xml:lang=\"en-US\">")
               .Append($"<voice name=\"{voice}\">")
               .Append(
                   $"<prosody rate=\"{speed - 100}%\" pitch=\"{(pitch - 100) / 2}%\" volume=\"{volume.Clamp(1, 100)}\">")
               .Append(expressAs)
               .Append("</prosody></voice></speak>")
               .ToString();
    }

    private static string BuildExpressAs(string text, string? style, int styleDegree, string? role)
    {
        if (style == "general" || role == "Default" ||
            (string.IsNullOrWhiteSpace(style) && string.IsNullOrWhiteSpace(role)))
            return text;

        var sb = new StringBuilder("<mstts:express-as");

        if (!string.IsNullOrWhiteSpace(style))
        {
            sb.Append($" style=\"{style}\"")
              .Append($" styledegree=\"{Math.Max(1, styleDegree) / 100.0f}\"");
        }

        if (!string.IsNullOrWhiteSpace(role)) sb.Append($" role=\"{role}\"");

        return sb.Append('>')
                 .Append(text)
                 .Append("</mstts:express-as>")
                 .ToString();
    }
}

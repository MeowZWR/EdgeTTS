using System.Net.WebSockets;
using System.Text;

namespace EdgeTTS;

internal static class AzureWSSynthesiser
{
    private const int WEBSOCKET_TIMEOUT_MS = 15000;
    private const int BUFFER_SIZE          = 4096;
    private const int MAX_RETRIES          = 3;
    private const int RETRY_DELAY_MS       = 1000;

    private static class PathConstants
    {
        public const string SPEECH_CONFIG = "Path:speech.config";
        public const string SSML = "Path:ssml";
        public const string TURN_START = "Path:turn.start";
        public const string TURN_END = "Path:turn.end";
        public const string AUDIO = "Path:audio";
    }

    private enum ProtocolState
    {
        NotStarted,
        TurnStarted,
        Streaming
    }

    public static async Task<byte[]> SynthesisAsync(
        WebSocket         ws,
        CancellationToken cancellationToken,
        string            text,
        int               speed,
        int               pitch,
        int               volume,
        string            voice,
        string?           style       = null,
        int               styleDegree = 100,
        string?           role        = null)
    {
        ArgumentNullException.ThrowIfNull(ws);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(voice);

        int        retryCount    = 0;
        Exception? lastException = null;

        while (retryCount < MAX_RETRIES)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(WEBSOCKET_TIMEOUT_MS);

                return await ExecuteSynthesisAsync(
                           ws, timeoutCts.Token, text, speed, pitch, volume, voice, style, styleDegree, role);
            }
            catch (Exception ex) when (ShouldRetry(ex) && retryCount < MAX_RETRIES - 1)
            {
                lastException = ex;
                retryCount++;
                
                // 等待一段时间后重试
                await Task.Delay(RETRY_DELAY_MS * retryCount, cancellationToken);
                
                // 检查WebSocket状态
                if (ws.State != WebSocketState.Open)
                {
                    throw new InvalidOperationException(
                        "WebSocket connection lost and needs to be re-established", ex);
                }
            }
        }

        throw new IOException($"Synthesis failed after {MAX_RETRIES} attempts", lastException);
    }

    private static bool ShouldRetry(Exception ex)
    {
        return ex switch
        {
            IOException ioEx        => true,
            WebSocketException wsEx => wsEx.WebSocketErrorCode != WebSocketError.InvalidState,
            _                       => false
        };
    }

    private static async Task<byte[]> ExecuteSynthesisAsync(
        WebSocket ws,
        CancellationToken cancellationToken,
        string text,
        int speed,
        int pitch,
        int volume,
        string voice,
        string? style,
        int styleDegree,
        string? role)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffK");

        try
        {
            if (ws.State != WebSocketState.Open)
            {
                throw new WebSocketException(WebSocketError.InvalidState, "WebSocket connection is not open");
            }

            // 创建一个信号量来控制发送操作
            using var sendLock = new SemaphoreSlim(1, 1);
            
            await SendWithRetryAsync(() => 
                SendConfigurationAsync(ws, requestId, timestamp, cancellationToken), sendLock, cancellationToken);
                
            await SendWithRetryAsync(() => 
                SendSpeechRequestAsync(ws, requestId, timestamp, text, speed, pitch, volume, 
                    voice, style, styleDegree, role, cancellationToken), sendLock, cancellationToken);

            return await ReceiveAudioDataAsync(ws, requestId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await SafeCloseWebSocketAsync(ws);
            throw;
        }
    }

    private static async Task SendWithRetryAsync(
        Func<Task> sendAction, 
        SemaphoreSlim sendLock,
        CancellationToken cancellationToken)
    {
        int attempts = 0;
        while (attempts < 2) // 最多重试一次
        {
            try
            {
                await sendLock.WaitAsync(cancellationToken);
                await sendAction();
                return;
            }
            catch (Exception ex) when (ex is IOException or WebSocketException && attempts == 0)
            {
                attempts++;
                await Task.Delay(500, cancellationToken); // 短暂延迟后重试
            }
            finally
            {
                sendLock.Release();
            }
        }
    }
    
    private static async Task<byte[]> ReceiveAudioDataAsync(
        WebSocket ws,
        string requestId,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var state = ProtocolState.NotStarted;
        var receiveBuffer = new byte[BUFFER_SIZE];
        var messageBuffer = new List<byte>();
        
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(receiveBuffer), cancellationToken);
                }
                catch (WebSocketException ex)
                {
                    if (buffer.Length > 0 && state == ProtocolState.Streaming)
                    {
                        // 如果已经收到了一些音频数据，尝试返回已收到的数据
                        return buffer.ToArray();
                    }
                    throw;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    if (buffer.Length > 0 && state == ProtocolState.Streaming)
                    {
                        return buffer.ToArray();
                    }
                    throw new IOException("Connection closed unexpectedly");
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var message = Encoding.UTF8.GetString(receiveBuffer, 0, result.Count);
                    state = await HandleTextMessageAsync(message, requestId, state);

                    if (state == ProtocolState.Streaming && message.Contains(PathConstants.TURN_END))
                    {
                        return buffer.ToArray();
                    }
                }
                else if (result.MessageType == WebSocketMessageType.Binary)
                {
                    messageBuffer.AddRange(new ArraySegment<byte>(receiveBuffer, 0, result.Count));
                    
                    if (result.EndOfMessage)
                    {
                        await HandleBinaryMessageAsync(messageBuffer.ToArray(), requestId, state, buffer);
                        state = ProtocolState.Streaming;
                        messageBuffer.Clear();
                    }
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException || ex is IOException)
        {
            if (buffer.Length > 0 && state == ProtocolState.Streaming)
            {
                // 如果已经收到了音频数据，返回部分数据而不是抛出异常
                return buffer.ToArray();
            }
            throw;
        }

        throw new OperationCanceledException();
    }

    private static async Task SendWebSocketTextAsync(
        WebSocket ws,
        string message,
        CancellationToken cancellationToken)
    {
        try
        {
            if (ws.State != WebSocketState.Open)
            {
                throw new WebSocketException(WebSocketError.InvalidState);
            }

            byte[] buffer = Encoding.UTF8.GetBytes(message);
            var segment = new ArraySegment<byte>(buffer);

            // 分块发送大消息
            const int chunkSize = 4096;
            for (int i = 0; i < segment.Count; i += chunkSize)
            {
                int size = Math.Min(chunkSize, segment.Count - i);
                bool endOfMessage = (i + size) >= segment.Count;
                
                await ws.SendAsync(
                    new ArraySegment<byte>(segment.Array!, segment.Offset + i, size),
                    WebSocketMessageType.Text,
                    endOfMessage,
                    cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely, ex);
        }
    }
    
    private static async Task SendConfigurationAsync(
        WebSocket ws,
        string requestId,
        string timestamp,
        CancellationToken cancellationToken)
    {
        var config = new StringBuilder()
            .AppendLine(PathConstants.SPEECH_CONFIG)
            .AppendLine($"X-RequestId:{requestId}")
            .AppendLine($"X-Timestamp:{timestamp}")
            .AppendLine("Content-Type:application/json")
            .AppendLine()
            .AppendLine("""{"context":{"synthesis":{"audio":{"metadataoptions":{"sentenceBoundaryEnabled":"false","wordBoundaryEnabled":"false"},"outputFormat":"audio-24khz-48kbitrate-mono-mp3"}}}}""")
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
            .AppendLine(PathConstants.SSML)
            .AppendLine($"X-RequestId:{requestId}")
            .AppendLine($"X-Timestamp:{timestamp}")
            .AppendLine("Content-Type:application/ssml+xml")
            .AppendLine()
            .AppendLine(ssml)
            .ToString();

        await SendWebSocketTextAsync(ws, request, cancellationToken);
    }

    private static async Task<ProtocolState> HandleTextMessageAsync(
        string message,
        string requestId,
        ProtocolState state)
    {
        if (!message.Contains(requestId))
            return state;

        return state switch
        {
            ProtocolState.NotStarted when message.Contains(PathConstants.TURN_START) => ProtocolState.TurnStarted,
            ProtocolState.TurnStarted when message.Contains(PathConstants.TURN_END) =>
                throw new IOException("Unexpected turn.end"),
            ProtocolState.Streaming when message.Contains(PathConstants.TURN_END) => state,
            _ => state
        };
    }

    private static async Task HandleBinaryMessageAsync(
        byte[] data,
        string requestId,
        ProtocolState state,
        MemoryStream buffer)
    {
        if (data.Length < 2)
            throw new IOException("Message too short");

        var headerLen = BitConverter.ToUInt16([data[1], data[0]], 0);
        if (data.Length < 2 + headerLen)
            throw new IOException("Message too short");

        var header = Encoding.UTF8.GetString(data, 2, headerLen);
        if (!header.EndsWith($"{PathConstants.AUDIO}\r\n"))
            return;

        if (!header.Contains(requestId))
            throw new IOException("Unexpected request id during streaming");

        await buffer.WriteAsync(data.AsMemory(2 + headerLen));
    }

    private static async Task SafeCloseWebSocketAsync(WebSocket ws)
    {
        try
        {
            if (ws.State == WebSocketState.Open)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await ws.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Synthesis completed",
                    cts.Token);
            }
        }
        catch
        {
            try { ws.Abort(); }
            catch { /* ignored */ }
        }
    }

    private static string CreateSSML(
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
            .Append("<speak xmlns=\"http://www.w3.org/2001/10/synthesis\" xmlns:mstts=\"http://www.w3.org/2001/mstts\" version=\"1.0\" xml:lang=\"en-US\">")
            .Append($"<voice name=\"{voice}\">")
            .Append($"<prosody rate=\"{speed - 100}%\" pitch=\"{(pitch - 100) / 2}%\" volume=\"{Math.Clamp(volume, 1, 100)}\">")
            .Append(expressAs)
            .Append("</prosody></voice></speak>")
            .ToString();
    }

    private static string BuildExpressAs(string text, string? style, int styleDegree, string? role)
    {
        if (style == "general" || role == "Default" ||
            (string.IsNullOrWhiteSpace(style) && string.IsNullOrWhiteSpace(role)))
        {
            return text;
        }

        var sb = new StringBuilder("<mstts:express-as");

        if (!string.IsNullOrWhiteSpace(style))
        {
            sb.Append($" style=\"{style}\"")
              .Append($" styledegree=\"{Math.Max(1, styleDegree) / 100.0f}\"");
        }

        if (!string.IsNullOrWhiteSpace(role))
        {
            sb.Append($" role=\"{role}\"");
        }

        return sb.Append('>')
                 .Append(text)
                 .Append("</mstts:express-as>")
                 .ToString();
    }
}

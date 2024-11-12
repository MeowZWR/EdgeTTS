using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace EdgeTTS;

public class EdgeTTSEngine(string cacheFolder) : IDisposable
{
    private const string WSS_URL = "wss://speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1?TrustedClientToken=6A5AA1D4EAFF4E9FB37E23D68491D6F4";

    private readonly CancellationTokenSource _wsCancellation = new();
    private readonly CancellationTokenSource _operationCancellation = new();
    private WebSocket? _webSocket;
    private bool _disposed;

    public static readonly Voice[] Voices =
    [
        new("zh-CN-XiaoxiaoNeural", "晓晓 (中文-普通话-女)"),
        new("zh-CN-XiaoyiNeural", "晓依 (中文-普通话-女)"),
        new("zh-CN-YunjianNeural", "云健 (中文-普通话-男)"),
        new("zh-CN-YunyangNeural", "云扬 (中文-普通话-新闻-男)"),
        new("zh-CN-YunxiaNeural", "云霞 (中文-普通话-儿童-男)"),
        new("zh-CN-YunxiNeural", "云希 (中文-普通话-男)"),
        new("zh-HK-HiuMaanNeural", "曉佳 (中文-廣東話-女)"),
        new("zh-TW-HsiaoChenNeural", "曉臻 (中文-國語-女)"),
        new("ja-JP-NanamiNeural", "七海 (日本語-女)"),
        new("en-US-AriaNeural", "Aria (English-American-Female)"),
        new("en-US-JennyNeural", "Jenny (English-American-Female)"),
        new("en-US-GuyNeural", "Guy (English-American-Male)"),
        new("en-GB-SoniaNeural", "Sonia (English-Britain-Female)")
    ];

    public void Speak(string text, EdgeTTSSettings settings)
    {
        ThrowIfDisposed();

        try
        {
            var task = Task.Run(() => SpeakAsync(text, settings), _operationCancellation.Token);
            task.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // ignored
        }
    }

    public async Task SpeakAsync(string text, EdgeTTSSettings settings)
    {
        ThrowIfDisposed();

        text = SecurityElement.Escape(text);

        var audioFile = GetOrCreateAudioFile(text, settings.ToString(), () =>
        {
            for (var retry = 0; retry < 3; retry++)
            {
                try
                {
                    return Synthesize(settings, text);
                }
                catch (Exception ex) when (IsConnectionResetError(ex))
                {
                    if (retry == 2) throw;
                }
            }
            return null;
        });

        if (!string.IsNullOrWhiteSpace(audioFile))
        {
            await AudioPlayer.PlayAudioAsync(audioFile);
        }
    }

    private static bool IsConnectionResetError(Exception? ex)
    {
        while (ex != null)
        {
            if (ex is SocketException { SocketErrorCode: SocketError.ConnectionReset })
                return true;
            ex = ex.InnerException;
        }
        return false;
    }

    private byte[]? Synthesize(EdgeTTSSettings settings, string text)
    {
        try
        {
            var ws = GetWebSocketConnection();
            return ws == null ? null : AzureWSSynthesiser.Synthesis(
                ws, _wsCancellation, text,
                settings.Speed, settings.Pitch,
                settings.Volume, settings.Voice);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            throw;
        }
    }

    private WebSocket? GetWebSocketConnection()
    {
        ThrowIfDisposed();

        lock (this)
        {
            if (_wsCancellation.IsCancellationRequested) return null;

            if (_webSocket == null || ShouldRecreateWebSocket())
            {
                _webSocket?.Dispose();
                _webSocket = CreateWebSocket();
            }

            return _webSocket;
        }
    }

    private bool ShouldRecreateWebSocket() => _webSocket?.State switch
    {
        WebSocketState.None => false,
        WebSocketState.Connecting or WebSocketState.Open => false,
        _ => true
    };

    private WebSocket CreateWebSocket()
    {
        var ws = SystemClientWebSocket.CreateClientWebSocket();
        SetWebSocketHeaders(ws);
        ws.ConnectAsync(new Uri(WSS_URL), _wsCancellation.Token).Wait();
        return ws;
    }

    private static void SetWebSocketHeaders(WebSocket ws)
    {
        dynamic options = ws switch
        {
            ClientWebSocket clientWs => clientWs.Options,
            System.Net.WebSockets.Managed.ClientWebSocket managedWs => managedWs.Options,
            _ => throw new ArgumentException("Unsupported WebSocket type")
        };

        options.SetRequestHeader("Accept-Encoding", "gzip, deflate, br");
        options.SetRequestHeader("Cache-Control", "no-cache");
        options.SetRequestHeader("Pragma", "no-cache");
    }

    private string GetOrCreateAudioFile(string text, string parameter, Func<byte[]?> createContent)
    {
        ThrowIfDisposed();

        lock (this)
        {
            var hash = ComputeHash($"EdgeTTS.{text}.{parameter}")[..10];
            var cacheFile = Path.Combine(cacheFolder, $"{hash}.mp3");

            if (!File.Exists(cacheFile))
            {
                Directory.CreateDirectory(cacheFolder);
                var content = createContent();
                if (content == null) return cacheFile;

                File.WriteAllBytes(cacheFile, content);
            }

            return cacheFile;
        }
    }

    private static string ComputeHash(string input)
    {
        using var sha1 = SHA1.Create();
        return sha1.ComputeHash(Encoding.UTF8.GetBytes(input)).ToBase36String();
    }

    private void ThrowIfDisposed()
    {
        if (!_disposed) return;
        throw new ObjectDisposedException(nameof(EdgeTTSEngine));
    }

    public void Stop()
    {
        if (!_disposed)
        {
            _operationCancellation.Cancel();
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                Stop();
                _operationCancellation.Dispose();
                _wsCancellation.Dispose();
                _webSocket?.Dispose();
            }

            _disposed = true;
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    ~EdgeTTSEngine()
    {
        Dispose(false);
    }
}

using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace EdgeTTS;

public class EdgeTTSEngine : IDisposable
{
    private const string WSS_URL = "wss://speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1?TrustedClientToken=6A5AA1D4EAFF4E9FB37E23D68491D6F4";
    private const int MAX_CONCURRENT_AUDIO = 3;

    private readonly string _cacheDir;
    private readonly CancellationTokenSource _wsCancellation = new();
    private readonly SemaphoreSlim _audioSemaphore = new(MAX_CONCURRENT_AUDIO);
    private readonly ConcurrentQueue<QueuedAudio> _audioQueue = [];
    private readonly CancellationTokenSource _queueCancellation = new();
    private WebSocket? _webSocket;
    private bool _disposed;

    public static readonly Voice[] Voices =
    [
        new("zh-CN-XiaoxiaoNeural", "中文-普通话-女 晓晓"),
        new("zh-CN-XiaoyiNeural", "中文-普通话-女 晓依"),
        new("zh-CN-YunjianNeural", "中文-普通话-男 云健"),
        new("zh-CN-YunyangNeural", "中文-普通话-新闻-男 云扬"),
        new("zh-CN-YunxiaNeural", "中文-普通话-儿童-男 云霞"),
        new("zh-CN-YunxiNeural", "中文-普通话-男 云希"),
        new("zh-HK-HiuMaanNeural", "中文-粤语-女 曉佳"),
        new("zh-TW-HsiaoChenNeural", "中文-台普-女 曉臻"),
        new("ja-JP-NanamiNeural", "日语-女 七海"),
        new("en-US-AriaNeural", "英语-美国-女 阿莉雅"),
        new("en-US-JennyNeural", "英语-美国-女 珍妮"),
        new("en-US-GuyNeural", "英语-美国-男 盖"),
        new("en-GB-SoniaNeural", "英语-英国-女 索尼娅")
    ];

    public EdgeTTSEngine(string cacheFolder)
    {
        _cacheDir = cacheFolder;
        StartQueueProcessor();
    }

    private void StartQueueProcessor()
    {
        Task.Run(async () =>
        {
            while (!_queueCancellation.Token.IsCancellationRequested)
            {
                if (_audioQueue.TryDequeue(out var queuedAudio))
                {
                    try
                    {
                        await _audioSemaphore.WaitAsync(_queueCancellation.Token);
                        await PlayAudioFromQueue(queuedAudio);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        queuedAudio.Completion.TrySetException(ex);
                    }
                }
                else
                {
                    await Task.Delay(100, _queueCancellation.Token);
                }
            }
        }, _queueCancellation.Token);
    }

    private async Task PlayAudioFromQueue(QueuedAudio queuedAudio)
    {
        try
        {
            await AudioPlayer.PlayAudioAsync(queuedAudio.FilePath);
            queuedAudio.Completion.TrySetResult(true);
        }
        finally
        {
            _audioSemaphore.Release();
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
            var queuedAudio = new QueuedAudio(audioFile);
            _audioQueue.Enqueue(queuedAudio);
            await queuedAudio.Completion.Task;
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
            var cacheFile = Path.Combine(_cacheDir, $"{hash}.mp3");

            if (!File.Exists(cacheFile))
            {
                Directory.CreateDirectory(_cacheDir);
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
            _queueCancellation.Cancel();
            while (_audioQueue.TryDequeue(out var audio))
            {
                audio.Completion.TrySetCanceled();
            }
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                Stop();
                _queueCancellation.Dispose();
                _wsCancellation.Dispose();
                _audioSemaphore.Dispose();
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

    private class QueuedAudio(string filePath)
    {
        public string                     FilePath   { get; } = filePath;
        public TaskCompletionSource<bool> Completion { get; } = new();
    }
}

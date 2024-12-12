using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace EdgeTTS;

public sealed class EdgeTTSEngine(string cacheFolder) : IDisposable
{
    private const string WSS_URL = "wss://speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1?TrustedClientToken=6A5AA1D4EAFF4E9FB37E23D68491D6F4";
    private readonly CancellationTokenSource _operationCts = new();
    private bool _disposed;

    public static readonly Voice[] Voices =
    [
        new("zh-CN-XiaoxiaoNeural", "晓晓 (中文-普通话-女)"),
        new("zh-CN-XiaoyiNeural", "晓依 (中文-普通话-女)"),
        new("zh-CN-YunjianNeural", "云健 (中文-普通话-男)"),
        new("zh-CN-YunyangNeural", "云扬 (中文-普通话-新闻-男)"),
        new("zh-CN-YunxiaNeural", "云夏 (中文-普通话-儿童-男)"),
        new("zh-CN-YunxiNeural", "云希 (中文-普通话-男)"),
        new("zh-HK-HiuGaaiNeural", "曉佳 (中文-廣東話-女)"),
        new("zh-HK-HiuMaanNeural", "曉曼 (中文-廣東話-女)"),
        new("zh-HK-WanLungNeural", "雲龍 (中文-廣東話-男)"),
        new("zh-TW-HsiaoChenNeural", "曉臻 (中文-國語-女)"),
        new("zh-TW-HsiaoYuNeural", "曉雨 (中文-國語-女)"),
        new("zh-TW-YunJheNeural", "雲哲 (中文-國語-男)"),
        new("ja-JP-NanamiNeural", "七海 (日本語-女)"),
        new("ja-JP-KeitaNeural", "庆太 (日本語-男)"),
        new("en-US-AriaNeural", "Aria (English-American-Female)"),
        new("en-US-JennyNeural", "Jenny (English-American-Female)"),
        new("en-US-AnaNeural", "Ana (English-American-Child-Female)"),
        new("en-US-MichelleNeural", "Michelle (English-American-Female)"),
        new("en-US-GuyNeural", "Guy (English-American-Male)"),
        new("en-US-ChristopherNeural", "Christopher (English-American-Male)"),
        new("en-US-EricNeural", "Eric (English-American-Male)"),
        new("en-US-RogerNeural", "Roger (English-American-Male)"),
        new("en-US-SteffanNeural", "Steffan (English-American-Male)"),
        new("en-GB-SoniaNeural", "Sonia (English-Britain-Female)"),
    ];

    public void Speak(string text, EdgeTTSSettings settings)
    {
        ThrowIfDisposed();
        try
        {
            Task.Run(() => SpeakAsync(text, settings), _operationCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // ignored
        }
    }

    public async Task SpeakAsync(string text, EdgeTTSSettings settings)
    {
        ThrowIfDisposed();
        var safeText = SecurityElement.Escape(text.Replace('：', ':'));
        var audioFile = await GetOrCreateAudioFileAsync(safeText, settings);

        if (!string.IsNullOrWhiteSpace(audioFile))
        {
            await AudioPlayer.PlayAudioAsync(audioFile);
        }
    }

    private async Task<string> GetOrCreateAudioFileAsync(string text, EdgeTTSSettings settings)
    {
        var hash = ComputeHash($"EdgeTTS.{text}.{settings}")[..10];
        var cacheFile = Path.Combine(cacheFolder, $"{hash}.mp3");

        if (!File.Exists(cacheFile))
        {
            Directory.CreateDirectory(cacheFolder);
            var content = await SynthesizeWithRetryAsync(settings, text);
            if (content != null)
            {
                await File.WriteAllBytesAsync(cacheFile, content);
            }
        }

        return cacheFile;
    }

    private async Task<byte[]?> SynthesizeWithRetryAsync(EdgeTTSSettings settings, string text)
    {
        for (var retry = 0; retry < 10; retry++)
        {
            try
            {
                using var ws = await CreateWebSocketAsync();
                return await AzureWSSynthesiser.SynthesisAsync(
                    ws, _operationCts.Token, text,
                    settings.Speed, settings.Pitch,
                    settings.Volume, settings.Voice);
            }
            catch (Exception ex) when (IsConnectionResetError(ex) && retry < 9)
            {
                // ignored
            }
        }

        return null;
    }

    private async Task<WebSocket> CreateWebSocketAsync()
    {
        var ws = SystemClientWebSocket.CreateClientWebSocket();
        ConfigureWebSocket(ws);
        await ws.ConnectAsync(new Uri($"{WSS_URL}&Sec-MS-GEC={Sec_MS_GEC.Get()}&Sec-MS-GEC-Version=1-132.0.2917.0"), _operationCts.Token);
        return ws;
    }

    private static void ConfigureWebSocket(WebSocket ws)
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

    private static bool IsConnectionResetError(Exception? ex) =>
        ex?.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset } ||
        ex is SocketException { SocketErrorCode: SocketError.ConnectionReset };

    private static string ComputeHash(string input) 
        => SHA1.HashData(Encoding.UTF8.GetBytes(input)).ToBase36String();

    private void ThrowIfDisposed()
    {
        if (!_disposed) return;
        throw new ObjectDisposedException(nameof(EdgeTTSEngine));
    }

    public void Stop()
    {
        if (!_disposed)
            _operationCts.Cancel();
    }

    public void Dispose()
    {
        if (_disposed) return;

        _operationCts.Cancel();
        _operationCts.Dispose();

        _disposed = true;
        GC.SuppressFinalize(this);
    }
}

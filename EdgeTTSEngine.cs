using System.Diagnostics;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace EdgeTTS;

public sealed class EdgeTTSEngine(string cacheFolder, Action<string>? logHandler = null) : IDisposable
{
    private const string WSS_URL =
        "wss://speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1?TrustedClientToken=6A5AA1D4EAFF4E9FB37E23D68491D6F4";
    private readonly CancellationTokenSource operationCts = new();
    private          bool                    disposed;

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

    private void Log(string message)
    {
        logHandler?.Invoke(message);
    }

    public void Speak(string text, EdgeTTSSettings settings)
    {
        ThrowIfDisposed();
        try
        {
            Task.Run(async () => await SpeakAsync(text, settings).ConfigureAwait(false), operationCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // ignored
        }
    }

    public async Task SpeakAsync(string text, EdgeTTSSettings settings)
    {
        ThrowIfDisposed();
        var audioFile = await GetOrCreateAudioFileAsync(text, settings).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(audioFile)) return;
        await AudioPlayer.PlayAudioAsync(audioFile, settings.Volume).ConfigureAwait(false);
    }

    public async Task<string> GetAudioFileAsync(string text, EdgeTTSSettings settings)
    {
        ThrowIfDisposed();
        var audioFile = await GetOrCreateAudioFileAsync(text, settings);
        return audioFile;
    }

    private async Task<string> GetOrCreateAudioFileAsync(string text, EdgeTTSSettings settings)
    {
        text = SanitizeString(text, settings);
        
        var hash = ComputeHash($"EdgeTTS.{text}.{settings}")[..10];
        var cacheFile = Path.Combine(cacheFolder, $"{hash}.mp3");

        if (!File.Exists(cacheFile))
        {
            if (!Directory.Exists(cacheFolder))
                Directory.CreateDirectory(cacheFolder);
            
            Log("开始合成语音");
            
            var stopWatch = new Stopwatch();
            stopWatch.Start();
            
            var content = await SynthesizeWithRetryAsync(settings, text).ConfigureAwait(false);
            if (content != null)
            {
                await File.WriteAllBytesAsync(cacheFile, content).ConfigureAwait(false);
                
                stopWatch.Stop();
                Log($"语音合成完成, 耗时: {stopWatch.ElapsedMilliseconds:F2}ms");
                Log($"已将语音保存到缓存文件: {cacheFile}");
            }
        }
        else
        {
            Log("使用缓存的语音文件");
        }

        return cacheFile;
    }
    
    private static string SanitizeString(string text, EdgeTTSSettings settings)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        
        foreach (var (word, phoneme) in settings.PhonemeReplacements)
            text = text.Replace(word, phoneme);
        
        var safeText = SecurityElement.Escape(text.Replace('：', ':'));
        return safeText;
    }

    private async Task<byte[]?> SynthesizeWithRetryAsync(EdgeTTSSettings settings, string text)
    {
        for (var retry = 0; retry < 10; retry++)
        {
            try
            {
                using var ws = await CreateWebSocketAsync().ConfigureAwait(false);
                return await AzureWSSynthesiser.SynthesisAsync(ws, operationCts.Token, text, settings.Speed, settings.Pitch, 100, settings.Voice)
                                               .ConfigureAwait(false);
            }
            catch (Exception ex) when (IsConnectionResetError(ex) && retry < 9)
            {
                Log($"语音合成失败, 正在重试 ({retry + 1}/10): {ex.Message}");
                await Task.Delay(1000 * (retry + 1)).ConfigureAwait(false);
            }
        }

        Log("语音合成失败, 已达到最大重试次数");
        return null;
    }

    private async Task<WebSocket> CreateWebSocketAsync()
    {
        var ws = SystemClientWebSocket.CreateClientWebSocket();
        ConfigureWebSocket(ws);
        await ws.ConnectAsync(new($"{WSS_URL}&Sec-MS-GEC={SecMSGEC.Get()}&Sec-MS-GEC-Version=1-132.0.2917.0"), operationCts.Token)
                .ConfigureAwait(false);
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
        if (!disposed) return;
        throw new ObjectDisposedException(nameof(EdgeTTSEngine));
    }

    public void Stop()
    {
        if (!disposed)
            operationCts.Cancel();
    }

    public void Dispose()
    {
        if (disposed) return;

        operationCts.Cancel();
        operationCts.Dispose();

        disposed = true;
    }
}

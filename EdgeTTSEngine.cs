using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace EdgeTTS;

public sealed class EdgeTTSEngine : IDisposable
{
    private readonly CancellationTokenSource operationCts = new();
    private          bool                    disposed;

    /// <summary>
    /// 所有可用的语音列表
    /// </summary>
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
    
    /// <param name="cacheFolder">缓存文件夹路径，用于存储生成的音频文件</param>
    /// <param name="logHandler">日志处理函数，用于接收引擎运行时的日志信息</param>
    public EdgeTTSEngine(string cacheFolder, Action<string>? logHandler = null)
    {
        this.cacheFolder = cacheFolder;
        this.logHandler = logHandler;
    }

    private readonly string cacheFolder;
    private readonly Action<string>? logHandler;

    /// <summary>
    /// 同步播放指定文本的语音
    /// </summary>
    /// <param name="text">要转换为语音的文本</param>
    /// <param name="settings">语音合成设置</param>
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

    /// <summary>
    /// 异步播放指定文本的语音
    /// </summary>
    /// <param name="text">要转换为语音的文本</param>
    /// <param name="settings">语音合成设置</param>
    /// <returns>表示异步操作的任务</returns>
    public async Task SpeakAsync(string text, EdgeTTSSettings settings)
    {
        ThrowIfDisposed();
        var audioFile = await GetOrCreateAudioFileAsync(text, settings).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(audioFile)) return;
        await AudioPlayer.PlayAudioAsync(audioFile, settings.Volume).ConfigureAwait(false);
    }

    /// <summary>
    /// 获取指定文本的音频文件路径, 可用于预先合成指定的文本音频
    /// </summary>
    /// <param name="text">要转换为语音的文本</param>
    /// <param name="settings">语音合成设置</param>
    /// <returns>音频文件的完整路径</returns>
    public async Task<string> GetAudioFileAsync(string text, EdgeTTSSettings settings)
    {
        ThrowIfDisposed();
        var audioFile = await GetOrCreateAudioFileAsync(text, settings);
        return audioFile;
    }

    /// <summary>
    /// 批量获取多个文本的音频文件路径，高效率地预先合成多个文本音频
    /// </summary>
    /// <param name="texts">要转换为语音的文本集合</param>
    /// <param name="settings">语音合成设置</param>
    /// <param name="maxConcurrency">最大并行处理数量，默认为4</param>
    /// <param name="progressCallback">进度回调函数，参数为已完成数量和总数量</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>包含所有文本对应音频文件路径的字典</returns>
    public async Task<Dictionary<string, string>> GetAudioFilesAsync(
        IEnumerable<string> texts, 
        EdgeTTSSettings settings, 
        int maxConcurrency = 4,
        Action<int, int>? progressCallback = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        
        var textList = texts.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        if (textList.Count == 0) return new Dictionary<string, string>();
        
        var result = new ConcurrentDictionary<string, string>();
        var completedCount = 0;
        
        Log($"开始批量合成 {textList.Count} 个文本的语音");
        var totalStopwatch = new Stopwatch();
        totalStopwatch.Start();

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, operationCts.Token);
        
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = maxConcurrency,
            CancellationToken = linkedCts.Token
        };
        
        try
        {
            await Parallel.ForEachAsync(textList, parallelOptions, async (text, _) =>
            {
                var audioFile = await GetOrCreateAudioFileAsync(text, settings).ConfigureAwait(false);
                result[text] = audioFile;
                var completed = Interlocked.Increment(ref completedCount);
                progressCallback?.Invoke(completed, textList.Count);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Log("批量语音合成已取消");
            throw;
        }
        catch (Exception ex)
        {
            Log($"批量语音合成过程中发生错误: {ex.Message}");
            throw;
        }
        finally
        {
            totalStopwatch.Stop();
            Log($"批量语音合成完成，共 {completedCount}/{textList.Count} 个文本，总耗时: {totalStopwatch.ElapsedMilliseconds}ms");
        }
        
        return new Dictionary<string, string>(result);
    }

    /// <summary>
    /// 批量获取多个文本和设置组合的音频文件路径，高效率地预先合成多个不同配置的文本音频
    /// </summary>
    /// <param name="textSettingsPairs">要转换为语音的文本和设置组合集合</param>
    /// <param name="maxConcurrency">最大并行处理数量，默认为4</param>
    /// <param name="progressCallback">进度回调函数，参数为已完成数量和总数量</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>包含所有文本和设置组合对应音频文件路径的字典</returns>
    public async Task<Dictionary<(string Text, EdgeTTSSettings Settings), string>> GetAudioFilesWithSettingsAsync(
        IEnumerable<(string Text, EdgeTTSSettings Settings)> textSettingsPairs,
        int maxConcurrency = 4,
        Action<int, int>? progressCallback = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        
        var pairsList = textSettingsPairs
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Text))
            .ToList();
            
        if (pairsList.Count == 0) 
            return new Dictionary<(string Text, EdgeTTSSettings Settings), string>();
        
        var result         = new ConcurrentDictionary<(string Text, EdgeTTSSettings Settings), string>();
        var completedCount = 0;
        
        Log($"开始批量合成 {pairsList.Count} 个不同配置的文本语音");
        var totalStopwatch = new Stopwatch();
        totalStopwatch.Start();
        
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, operationCts.Token);
        
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = maxConcurrency,
            CancellationToken = linkedCts.Token
        };
        
        try
        {
            await Parallel.ForEachAsync(pairsList, parallelOptions, async (pair, _) =>
            {
                var (text, settings) = pair;
                var audioFile = await GetOrCreateAudioFileAsync(text, settings).ConfigureAwait(false);
                result[(text, settings)] = audioFile;
                var completed = Interlocked.Increment(ref completedCount);
                progressCallback?.Invoke(completed, pairsList.Count);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Log("批量语音合成已取消");
            throw;
        }
        catch (Exception ex)
        {
            Log($"批量语音合成过程中发生错误: {ex.Message}");
            throw;
        }
        finally
        {
            totalStopwatch.Stop();
            Log($"批量语音合成完成，共 {completedCount}/{pairsList.Count} 个文本，总耗时: {totalStopwatch.ElapsedMilliseconds}ms");
        }
        
        return new Dictionary<(string Text, EdgeTTSSettings Settings), string>(result);
    }

    /// <summary>
    /// 停止当前正在进行的语音合成或播放操作
    /// </summary>
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

    private void Log(string message)
    {
        logHandler?.Invoke($"[EdgeTTS] {message}");
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
                using var ws = await EdgeTTSWebSocket.CreateWebSocketAsync(operationCts.Token).ConfigureAwait(false);
                return await AzureWSSynthesiser.SynthesisAsync(ws, operationCts.Token, text, settings.Speed, settings.Pitch, 100, settings.Voice)
                                               .ConfigureAwait(false);
            }
            catch (Exception ex) when (EdgeTTSWebSocket.IsConnectionResetError(ex) && retry < 9)
            {
                Log($"语音合成失败, 正在重试 ({retry + 1}/10): {ex.Message}");
                await Task.Delay(1000 * (retry + 1)).ConfigureAwait(false);
            }
        }

        Log("语音合成失败, 已达到最大重试次数");
        return null;
    }

    private static string ComputeHash(string input) 
        => SHA1.HashData(Encoding.UTF8.GetBytes(input)).ToBase36String();

    private void ThrowIfDisposed()
    {
        if (!disposed) return;
        throw new ObjectDisposedException(nameof(EdgeTTSEngine));
    }
}

using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace EdgeTTS;

public class AudioPlayer : IAsyncDisposable
{
    private readonly WaveOutEvent waveOut;
    private readonly AudioFileReader audioFile;
    private readonly TaskCompletionSource<bool> playbackStarted;
    private bool isDisposed;

    public event EventHandler<PlayStateChangedEventArgs>? PlayStateChanged;

    private AudioPlayer(string filePath)
    {
        audioFile = new AudioFileReader(filePath);
        waveOut = new WaveOutEvent();
        waveOut.PlaybackStopped += WaveOut_PlaybackStopped;
        playbackStarted = new TaskCompletionSource<bool>();
    }

    public bool IsPlaying => waveOut.PlaybackState == PlaybackState.Playing;

    public TimeSpan CurrentPosition => audioFile.CurrentTime;

    public TimeSpan Duration => audioFile.TotalTime;

    private void WaveOut_PlaybackStopped(object? sender, StoppedEventArgs e)
    {
        PlayStateChanged?.Invoke(this, new PlayStateChangedEventArgs(WMPPlayState.wmppsStopped));
        playbackStarted.TrySetResult(false);
    }

    public static async Task PlayAudioAsync(string filePath, int timeoutSeconds = 10, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("Path is null or empty", nameof(filePath));

        if (!File.Exists(filePath))
            throw new FileNotFoundException("Audio file not found", filePath);

        await using var player = new AudioPlayer(filePath);
        await player.PlayInternalAsync(timeoutSeconds, cancellationToken).ConfigureAwait(false);
    }

    private async Task PlayInternalAsync(int timeoutSeconds, CancellationToken cancellationToken)
    {
        try
        {
            waveOut.Init(audioFile);
            waveOut.Play();
            PlayStateChanged?.Invoke(this, new PlayStateChangedEventArgs(WMPPlayState.wmppsPlaying));
            playbackStarted.TrySetResult(true);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            await Task.Delay(-1, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 正常取消，不需要处理
        }
        finally
        {
            waveOut.Stop();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (isDisposed) return;

        waveOut.Stop();
        waveOut.Dispose();
        audioFile.Dispose();

        isDisposed = true;

        await Task.CompletedTask;
    }
}

public class PlayStateChangedEventArgs : EventArgs
{
    public WMPPlayState PlayState { get; }

    public PlayStateChangedEventArgs(WMPPlayState playState)
    {
        PlayState = playState;
    }
}

public enum WMPPlayState
{
    wmppsUndefined = 0,
    wmppsStopped = 1,
    wmppsPaused = 2,
    wmppsPlaying = 3,
    wmppsScanForward = 4,
    wmppsScanBackward = 5,
    wmppsBuffering = 6,
    wmppsWaiting = 7,
    wmppsMediaEnded = 8,
    wmppsTransitioning = 9,
    wmppsReady = 10,
    wmppsReconnecting = 11,
    wmppsLast = 12
}

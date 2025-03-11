using WMPLib;

namespace EdgeTTS;

public class AudioPlayer : IAsyncDisposable
{
    private readonly WindowsMediaPlayer player;
    private readonly TaskCompletionSource<bool> playbackStarted;
    private bool isDisposed;

    public event EventHandler<PlayStateChangedEventArgs>? PlayStateChanged;

    private AudioPlayer()
    {
        player = new WindowsMediaPlayer();
        player.PlayStateChange += Player_PlayStateChange;
        playbackStarted = new TaskCompletionSource<bool>();
    }

    public bool IsPlaying => player.playState == WMPPlayState.wmppsPlaying;

    public TimeSpan CurrentPosition => TimeSpan.FromSeconds(player.controls.currentPosition);

    public TimeSpan Duration => TimeSpan.FromSeconds(player.currentMedia?.duration ?? 0);

    private void Player_PlayStateChange(int newState)
    {
        var state = (WMPPlayState)newState;
        PlayStateChanged?.Invoke(this, new PlayStateChangedEventArgs(state));

        switch (state)
        {
            case WMPPlayState.wmppsPlaying:
                playbackStarted.TrySetResult(true);
                break;
            case WMPPlayState.wmppsStopped or WMPPlayState.wmppsMediaEnded:
                playbackStarted.TrySetResult(false);
                break;
        }
    }

    public static async Task PlayAudioAsync(string filePath, int timeoutSeconds = 10, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("Path is null or empty", nameof(filePath));

        if (!File.Exists(filePath))
            throw new FileNotFoundException("Audio file not found", filePath);

        await using var player = new AudioPlayer();
        await player.PlayInternalAsync(filePath, timeoutSeconds, cancellationToken);
    }

    private async Task PlayInternalAsync(string filePath, int timeoutSeconds, CancellationToken cancellationToken)
    {
        player.URL = filePath;
        player.controls.play();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        await playbackStarted.Task.WaitAsync(timeoutCts.Token);

        if (!playbackStarted.Task.Result) return;

        while (IsPlaying && !cancellationToken.IsCancellationRequested)
            await Task.Delay(100, cancellationToken);
    }

    private void SetVolume(int volume)
    {
        if (volume is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(volume), "Volumn should be between 0 and 100");

        player.settings.volume = volume;
    }

    public async ValueTask DisposeAsync()
    {
        if (!isDisposed)
        {
            await Task.Run(() =>
            {
                player.PlayStateChange -= Player_PlayStateChange;
                player.controls.stop();
                player.close();
            });
            isDisposed = true;

        }
    }
    
    public class PlayStateChangedEventArgs(WMPPlayState newState) : EventArgs
    {
        public WMPPlayState NewState { get; } = newState;
    }
}

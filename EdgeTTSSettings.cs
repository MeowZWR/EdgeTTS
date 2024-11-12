namespace EdgeTTS;

public class EdgeTTSSettings
{
    public int    Speed  { get; set; } = 100;
    public int    Pitch  { get; set; } = 100;
    public int    Volume { get; set; } = 100;
    public string Voice  { get; set; } = "zh-CN-YunyangNeural";

    public override string ToString() =>
        $"{nameof(Speed)}: {Speed}, {nameof(Pitch)}: {Pitch}, {nameof(Volume)}: {Volume}, {nameof(Voice)}: {Voice}";
}

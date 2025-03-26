namespace EdgeTTS;

public class EdgeTTSSettings
{
    public int    AudioDeviceId { get; set; } = 0;
    public int    Speed         { get; set; } = 100;
    public int    Pitch         { get; set; } = 100;
    public int    Volume        { get; set; } = 100;
    public string Voice         { get; set; } = "zh-CN-YunyangNeural";

    public Dictionary<string, string> PhonemeReplacements { get; set; } = new()
    {
        ["欧米茄"] = "欧米加",
        ["歐米茄"] = "歐米加",
    };

    public override string ToString() => 
        $"{nameof(AudioDeviceId)}: {AudioDeviceId}, {nameof(Speed)}: {Speed}, {nameof(Pitch)}: {Pitch}, {nameof(Volume)}: {Volume}, {nameof(Voice)}: {Voice}";
}

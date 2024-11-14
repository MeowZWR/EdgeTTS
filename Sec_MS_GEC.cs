using System.Security.Cryptography;
using System.Text;

namespace EdgeTTS;

public static class Sec_MS_GEC
{
    private static string? _sec_ms_gec;
    private static long _ticks;

    public static void Update()
    {
        var ticks = DateTime.Now.ToFileTimeUtc();
        var str = $"{ticks - (ticks % 3_000_000_000)}6A5AA1D4EAFF4E9FB37E23D68491D6F4";
        var by = SHA256.HashData(Encoding.UTF8.GetBytes(str));
        _sec_ms_gec = BitConverter.ToString(by).Replace("-", "").ToUpper();
        _ticks = ticks;
    }

    public static string? Get()
    {
        if (DateTime.Now.ToFileTimeUtc() >= _ticks + 3_000_000_000) Update();
        return _sec_ms_gec;
    }
}

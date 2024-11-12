using System.Numerics;
using System.Text;

namespace EdgeTTS;

internal static class Utils
{
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    public static string ToBase36String(this byte[] toConvert, bool bigEndian = false)
    {
        if (bigEndian) Array.Reverse(toConvert);
        var dividend = new BigInteger(toConvert);
        var builder = new StringBuilder();
        while (dividend != 0)
        {
            dividend = BigInteger.DivRem(dividend, 36, out var remainder);
            builder.Insert(0, Alphabet[Math.Abs((int)remainder)]);
        }

        return builder.ToString();
    }

    public static T Clamp<T>(this T val, T min, T max) where T : IComparable<T>
    {
        if (val.CompareTo(min) < 0) return min;
        if (val.CompareTo(max) > 0) return max;
        return val;
    }
}

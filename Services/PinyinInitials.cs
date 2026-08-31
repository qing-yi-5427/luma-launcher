using System.Text;

namespace LumaLauncher.Services;

internal static class PinyinInitials
{
    private static readonly int[] Boundaries =
    [
        -20319, -20284, -19776, -19219, -18711, -18527, -18240, -17923,
        -17418, -16475, -16213, -15641, -15166, -14923, -14915, -14631,
        -14150, -14091, -13319, -12839, -12557, -11848, -11056, -10247
    ];

    private const string Letters = "ABCDEFGHJKLMNOPQRSTWXYZ";
    private static readonly Encoding Gbk;

    static PinyinInitials()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Gbk = Encoding.GetEncoding(936);
    }

    internal static string Get(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (character <= 127)
            {
                if (char.IsLetterOrDigit(character))
                    builder.Append(char.ToLowerInvariant(character));
                continue;
            }

            var bytes = Gbk.GetBytes(character.ToString());
            if (bytes.Length < 2)
                continue;
            var code = bytes[0] * 256 + bytes[1] - 65536;
            for (var index = Letters.Length - 1; index >= 0; index--)
            {
                if (code < Boundaries[index])
                    continue;
                builder.Append(char.ToLowerInvariant(Letters[index]));
                break;
            }
        }
        return builder.ToString();
    }
}

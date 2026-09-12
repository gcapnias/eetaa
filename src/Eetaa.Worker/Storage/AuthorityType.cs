using System.Globalization;

namespace Eetaa.Worker.Storage;

public enum AuthorityType
{
    Municipality, // 'Δ' (Δήμος)
    Community     // 'Κ' (Κοινότητα)
}

public static class AuthorityTypeExtensions
{
    public static string ToDbCode(this AuthorityType type) => type switch
    {
        AuthorityType.Municipality => "Δ",
        AuthorityType.Community => "Κ",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown AuthorityType")
    };

    private static readonly CultureInfo GreekCulture = new("el-GR");

    public static AuthorityType? FromCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var trimmed = code.Trim();
        if (trimmed.Equals("Δ", StringComparison.OrdinalIgnoreCase) ||
            GreekCulture.CompareInfo.IsPrefix(trimmed, "ΔΗΜ", CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace))
        {
            return AuthorityType.Municipality;
        }

        if (trimmed.Equals("Κ", StringComparison.OrdinalIgnoreCase) ||
            GreekCulture.CompareInfo.IsPrefix(trimmed, "ΚΟΙΝ", CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace))
        {
            return AuthorityType.Community;
        }

        return null;
    }
}

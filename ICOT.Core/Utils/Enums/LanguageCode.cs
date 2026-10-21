using System.Linq;

namespace HPK.Core.Utils.Enums;

public static class LanguageCode
{
    public const string Fa = "fa";
    public const string En = "en";
    public const string Ar = "ar";
    public const string Tr = "tr";

    public static readonly string[] All = { Fa, En, Ar, Tr };

    public static bool IsValid(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        return All.Contains(code.Trim().ToLowerInvariant());
    }
}

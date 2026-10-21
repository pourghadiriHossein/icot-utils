namespace HPK.Core.Utils.Enums;

public enum PlatformStatus
{
    Active,
    Inactive
}

public static class PlatformStatusExtensions
{
    public static string ToLowerString(this PlatformStatus status) => status switch
    {
        PlatformStatus.Active => "active",
        PlatformStatus.Inactive => "inactive",
        _ => status.ToString().ToLowerInvariant()
    };
}

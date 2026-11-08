namespace ICOT.Core.Utils.Enums;

public enum PlatformHttpMethod
{
    Get,
    Post,
    Put,
    Delete,
    Patch,
    Options,
    Head,
    Ws,
    WS = Ws
}

public static class PlatformHttpMethodExtensions
{
    public static string ToLowerString(this PlatformHttpMethod method) => method switch
    {
        PlatformHttpMethod.Get => "get",
        PlatformHttpMethod.Post => "post",
        PlatformHttpMethod.Put => "put",
        PlatformHttpMethod.Delete => "delete",
        PlatformHttpMethod.Patch => "patch",
        PlatformHttpMethod.Options => "options",
        PlatformHttpMethod.Head => "head",
        PlatformHttpMethod.Ws => "ws",
        _ => method.ToString().ToLowerInvariant()
    };
}

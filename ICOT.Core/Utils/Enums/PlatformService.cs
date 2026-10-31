namespace ICOT.Core.Utils.Enums;

public enum PlatformService
{
    Workspace,
    Action,
    Plan,
    Customer,
    Tour,
    Idp,
    Studio,
    Messenger,
    Media,
    Policy,
    Domain,
    Recommender,
    ApiGateway,
    Ai
}

public static class PlatformServiceExtensions
{
    public static string ToLowerString(this PlatformService service) => service switch
    {
        PlatformService.ApiGateway => "gateway",
        PlatformService.Ai => "ai",
        _ => service.ToString().ToLowerInvariant()
    };
}

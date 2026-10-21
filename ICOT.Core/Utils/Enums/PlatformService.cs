namespace HPK.Core.Utils.Enums;

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
    ApiGateway
}

public static class PlatformServiceExtensions
{
    public static string ToLowerString(this PlatformService service) => service switch
    {
        PlatformService.ApiGateway => "gateway",
        _ => service.ToString().ToLowerInvariant()
    };
}

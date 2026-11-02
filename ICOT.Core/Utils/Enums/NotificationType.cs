namespace ICOT.Core.Utils.Enums;

public enum NotificationType
{
    info,
    warning,
    danger,
    success
}

public static class NotificationTypeExtensions
{
    public static string ToLowerString(this NotificationType type) => type switch
    {
        NotificationType.info => "info",
        NotificationType.warning => "warning",
        NotificationType.danger => "danger",
        NotificationType.success => "success",
        _ => "info"
    };
}

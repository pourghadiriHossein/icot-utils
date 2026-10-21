namespace HPK.Core.Utils.Enums;

public enum LogType
{
    Command,
    Query
}

public static class LogTypeExtensions
{
    public static string ToLowerString(this LogType logType) => logType switch
    {
        LogType.Command => "command",
        LogType.Query => "query",
        _ => "command"
    };
}

using System;
using HPK.Core.Utils.Enums;

namespace HPK.Core.Interfaces;

public interface ILoggableRequest
{
    PlatformService LogService { get; }
    string LogTable { get; }
    string LogModel { get; }
    LogType LogType { get; } // "command" or "query"
}

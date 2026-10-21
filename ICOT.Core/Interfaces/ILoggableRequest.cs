using System;
using ICOT.Core.Utils.Enums;

namespace ICOT.Core.Interfaces;

public interface ILoggableRequest
{
    PlatformService LogService { get; }
    string LogTable { get; }
    string LogModel { get; }
    LogType LogType { get; } // "command" or "query"
}

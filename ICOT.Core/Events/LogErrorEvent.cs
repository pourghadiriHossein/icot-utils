using System;
using System.Collections.Generic;

namespace ICOT.Core.Events;

public class LogErrorEvent
{
    public string Event { get; set; } = "log:error";
    public int Priority { get; set; } = 1;
    public LogErrorEventData Data { get; set; } = new();
}

public class LogErrorEventData
{
    public string? Service { get; set; }
    public string? Message { get; set; }
    public Dictionary<string, object>? Meta { get; set; }
}

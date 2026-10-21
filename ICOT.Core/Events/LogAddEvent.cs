using System;
using System.Collections.Generic;

namespace HPK.Core.Events;

public class LogAddEvent
{
    public string Event { get; set; } = "log:add";
    public int Priority { get; set; } = 1;
    public LogAddEventData Data { get; set; } = new();
}

public class LogAddEventData
{
    public Guid? RequestId { get; set; }
    public Guid? QueueId { get; set; }
    public Guid? WorkspaceId { get; set; }
    public Guid? AccountId { get; set; }
    public Guid? TraceId { get; set; }
    public string? Service { get; set; }
    public string? Table { get; set; }
    public string? Model { get; set; }
    public string Type { get; set; } = "command"; // "command" or "query"
    public Dictionary<string, object>? Meta { get; set; }
}

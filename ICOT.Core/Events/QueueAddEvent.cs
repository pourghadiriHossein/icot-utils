using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ICOT.Core.Events;

public class QueueAddEvent
{
    [JsonPropertyName("event")]
    public string Event { get; set; } = "queue:add";

    [JsonPropertyName("priority")]
    public int Priority { get; set; } = 2;

    [JsonPropertyName("data")]
    public QueueAddEventData Data { get; set; } = new();

    public QueueAddEvent() { }

    public QueueAddEvent(QueueAddEventData data, int priority = 2)
    {
        Priority = priority;
        Data = data;
    }
}

public class QueueAddEventData
{
    [JsonPropertyName("queueId")]
    public Guid? QueueId { get; set; }

    [JsonPropertyName("workspaceId")]
    public Guid? WorkspaceId { get; set; }

    [JsonPropertyName("accountId")]
    public Guid? AccountId { get; set; }

    [JsonPropertyName("service")]
    public string? Service { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("request")]
    public object? Request { get; set; }

    [JsonPropertyName("meta")]
    public Dictionary<string, object>? Meta { get; set; }
}

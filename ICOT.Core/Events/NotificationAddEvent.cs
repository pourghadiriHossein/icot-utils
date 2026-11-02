using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ICOT.Core.Events;

public class NotificationAddEvent
{
    [JsonPropertyName("event")]
    public string Event { get; set; } = "notification:add";

    [JsonPropertyName("priority")]
    public int Priority { get; set; } = 1;

    [JsonPropertyName("data")]
    public NotificationAddEventData Data { get; set; } = new();

    public NotificationAddEvent() { }

    public NotificationAddEvent(NotificationAddEventData data, int priority = 1)
    {
        Priority = priority;
        Data = data;
    }
}

public class NotificationAddEventData
{
    [JsonPropertyName("workspace_id")]
    public Guid? WorkspaceId { get; set; }

    [JsonPropertyName("account_id")]
    public Guid? AccountId { get; set; }

    [JsonPropertyName("service")]
    public string? Service { get; set; }

    [JsonPropertyName("table")]
    public string? Table { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; } // "info", "warning", "danger", "success"

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("meta")]
    public Dictionary<string, object>? Meta { get; set; }
}

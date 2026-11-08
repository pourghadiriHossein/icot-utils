using System;
using System.Text.Json.Serialization;

namespace ICOT.Core.Events;

public class WebSocketSendEvent
{
    [JsonPropertyName("event")]
    public string Event { get; set; } = "ws:send";

    [JsonPropertyName("priority")]
    public int Priority { get; set; } = 1;

    [JsonPropertyName("data")]
    public WebSocketSendEventData Data { get; set; } = new();

    public WebSocketSendEvent() { }

    public WebSocketSendEvent(WebSocketSendEventData data, int priority = 1)
    {
        Priority = priority;
        Data = data;
    }
}

public class WebSocketSendEventData
{
    [JsonPropertyName("workspaceId")]
    public Guid WorkspaceId { get; set; }

    [JsonPropertyName("scope")]
    public string Scope { get; set; } = "private"; // "private" or "public"

    [JsonPropertyName("accountId")]
    public Guid? AccountId { get; set; }

    [JsonPropertyName("signature")]
    public string? Signature { get; set; }

    [JsonPropertyName("message")]
    public object? Message { get; set; }
}

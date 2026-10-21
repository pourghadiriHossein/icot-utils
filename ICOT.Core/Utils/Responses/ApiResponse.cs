using System.Text.Json.Serialization;

namespace HPK.Core.Utils.Responses;

/// <summary>
/// A standardized wrapper for all API responses, ensuring uniform JSON output across all microservices.
/// </summary>
/// <typeparam name="T">Type of the data payload.</typeparam>
public class ApiResponse<T>
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("data")]
    public T? Data { get; set; }

    [JsonPropertyName("meta")]
    public object? Meta { get; set; }

    [JsonPropertyName("exception")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Exception { get; set; }

    public ApiResponse()
    {
    }

    public ApiResponse(bool success, int code, string? message, T? data, object? meta = null, object? exception = null)
    {
        Success = success;
        Code = code;
        Message = message;
        Data = data;
        Meta = meta;
        Exception = exception;
    }

    // Static helper methods for quick building
    public static ApiResponse<T> Ok(T data, string? message = "Operation completed successfully", object? meta = null)
    {
        return new ApiResponse<T>(true, 200, message, data, meta);
    }

    public static ApiResponse<T> Error(int code, string message, object? exception = null)
    {
        return new ApiResponse<T>(false, code, message, default, null, exception);
    }
}

// A non-generic version when there is no specific data payload to return
public class ApiResponse : ApiResponse<object>
{
    public ApiResponse() : base()
    {
    }

    public ApiResponse(bool success, int code, string? message, object? data, object? meta = null, object? exception = null) 
        : base(success, code, message, data, meta, exception)
    {
    }

    public static ApiResponse Ok(string? message = "Operation completed successfully")
    {
        return new ApiResponse(true, 200, message, null);
    }

    public static new ApiResponse Error(int code, string message, object? exception = null)
    {
        return new ApiResponse(false, code, message, null, null, exception);
    }
}

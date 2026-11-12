using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using ICOT.Core.Utils.Responses;

namespace ICOT.Core.Web.Helpers;

/// <summary>
/// Helper for extracting precise, field-level validation errors from ASP.NET Core ModelState.
/// Normalizes keys to camelCase, extracts JsonException paths and messages, and eliminates generic "The request field is required." fallbacks.
/// </summary>
public static class ModelStateHelper
{
    public static ApiResponse BuildValidationResponse(ActionContext context)
    {
        var errorDict = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (rawKey, entry) in context.ModelState)
        {
            if (entry == null || entry.Errors.Count == 0) continue;

            foreach (var error in entry.Errors)
            {
                string targetField = NormalizeKey(rawKey);
                string message = error.ErrorMessage;

                // Check if there is an underlying JsonException with path details
                if (error.Exception is JsonException jsonEx)
                {
                    if (!string.IsNullOrEmpty(jsonEx.Path) && jsonEx.Path != "$")
                    {
                        targetField = NormalizeKey(jsonEx.Path);
                    }

                    if (string.IsNullOrWhiteSpace(message))
                    {
                        message = FormatJsonExceptionMessage(jsonEx, targetField);
                    }
                }
                else if (error.Exception != null && string.IsNullOrWhiteSpace(message))
                {
                    message = error.Exception.Message;
                }

                // If message is the generic ASP.NET Core "The request field is required."
                if (message.Equals("The request field is required.", StringComparison.OrdinalIgnoreCase) ||
                    message.Equals("The input was not valid.", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrEmpty(targetField) || targetField == "request" || targetField == "command")
                    {
                        targetField = "body";
                        message = "The request body is required and must be valid JSON.";
                    }
                    else
                    {
                        message = $"The {targetField} field is required.";
                    }
                }

                if (string.IsNullOrEmpty(targetField))
                {
                    targetField = "body";
                }

                if (!errorDict.TryGetValue(targetField, out var list))
                {
                    list = new List<string>();
                    errorDict[targetField] = list;
                }

                if (!list.Contains(message))
                {
                    list.Add(message);
                }
            }
        }

        // If errorDict has "body" alongside specific fields, remove "body" because specific fields are more informative
        if (errorDict.Count > 1 && errorDict.ContainsKey("body"))
        {
            errorDict.Remove("body");
        }

        var resultDict = errorDict.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.ToArray()
        );

        return ApiResponse.ValidationError(resultDict, "One or more validation errors occurred.");
    }

    private static string NormalizeKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return string.Empty;

        var trimmed = key.Trim();
        if (trimmed.StartsWith("$.")) trimmed = trimmed.Substring(2);
        else if (trimmed.StartsWith("$")) trimmed = trimmed.Substring(1);

        if (trimmed.StartsWith("request.", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed.Substring(8);
        else if (trimmed.StartsWith("command.", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed.Substring(8);
        else if (trimmed.StartsWith("payload.", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed.Substring(8);

        var parts = trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return string.Empty;

        return string.Join(".", parts.Select(ToCamelCaseSegment));
    }

    private static string ToCamelCaseSegment(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var bracketIndex = s.IndexOf('[');
        if (bracketIndex > 0)
        {
            var prop = s.Substring(0, bracketIndex);
            var rest = s.Substring(bracketIndex);
            return ToCamelCase(prop) + rest;
        }
        return ToCamelCase(s);
    }

    private static string ToCamelCase(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        if (s.Length == 1) return s.ToLowerInvariant();
        return char.ToLowerInvariant(s[0]) + s.Substring(1);
    }

    private static string FormatJsonExceptionMessage(JsonException jsonEx, string fieldName)
    {
        var msg = jsonEx.Message;
        var pathIndex = msg.IndexOf("Path:", StringComparison.OrdinalIgnoreCase);
        if (pathIndex > 0)
        {
            msg = msg.Substring(0, pathIndex).Trim().TrimEnd('.');
        }

        if (!string.IsNullOrEmpty(fieldName) && fieldName != "body")
        {
            return $"Invalid value for '{fieldName}': {msg}.";
        }
        return msg;
    }
}

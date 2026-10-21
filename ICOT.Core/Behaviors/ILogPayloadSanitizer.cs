using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http;

namespace ICOT.Core.Behaviors;

/// <summary>
/// Abstraction for sanitizing requests and responses before logging into queues or storage (SRP).
/// Prevents serialization of streams, files, circular references, and queryables.
/// </summary>
public interface ILogPayloadSanitizer
{
    object? Sanitize(object? obj);
}

/// <summary>
/// Default JSON-based implementation of <see cref="ILogPayloadSanitizer"/>.
/// </summary>
public class DefaultLogPayloadSanitizer : ILogPayloadSanitizer
{
    private static readonly JsonSerializerOptions _logJsonOptions = CreateLogJsonOptions();

    public static DefaultLogPayloadSanitizer Instance { get; } = new();

    public object? Sanitize(object? obj)
    {
        if (obj == null) return null;

        var type = obj.GetType();
        if (IsFileOrStreamType(type) ||
            type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IQueryable<>)))
        {
            return "[File/Stream/Queryable Omitted]";
        }

        try
        {
            return JsonSerializer.SerializeToElement(obj, _logJsonOptions);
        }
        catch
        {
            return "[Unserializable Object]";
        }
    }

    private static bool IsFileOrStreamType(Type type)
    {
        if (typeof(Stream).IsAssignableFrom(type) ||
            typeof(IFormFile).IsAssignableFrom(type) ||
            typeof(IFormFileCollection).IsAssignableFrom(type))
        {
            return true;
        }

        if (typeof(System.Collections.IEnumerable).IsAssignableFrom(type) && type.IsGenericType)
        {
            var itemType = type.GetGenericArguments().FirstOrDefault();
            if (itemType != null && (typeof(IFormFile).IsAssignableFrom(itemType) || typeof(Stream).IsAssignableFrom(itemType)))
            {
                return true;
            }
        }

        return false;
    }

    private static JsonSerializerOptions CreateLogJsonOptions()
    {
        return new JsonSerializerOptions
        {
            ReferenceHandler = ReferenceHandler.IgnoreCycles,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver
            {
                Modifiers =
                {
                    typeInfo =>
                    {
                        foreach (var prop in typeInfo.Properties)
                        {
                            if (IsFileOrStreamType(prop.PropertyType))
                            {
                                prop.ShouldSerialize = (_, _) => false;
                            }
                        }
                    }
                }
            }
        };
    }
}

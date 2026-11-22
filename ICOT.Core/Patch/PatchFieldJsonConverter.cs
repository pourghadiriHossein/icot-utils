using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace ICOT.Core.Patch;

/// <summary>
/// Creates <see cref="PatchFieldJsonConverter{T}"/> instances for every <see cref="PatchField{T}"/> closed type.
/// Register once globally (done automatically by <c>AddPlatformApiStandard</c>).
/// </summary>
public sealed class PatchFieldJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(PatchField<>);

    public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var valueType = typeToConvert.GetGenericArguments()[0];
        var converterType = typeof(PatchFieldJsonConverter<>).MakeGenericType(valueType);
        return (JsonConverter?)Activator.CreateInstance(converterType);
    }
}

/// <summary>
/// Reads a JSON value into a <see cref="PatchField{T}"/>:
/// explicit <c>null</c> becomes <see cref="PatchFieldState.Null"/> (even for value types such as Guid/int/bool/enum,
/// so no JsonException is raised and the 422 decision is left to FluentValidation),
/// any other token is deserialized as <typeparamref name="T"/> and becomes <see cref="PatchFieldState.Value"/>.
/// Keys missing from the JSON never reach this converter and therefore stay <see cref="PatchFieldState.Unspecified"/>.
/// </summary>
public sealed class PatchFieldJsonConverter<T> : JsonConverter<PatchField<T>>
{
    public override bool HandleNull => true;

    public override PatchField<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return PatchField<T>.Null;
        }

        var value = JsonSerializer.Deserialize<T>(ref reader, options);
        return PatchField<T>.From(value);
    }

    public override void Write(Utf8JsonWriter writer, PatchField<T> value, JsonSerializerOptions options)
    {
        if (value.HasValue)
        {
            JsonSerializer.Serialize(writer, value.Value, options);
            return;
        }

        // Null and Unspecified are both written as null when the property is not skipped
        // (use PatchFieldJsonModifiers.SkipUnspecified to omit Unspecified properties entirely).
        writer.WriteNullValue();
    }
}

/// <summary>
/// <see cref="JsonTypeInfo"/> modifiers for <see cref="PatchField{T}"/>.
/// </summary>
public static class PatchFieldJsonModifiers
{
    /// <summary>
    /// Omits properties of type <see cref="PatchField{T}"/> from serialized output when they are
    /// <see cref="PatchFieldState.Unspecified"/> (used for request logging so only keys that were really sent are recorded).
    /// </summary>
    public static void SkipUnspecified(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object)
        {
            return;
        }

        foreach (var property in typeInfo.Properties)
        {
            var propertyType = property.PropertyType;
            if (propertyType.IsGenericType && propertyType.GetGenericTypeDefinition() == typeof(PatchField<>))
            {
                var previous = property.ShouldSerialize;
                property.ShouldSerialize = (obj, value) =>
                    (previous == null || previous(obj, value)) &&
                    value is IPatchField patchField &&
                    patchField.IsSpecified;
            }
        }
    }
}

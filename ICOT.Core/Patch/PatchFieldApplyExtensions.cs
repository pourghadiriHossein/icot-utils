using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using ICOT.Core.Exceptions;

namespace ICOT.Core.Patch;

/// <summary>
/// Helpers that apply a <see cref="PatchField{T}"/> to an entity property and report whether anything really changed.
/// <para>
/// Rules:
/// <list type="bullet">
/// <item>Unspecified: nothing happens (returns false).</item>
/// <item>Null: nullable columns are set to NULL when they are not already NULL; required columns throw a 422 <see cref="ValidationException"/> (safety net, normally already rejected by the validator).</item>
/// <item>Value: the column is updated only when the new value differs from the current one (structural comparison for JSONB dictionaries/collections).</item>
/// </list>
/// Handlers must skip <c>UpdatedAt</c>, <c>UpdatedBy</c> and <c>SaveChangesAsync</c> when no helper returned true.
/// </para>
/// </summary>
public static class PatchFieldApplyExtensions
{
    /// <summary>Applies to a nullable reference-type column (string?, Dictionary&lt;,&gt;?, List&lt;&gt;?, ...).</summary>
    public static bool ApplyNullable<T>(this PatchField<T> field, T? current, Action<T?> setter) where T : class
    {
        switch (field.State)
        {
            case PatchFieldState.Null:
                if (current is null) return false;
                setter(null);
                return true;

            case PatchFieldState.Value:
                if (PatchValueComparer.AreEqual(current, field.Value)) return false;
                setter(field.Value);
                return true;

            default:
                return false;
        }
    }

    /// <summary>Applies to a nullable value-type column (Guid?, int?, decimal?, bool?, DateTime?, enum?, ...).</summary>
    public static bool ApplyNullableValue<T>(this PatchField<T> field, T? current, Action<T?> setter) where T : struct
    {
        switch (field.State)
        {
            case PatchFieldState.Null:
                if (!current.HasValue) return false;
                setter(null);
                return true;

            case PatchFieldState.Value:
                if (current.HasValue && PatchValueComparer.AreEqual(current.Value, field.Value)) return false;
                setter(field.Value);
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Applies to a NOT NULL column. An explicit null raises a 422 <see cref="ValidationException"/>
    /// keyed by <paramref name="propertyName"/>.
    /// </summary>
    public static bool ApplyRequired<T>(this PatchField<T> field, T current, Action<T> setter, string propertyName)
    {
        switch (field.State)
        {
            case PatchFieldState.Null:
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    [ToCamelCase(propertyName)] = new[] { $"{propertyName} cannot be null." }
                });

            case PatchFieldState.Value:
                if (PatchValueComparer.AreEqual(current, field.Value)) return false;
                setter(field.Value);
                return true;

            default:
                return false;
        }
    }

    private static string ToCamelCase(string value) =>
        string.IsNullOrEmpty(value) ? value : char.ToLowerInvariant(value[0]) + value[1..];
}

/// <summary>
/// Equality used to decide if a patch really changes a column.
/// Scalars use <see cref="EqualityComparer{T}.Default"/>; collections/dictionaries (JSONB) are compared structurally.
/// </summary>
public static class PatchValueComparer
{
    public static bool AreEqual<T>(T? current, T? incoming)
    {
        if (current is null && incoming is null) return true;
        if (current is null || incoming is null) return false;

        if (current is string || current.GetType().IsValueType)
        {
            return EqualityComparer<T>.Default.Equals(current, incoming);
        }

        if (current is IEnumerable || incoming is IEnumerable)
        {
            try
            {
                var left = JsonSerializer.SerializeToNode(current, typeof(T));
                var right = JsonSerializer.SerializeToNode(incoming, typeof(T));
                return JsonNode.DeepEquals(left, right);
            }
            catch (NotSupportedException)
            {
                return false;
            }
        }

        return EqualityComparer<T>.Default.Equals(current, incoming);
    }
}

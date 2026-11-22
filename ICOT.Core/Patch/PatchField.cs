using System;
using System.Collections.Generic;

namespace ICOT.Core.Patch;

/// <summary>
/// The three states a JSON field can have in a partial update (PUT/PATCH) request body.
/// </summary>
public enum PatchFieldState : byte
{
    /// <summary>The key was not present in the JSON body. Nothing must change.</summary>
    Unspecified = 0,

    /// <summary>The key was present with an explicit <c>null</c> value. The column must be set to NULL (or rejected with 422 if not nullable).</summary>
    Null = 1,

    /// <summary>The key was present with a non-null value. The column must be updated if the value differs.</summary>
    Value = 2
}

/// <summary>
/// Non-generic view of <see cref="PatchField{T}"/> used by serializers and reflection based helpers.
/// </summary>
public interface IPatchField
{
    PatchFieldState State { get; }
    bool IsSpecified { get; }
}

/// <summary>
/// Tri-state wrapper used on Update DTOs to distinguish between
/// "key not sent" (<see cref="PatchFieldState.Unspecified"/>),
/// "key sent as null" (<see cref="PatchFieldState.Null"/>) and
/// "key sent with a value" (<see cref="PatchFieldState.Value"/>).
/// <para>
/// <typeparamref name="T"/> is always the underlying NON-nullable type (e.g. <c>PatchField&lt;Guid&gt;</c> for a
/// <c>Guid?</c> column, <c>PatchField&lt;int&gt;</c> for an <c>int?</c> column, <c>PatchField&lt;string&gt;</c> for a <c>string?</c> column).
/// </para>
/// <para>
/// <c>default(PatchField&lt;T&gt;)</c> is <see cref="PatchFieldState.Unspecified"/>, so a DTO property that is
/// absent from the JSON body is automatically "unspecified" (System.Text.Json never calls the converter for missing keys).
/// </para>
/// </summary>
public readonly struct PatchField<T> : IPatchField, IEquatable<PatchField<T>>
{
    private readonly T? _value;

    private PatchField(PatchFieldState state, T? value)
    {
        State = state;
        _value = value;
    }

    /// <summary>State of the field.</summary>
    public PatchFieldState State { get; }

    /// <summary>True when the key was present in the JSON body (either null or a value).</summary>
    public bool IsSpecified => State != PatchFieldState.Unspecified;

    /// <summary>True when the key was present with an explicit null.</summary>
    public bool IsNull => State == PatchFieldState.Null;

    /// <summary>True when the key was present with a non-null value.</summary>
    public bool HasValue => State == PatchFieldState.Value;

    /// <summary>
    /// The supplied value. Only meaningful when <see cref="HasValue"/> is true; otherwise returns the default value
    /// (never throws, so it is safe inside guarded FluentValidation rules).
    /// </summary>
    public T Value => _value!;

    /// <summary>Key not sent.</summary>
    public static PatchField<T> Unspecified => default;

    /// <summary>Key sent as explicit null.</summary>
    public static PatchField<T> Null => new(PatchFieldState.Null, default);

    /// <summary>Key sent with the supplied value (a null reference is treated as an explicit null).</summary>
    public static PatchField<T> From(T? value) =>
        value is null ? Null : new PatchField<T>(PatchFieldState.Value, value);

    /// <summary>Returns the value when present, otherwise the supplied fallback.</summary>
    public T GetValueOrDefault(T fallback) => HasValue ? _value! : fallback;

    public static implicit operator PatchField<T>(T? value) => From(value);

    public bool Equals(PatchField<T> other) =>
        State == other.State && EqualityComparer<T?>.Default.Equals(_value, other._value);

    public override bool Equals(object? obj) => obj is PatchField<T> other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(State, _value);

    public static bool operator ==(PatchField<T> left, PatchField<T> right) => left.Equals(right);

    public static bool operator !=(PatchField<T> left, PatchField<T> right) => !left.Equals(right);

    public override string ToString() => State switch
    {
        PatchFieldState.Unspecified => "<unspecified>",
        PatchFieldState.Null => "null",
        _ => _value?.ToString() ?? "null"
    };
}

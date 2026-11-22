using System;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using FluentValidation;

namespace ICOT.Core.Patch;

/// <summary>
/// FluentValidation helpers for <see cref="PatchField{T}"/> properties.
/// <code>
/// RuleForPatch(x =&gt; x.Payload.Username)
///     .NotNullWhenSpecified("Username cannot be null.")
///     .WhenHasValue(r =&gt; r.NotEmpty().MinimumLength(4));
/// RuleForPatch(x =&gt; x.Payload.Website)
///     .WhenHasValue(r =&gt; r.Must(IsUrl).WithMessage("Website must be a valid URL."));
/// </code>
/// Failures are reported under the property name (last member of the expression, e.g. <c>Username</c>),
/// which the global ValidationBehavior converts to camelCase (<c>username</c>).
/// </summary>
public static class PatchFieldValidationExtensions
{
    public static PatchFieldRuleBuilder<T, TValue> RuleForPatch<T, TValue>(
        this AbstractValidator<T> validator,
        Expression<Func<T, PatchField<TValue>>> expression)
    {
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(expression);
        return new PatchFieldRuleBuilder<T, TValue>(validator, expression);
    }
}

/// <summary>Holder used to run value rules of a patch field on an inner validator.</summary>
public sealed class PatchValueHolder<TValue>
{
    public PatchValueHolder(TValue value) => Value = value;

    public TValue Value { get; }
}

/// <summary>
/// Fluent builder for validating one <see cref="PatchField{T}"/>.
/// Rules run only when the key was sent; value rules run only when a non-null value was sent.
/// </summary>
public sealed class PatchFieldRuleBuilder<T, TValue>
{
    private readonly AbstractValidator<T> _validator;
    private readonly Func<T, PatchField<TValue>> _getter;
    private readonly string _propertyName;

    internal PatchFieldRuleBuilder(AbstractValidator<T> validator, Expression<Func<T, PatchField<TValue>>> expression)
    {
        _validator = validator;
        _getter = expression.Compile();
        _propertyName = ResolvePropertyName(expression);
    }

    /// <summary>
    /// Use for fields that must never be NULL (business rule or NOT NULL column):
    /// when the key is sent with an explicit null, a 422 error is produced for this property.
    /// </summary>
    public PatchFieldRuleBuilder<T, TValue> NotNullWhenSpecified(string? message = null)
    {
        var name = _propertyName;
        var errorMessage = message ?? $"{name} cannot be null.";

        _validator.RuleFor(x => x).Custom((instance, context) =>
        {
            if (_getter(instance).IsNull)
            {
                context.AddFailure(name, errorMessage);
            }
        });

        return this;
    }

    /// <summary>
    /// Configures value rules (format, length, enum, ...) that run only when a non-null value was sent.
    /// Failures are attached to this property's name.
    /// </summary>
    public PatchFieldRuleBuilder<T, TValue> WhenHasValue(Action<IRuleBuilder<PatchValueHolder<TValue>, TValue>> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var inner = new InlineValidator<PatchValueHolder<TValue>>();
        configure(inner.RuleFor(holder => holder.Value));

        var name = _propertyName;
        _validator.RuleFor(x => x).CustomAsync(async (instance, context, cancellationToken) =>
        {
            var field = _getter(instance);
            if (!field.HasValue)
            {
                return;
            }

            var result = await inner.ValidateAsync(new PatchValueHolder<TValue>(field.Value), cancellationToken);
            foreach (var error in result.Errors)
            {
                context.AddFailure(name, error.ErrorMessage);
            }
        });

        return this;
    }

    private static string ResolvePropertyName(Expression<Func<T, PatchField<TValue>>> expression)
    {
        var body = expression.Body;
        while (body is UnaryExpression unary)
        {
            body = unary.Operand;
        }

        return body is MemberExpression member
            ? member.Member.Name
            : throw new ArgumentException("Expression must select a property (e.g. x => x.Payload.Username).", nameof(expression));
    }
}

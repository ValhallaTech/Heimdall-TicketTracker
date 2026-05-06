using System;
using System.ComponentModel.DataAnnotations;

namespace Heimdall.Core.Dtos;

/// <summary>
/// Validates that a <see cref="Guid"/> property is not <see cref="Guid.Empty"/>.
/// <see cref="RequiredAttribute"/> alone treats <c>Guid.Empty</c> as "present"
/// because <c>Guid</c> is a non-nullable value type — so <c>[Required]</c> on a
/// <c>Guid</c> never fails. Stack <see cref="NotEmptyGuidAttribute"/> on top to
/// reject the all-zeroes default that an unedited form binding would otherwise
/// submit and that no FK column will ever satisfy.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
public sealed class NotEmptyGuidAttribute : ValidationAttribute
{
    /// <summary>The default error message; used when no <see cref="ValidationAttribute.ErrorMessage"/> is configured.</summary>
    public const string DefaultErrorMessage = "The {0} field must not be the empty Guid.";

    /// <summary>
    /// Initializes a new instance of the <see cref="NotEmptyGuidAttribute"/> class.
    /// </summary>
    public NotEmptyGuidAttribute()
        : base(DefaultErrorMessage)
    {
    }

    /// <inheritdoc />
    public override bool IsValid(object? value)
    {
        return value switch
        {
            null => true, // null nullability is RequiredAttribute's responsibility, not ours.
            Guid g => g != Guid.Empty,
            _ => false,
        };
    }
}

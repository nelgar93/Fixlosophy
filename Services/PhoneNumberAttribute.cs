using System.ComponentModel.DataAnnotations;

namespace Fixlosophy.Services;

/// <summary>
/// Validates a phone number with the same rule the server applies everywhere else,
/// so an <c>EditForm</c> rejects exactly what <see cref="AuthService.IsValidPhone"/>
/// would. .NET's own <c>[Phone]</c> is far looser — it accepts letters — which is how
/// "call me on 07700 900000" used to get through the contact form.
///
/// An empty value passes: use <c>[Required]</c> alongside this where the field is
/// mandatory, which is how the rest of the model reads.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
public sealed class PhoneNumberAttribute : ValidationAttribute
{
    public PhoneNumberAttribute()
        : base("Please enter a phone number we can reach you on.") { }

    public override bool IsValid(object? value)
    {
        if (value is null) return true;
        var s = value as string;
        return string.IsNullOrWhiteSpace(s) || AuthService.IsValidPhone(s);
    }
}

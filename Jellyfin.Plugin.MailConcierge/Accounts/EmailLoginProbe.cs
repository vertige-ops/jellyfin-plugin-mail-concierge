using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace Jellyfin.Plugin.MailConcierge.Accounts;

/// <summary>
/// Tells whether the sign-in action of Jellyfin has the shape e-mail sign-in relies on: an action
/// <c>User/AuthenticateUserByName</c> whose request has a writable string property <c>Username</c>.
/// </summary>
public sealed class EmailLoginProbe
{
    /// <summary>The controller of the sign-in action.</summary>
    public const string ControllerName = "User";

    /// <summary>The sign-in action.</summary>
    public const string ActionName = "AuthenticateUserByName";

    /// <summary>The property of the request that carries the typed user name.</summary>
    public const string UsernameProperty = "Username";

    private readonly IActionDescriptorCollectionProvider _actions;
    private readonly Lazy<bool> _available;

    /// <summary>
    /// Initializes a new instance of the <see cref="EmailLoginProbe"/> class.
    /// </summary>
    /// <param name="actions">The actions of the server.</param>
    public EmailLoginProbe(IActionDescriptorCollectionProvider actions)
    {
        _actions = actions;
        _available = new Lazy<bool>(Compute, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// Gets a value indicating whether the sign-in action has the expected shape. Computed once, on first use; any error gives false.
    /// </summary>
    public bool IsAvailable => _available.Value;

    /// <summary>
    /// Finds the user name property on a bound argument.
    /// </summary>
    /// <param name="argument">The bound argument.</param>
    /// <returns>The public readable and writable string property <c>Username</c>, or null (also when the name is ambiguous).</returns>
    public static PropertyInfo? FindUsernameProperty(object? argument)
        => argument is null ? null : FindUsernameProperty(argument.GetType());

    private static PropertyInfo? FindUsernameProperty(Type type)
    {
        PropertyInfo? property;
        try
        {
            property = type.GetProperty(UsernameProperty, BindingFlags.Public | BindingFlags.Instance);
        }
        catch (AmbiguousMatchException)
        {
            // A type that hides Username with `new`: which property Jellyfin binds is not ours to guess.
            return null;
        }

        return property is not null
            && property.PropertyType == typeof(string)
            && property.GetGetMethod() is not null
            && property.GetSetMethod() is not null
            ? property
            : null;
    }

    private bool Compute()
    {
        try
        {
            return _actions.ActionDescriptors.Items
                .OfType<ControllerActionDescriptor>()
                .Where(a => string.Equals(a.ControllerName, ControllerName, StringComparison.Ordinal)
                    && string.Equals(a.ActionName, ActionName, StringComparison.Ordinal))
                .SelectMany(a => a.Parameters)
                .Any(p => p.ParameterType is not null && FindUsernameProperty(p.ParameterType) is not null);
        }
        catch (Exception)
        {
            return false;
        }
    }
}

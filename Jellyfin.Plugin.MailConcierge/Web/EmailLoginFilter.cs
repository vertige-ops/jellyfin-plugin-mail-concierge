using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MailConcierge.Accounts;
using Jellyfin.Plugin.MailConcierge.Configuration;
using Jellyfin.Plugin.MailConcierge.Security;
using MediaBrowser.Controller.Authentication;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MailConcierge.Web;

/// <summary>
/// Lets a user type the confirmed e-mail address of the account in place of its user name at sign-in: the address is swapped
/// for the user name and Jellyfin does everything else. A failed sign-in with an address never tells a known address from an
/// unknown one. The password is never read, and nothing is counted here: Jellyfin keeps its own failure counter.
/// </summary>
public sealed class EmailLoginFilter : IAsyncActionFilter
{
    /// <summary>The message of Jellyfin for an unknown user, given for every failed sign-in translated from an address.</summary>
    public const string GenericMessage = "Invalid username or password entered.";

    /// <summary>The shortest time before a failed sign-in with an address is answered.</summary>
    public static readonly TimeSpan FailureFloor = TimeSpan.FromMilliseconds(700);

    /// <summary>
    /// The longest time a sign-in with an address waits for its account while another attempt on the same account is under
    /// way; past it, the attempt goes on untranslated. It must stay well under <see cref="FailureFloor"/> minus one password check,
    /// so that a failure after the wait is still answered at the floor.
    /// </summary>
    public static readonly TimeSpan LockWait = TimeSpan.FromMilliseconds(175);

    // One error line per exception type per hour, for at most MaxLoggedTypes types: the filter is transient, so the map is static.
    private const int MaxLoggedTypes = 32;
    private static readonly TimeSpan FailureLogInterval = TimeSpan.FromHours(1);
    private static readonly Lock FailureLogSync = new();
    private static readonly Dictionary<string, DateTimeOffset> FailureLines = new(StringComparer.Ordinal);

    private readonly IServiceProvider _services;
    private readonly ILogger<EmailLoginFilter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="EmailLoginFilter"/> class.
    /// </summary>
    /// <remarks>
    /// Only the service provider is injected: this is a global MVC filter, so it is built for every API call, and the
    /// plugin services throw while the plugin is not initialized. They are resolved lazily, for the sign-in action only.
    /// </remarks>
    /// <param name="services">The service provider.</param>
    /// <param name="logger">The logger.</param>
    public EmailLoginFilter(IServiceProvider services, ILogger<EmailLoginFilter> logger)
    {
        _services = services;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var action = context.ActionDescriptor as ControllerActionDescriptor;
        if (action is null
            || !string.Equals(action.ControllerName, EmailLoginProbe.ControllerName, StringComparison.Ordinal)
            || !string.Equals(action.ActionName, EmailLoginProbe.ActionName, StringComparison.Ordinal))
        {
            await next().ConfigureAwait(false);
            return;
        }

        var attempt = new Attempt();
        try
        {
            await PrepareAsync(context, attempt).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Sign-in is never broken by the plugin: the request goes on as it came.
            attempt.Undo();
            LogFailure(ex, attempt.UserId);
        }

        // The account is held for the eligibility read and, when translated, the native call; never during the floor.
        ActionExecutedContext executed;
        try
        {
            executed = await next().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            attempt.Release();
            if (attempt.Time is null || IsAborted(ex, context))
            {
                throw;
            }

            await WaitForTheFloorAsync(attempt).ConfigureAwait(false);
            if (attempt.Translated && IsReplaced(ex))
            {
                throw new AuthenticationException(GenericMessage);
            }

            throw;
        }

        attempt.Release();
        if (attempt.Time is null || executed is null || Succeeded(executed) || IsAborted(executed.Exception, context))
        {
            return;
        }

        await WaitForTheFloorAsync(attempt).ConfigureAwait(false);
        if (attempt.Translated && !executed.ExceptionHandled && executed.Exception is { } failure && IsReplaced(failure))
        {
            // Same exception as for an unknown user: no account state, no client address, no user name.
            executed.Exception = new AuthenticationException(GenericMessage);
        }
    }

    /// <summary>Forgets which error lines were logged, so that tests do not depend on their order.</summary>
    internal static void ResetFailureLog()
    {
        lock (FailureLogSync)
        {
            FailureLines.Clear();
        }
    }

    /// <summary>A sign-in succeeded when it ended without an unhandled exception and without an error status.</summary>
    private static bool Succeeded(ActionExecutedContext executed)
        => (executed.Exception is null || executed.ExceptionHandled)
            && executed.Result is not IStatusCodeActionResult { StatusCode: >= 400 };

    /// <summary>The client went away: nobody is listening, so the answer is neither held back nor changed.</summary>
    private static bool IsAborted(Exception? ex, ActionExecutingContext context)
        => ex is OperationCanceledException && context.HttpContext.RequestAborted.IsCancellationRequested;

    /// <summary>
    /// Tells whether the failure of a translated attempt is replaced by the generic exception. An <see cref="ArgumentException"/>
    /// is kept: Jellyfin throws it before it looks at the user name, the same way for a known and an unknown address.
    /// </summary>
    private static bool IsReplaced(Exception ex) => ex is not ArgumentException;

    /// <summary>Waits until <see cref="FailureFloor"/> has elapsed since the input was found to contain <c>@</c>.</summary>
    private static async Task WaitForTheFloorAsync(Attempt attempt)
    {
        var time = attempt.Time!;
        var remaining = FailureFloor - time.GetElapsedTime(attempt.Started);
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, time).ConfigureAwait(false);
        }
    }

    private static (object Argument, PropertyInfo Property)? FindUsername(ActionExecutingContext context)
    {
        foreach (var argument in context.ActionArguments.Values)
        {
            if (EmailLoginProbe.FindUsernameProperty(argument) is { } property)
            {
                return (argument!, property);
            }
        }

        return null;
    }

    private async Task PrepareAsync(ActionExecutingContext context, Attempt attempt)
    {
        var config = _services.GetRequiredService<Func<PluginConfiguration>>();
        if (!config().EmailLogin
            || !_services.GetRequiredService<ServiceGate>().IsOpen
            || !_services.GetRequiredService<EmailLoginProbe>().IsAvailable)
        {
            return;
        }

        if (FindUsername(context) is not { } found
            || found.Property.GetValue(found.Argument) is not string entered
            || !entered.Contains('@', StringComparison.Ordinal))
        {
            return;
        }

        // From here the input is shaped like an address: every failure waits for the floor.
        var time = _services.GetRequiredService<TimeProvider>();
        attempt.Time = time;
        attempt.Started = time.GetTimestamp();

        var resolver = _services.GetRequiredService<EmailLoginResolver>();
        if (await resolver.FindAccountAsync(entered).ConfigureAwait(false) is not { } userId)
        {
            return;
        }

        attempt.UserId = userId;

        // Checked before the account is held: an attempt that will not be translated never waits and never makes others wait.
        if (await resolver.UsernameIfAllowedAsync(userId).ConfigureAwait(false) is null)
        {
            return;
        }

        attempt.Lock = await TryLockAsync(resolver, userId, time, context.HttpContext.RequestAborted).ConfigureAwait(false);
        if (attempt.Lock is null)
        {
            return;
        }

        // The address must still be the confirmed address of this account once the account is held.
        if (await resolver.FindAccountAsync(entered).ConfigureAwait(false) != userId
            || await resolver.UsernameIfAllowedAsync(userId).ConfigureAwait(false) is not { } username)
        {
            // Not translated: the account is not held during the native call.
            attempt.Release();
            return;
        }

        attempt.Restore = () => found.Property.SetValue(found.Argument, entered);
        attempt.Translated = true;
        found.Property.SetValue(found.Argument, username);
    }

    /// <summary>
    /// Waits for the account at most <see cref="LockWait"/>, and no longer than the client does.
    /// </summary>
    /// <returns>The handle of the account, or null when the wait ended first (expected under load, not an error).</returns>
    private static async Task<IDisposable?> TryLockAsync(EmailLoginResolver resolver, Guid userId, TimeProvider time, CancellationToken aborted)
    {
        using var limit = new CancellationTokenSource(LockWait, time);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(aborted, limit.Token);
        try
        {
            return await resolver.LockAccountAsync(userId, wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (wait.IsCancellationRequested)
        {
            return null;
        }
    }

    private void LogFailure(Exception ex, Guid? userId)
    {
        if (!FirstLineThisHour(ex.GetType().FullName ?? ex.GetType().Name, (_services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow()))
        {
            return;
        }

        // The exception itself is not logged: its message could carry the typed address.
        if (userId is { } id)
        {
            _logger.LogError("E-mail sign-in failed with {ExceptionType} for account {UserId}; the request goes on unchanged", ex.GetType().FullName, id);
        }
        else
        {
            _logger.LogError("E-mail sign-in failed with {ExceptionType}; the request goes on unchanged", ex.GetType().FullName);
        }
    }

    /// <summary>Tells whether an error line for this exception type is due, and records it when it is.</summary>
    private static bool FirstLineThisHour(string type, DateTimeOffset now)
    {
        lock (FailureLogSync)
        {
            // The cap counts the types seen in the last hour: older entries no longer count.
            foreach (var old in FailureLines.Where(l => now - l.Value >= FailureLogInterval).Select(l => l.Key).ToList())
            {
                FailureLines.Remove(old);
            }

            if (FailureLines.TryGetValue(type, out var last) ? now - last < FailureLogInterval : FailureLines.Count >= MaxLoggedTypes)
            {
                return false;
            }

            FailureLines[type] = now;
            return true;
        }
    }

    private sealed class Attempt
    {
        public TimeProvider? Time { get; set; }

        public long Started { get; set; }

        public Guid? UserId { get; set; }

        public IDisposable? Lock { get; set; }

        public Action? Restore { get; set; }

        public bool Translated { get; set; }

        /// <summary>Puts the typed text back and releases the account, after a plugin error before the native call.</summary>
        public void Undo()
        {
            try
            {
                Restore?.Invoke();
                Translated = false;
            }
            catch (Exception)
            {
                // The text could not be put back: Jellyfin gets the user name, so a failure stays generic.
            }

            Restore = null;
            Release();
        }

        public void Release()
        {
            Lock?.Dispose();
            Lock = null;
        }
    }
}

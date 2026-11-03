using System;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MailConcierge.Accounts;
using Jellyfin.Plugin.MailConcierge.Configuration;
using Jellyfin.Plugin.MailConcierge.Localization;
using Jellyfin.Plugin.MailConcierge.Security;
using MediaBrowser.Controller.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MailConcierge.Web;

/// <summary>
/// Takes over Jellyfin's native forgot-password endpoints so that responses never reveal whether an account exists
/// and a PIN never becomes a password.
/// </summary>
public sealed class ForgotPasswordFilter : IAsyncActionFilter
{
    private const string ControllerName = "User";
    private const string ForgotPasswordAction = "ForgotPassword";
    private const string ForgotPasswordPinAction = "ForgotPasswordPin";
    private const string UsernameProperty = "EnteredUsername";
    private const string UnknownClientKey = "unknown";
    private static readonly TimeSpan ResponseTime = TimeSpan.FromMilliseconds(300);

    private readonly IServiceProvider _services;
    private readonly ILogger<ForgotPasswordFilter> _logger;
    private int _failureLogged;

    /// <summary>
    /// Initializes a new instance of the <see cref="ForgotPasswordFilter"/> class.
    /// </summary>
    /// <remarks>
    /// Only the service provider is injected: this is a global MVC filter, so it is built for every API call, and the
    /// plugin services throw while the plugin is not initialized. They are resolved lazily, for the target actions only.
    /// </remarks>
    /// <param name="services">The service provider.</param>
    /// <param name="logger">The logger.</param>
    public ForgotPasswordFilter(IServiceProvider services, ILogger<ForgotPasswordFilter> logger)
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
        var isTarget = action is not null
            && string.Equals(action.ControllerName, ControllerName, StringComparison.Ordinal)
            && (string.Equals(action.ActionName, ForgotPasswordAction, StringComparison.Ordinal)
                || string.Equals(action.ActionName, ForgotPasswordPinAction, StringComparison.Ordinal));
        if (!isTarget)
        {
            await next().ConfigureAwait(false);
            return;
        }

        Deps deps;
        try
        {
            deps = new Deps(
                _services.GetRequiredService<Func<PluginConfiguration>>(),
                _services.GetRequiredService<ProxyGuard>(),
                _services.GetRequiredService<RecoveryService>(),
                _services.GetRequiredService<ForgotPasswordResponses>(),
                _services.GetRequiredService<IServerConfigurationManager>(),
                _services.GetRequiredService<TimeProvider>());
            _ = deps.Config();
        }
        catch (Exception ex)
        {
            // Plugin not initialized (malfunctioned): never break the native endpoint.
            if (Interlocked.Exchange(ref _failureLogged, 1) == 0)
            {
                _logger.LogError(ex, "Mail Concierge services are unavailable, the native forgot-password behavior is used");
            }

            await next().ConfigureAwait(false);
            return;
        }

        var cfg = deps.Config();
        if (!cfg.Enabled || cfg.NativeForgotPassword)
        {
            await next().ConfigureAwait(false);
            return;
        }

        if (string.Equals(action!.ActionName, ForgotPasswordPinAction, StringComparison.Ordinal))
        {
            // Same answer as the native code when no PIN matches (ExceptionMiddleware, ResourceNotFoundException).
            context.Result = new ContentResult
            {
                StatusCode = StatusCodes.Status404NotFound,
                ContentType = MediaTypeNames.Text.Plain,
                Content = "Error processing request.",
            };
            return;
        }

        var time = deps.Time;
        var started = time.GetTimestamp();
        var http = context.HttpContext;
        deps.Guard.Observe(http);
        var ip = http.Connection.RemoteIpAddress;
        var key = ip is null ? UnknownClientKey : ClientIp.Key(ip);
        var username = ReadUsername(context) ?? string.Empty;
        var recovery = deps.Recovery;

        // Fire and forget: response timing must not depend on whether the account is eligible.
        _ = Task.Run(async () =>
        {
            try
            {
                await recovery.RequestAsync(username, key).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Recovery request failed");
            }
        });

        var remaining = ResponseTime - time.GetElapsedTime(started);
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, time).ConfigureAwait(false);
        }

        context.Result = new ObjectResult(deps.Responses.Neutral(Language(deps.ServerConfig, cfg))) { StatusCode = StatusCodes.Status200OK };
    }

    private static string? ReadUsername(ActionExecutingContext context)
    {
        foreach (var arg in context.ActionArguments.Values)
        {
            var prop = arg?.GetType().GetProperty(UsernameProperty);
            if (prop?.PropertyType == typeof(string))
            {
                return (string?)prop.GetValue(arg);
            }
        }

        return null;
    }

    private static string Language(IServerConfigurationManager serverConfig, PluginConfiguration cfg)
    {
        return Languages.Normalize(serverConfig.Configuration.UICulture, cfg.DefaultLanguage);
    }

    private sealed record Deps(
        Func<PluginConfiguration> Config,
        ProxyGuard Guard,
        RecoveryService Recovery,
        ForgotPasswordResponses Responses,
        IServerConfigurationManager ServerConfig,
        TimeProvider Time);
}

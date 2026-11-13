using System;
using Jellyfin.Data.Events.Users;
using Jellyfin.Plugin.MailConcierge.Accounts;
using Jellyfin.Plugin.MailConcierge.Configuration;
using Jellyfin.Plugin.MailConcierge.Data;
using Jellyfin.Plugin.MailConcierge.Email;
using Jellyfin.Plugin.MailConcierge.Invitations;
using Jellyfin.Plugin.MailConcierge.Security;
using Jellyfin.Plugin.MailConcierge.Seerr;
using Jellyfin.Plugin.MailConcierge.Web;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.MailConcierge;

/// <summary>
/// Registers the services of the plugin in the Jellyfin service collection.
/// </summary>
public class ServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton(_ => new MailConciergeStore(DataFolder()));
        serviceCollection.AddSingleton(_ => new TokenService(DataFolder()));
        serviceCollection.TryAddSingleton(TimeProvider.System);
        serviceCollection.AddSingleton<RateLimiter>();
        serviceCollection.AddSingleton<Func<PluginConfiguration>>(_ => Configuration);
        serviceCollection.AddSingleton<Action<PluginConfiguration>>(_ => SaveConfiguration);
        serviceCollection.AddSingleton<IEmailTransport, SmtpEmailTransport>();
        serviceCollection.AddSingleton(sp => new EmailQueue(
            sp.GetRequiredService<Func<PluginConfiguration>>(),
            sp.GetRequiredService<IEmailTransport>(),
            sp.GetRequiredService<RateLimiter>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<EmailQueue>>(),
            sp.GetRequiredService<TimeProvider>()));
        serviceCollection.AddSingleton<ProxyGuard>();
        serviceCollection.AddSingleton<ServiceGate>();
        serviceCollection.AddSingleton<IHostedService>(sp => sp.GetRequiredService<EmailQueue>());
        serviceCollection.AddSingleton<SessionCloser>();
        serviceCollection.AddSingleton<RecoveryService>();
        serviceCollection.AddHttpClient();
        serviceCollection.AddHttpClient(SeerrClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(SeerrClient.CreateHandler);
        serviceCollection.AddSingleton<SeerrClient>();
        serviceCollection.AddSingleton<SeerrSync>();
        serviceCollection.AddSingleton<ISeerrSync>(sp => sp.GetRequiredService<SeerrSync>());
        serviceCollection.AddSingleton<EmailService>();
        serviceCollection.AddSingleton<IEventConsumer<UserDeletedEventArgs>>(sp => sp.GetRequiredService<EmailService>());
        serviceCollection.AddSingleton<EmailLoginResolver>();
        serviceCollection.AddSingleton<EmailLoginProbe>();
        serviceCollection.AddSingleton<AvatarSaver>();
        serviceCollection.AddSingleton<InvitationService>();
        serviceCollection.AddSingleton<ForgotPasswordResponses>();
        serviceCollection.AddSingleton<PageRenderer>();
        serviceCollection.AddTransient<ForgotPasswordFilter>();
        serviceCollection.Configure<MvcOptions>(o => o.Filters.Add<ForgotPasswordFilter>());
        serviceCollection.AddTransient<EmailLoginFilter>();
        serviceCollection.Configure<MvcOptions>(o => o.Filters.Add<EmailLoginFilter>());
        serviceCollection.AddSingleton<IPasswordResetProvider, MailConciergePasswordResetProvider>();
        serviceCollection.AddTransient<IStartupFilter, IndexInjectionStartupFilter>();
    }

    private static PluginConfiguration Configuration()
        => (Plugin.Instance ?? throw new InvalidOperationException("The Mail Concierge plugin is not initialized.")).Configuration;

    private static void SaveConfiguration(PluginConfiguration configuration)
        => (Plugin.Instance ?? throw new InvalidOperationException("The Mail Concierge plugin is not initialized.")).UpdateConfiguration(configuration);

    private static string DataFolder() =>
        (Plugin.Instance ?? throw new InvalidOperationException("The Mail Concierge plugin is not initialized.")).DataFolderPath;
}

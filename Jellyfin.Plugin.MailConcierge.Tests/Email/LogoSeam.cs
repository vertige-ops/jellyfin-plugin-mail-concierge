using System;
using Jellyfin.Plugin.MailConcierge.Email;
using Xunit;

namespace Jellyfin.Plugin.MailConcierge.Tests.Email;

/// <summary>Collection for the tests that touch the logo resource seam; it never runs alongside other tests.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LogoSeamCollection
{
    public const string Name = "Logo resource seam";
}

/// <summary>Makes the embedded logo unreadable for the duration of a test, then restores it.</summary>
internal sealed class LogoSeam : IDisposable
{
    private readonly Func<byte[]?> _previous;

    private LogoSeam()
    {
        _previous = EmailTemplates.LogoSource;
        EmailTemplates.LogoSource = static () => null;
    }

    public static LogoSeam Unreadable() => new();

    public void Dispose() => EmailTemplates.LogoSource = _previous;
}

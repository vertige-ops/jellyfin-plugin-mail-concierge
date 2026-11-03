using System.IO;
using System.Linq;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MailConcierge.Tests;

public class PluginPagesTests
{
    [Fact]
    public void GetPages_ExposesOnlyTheSettingsPage_InMainMenu()
    {
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.PluginConfigurationsPath).Returns(Path.GetTempPath());
        paths.SetupGet(p => p.PluginsPath).Returns(Path.GetTempPath());
        var plugin = new Plugin(paths.Object, new Mock<IXmlSerializer>().Object);

        var settings = Assert.Single(plugin.GetPages());

        Assert.Equal("MailConcierge", settings.Name);
        Assert.Equal("Mail Concierge", settings.DisplayName);
        Assert.True(settings.EnableInMainMenu);
        Assert.Equal("server", settings.MenuSection);
        Assert.False(string.IsNullOrEmpty(settings.MenuIcon));
    }
}

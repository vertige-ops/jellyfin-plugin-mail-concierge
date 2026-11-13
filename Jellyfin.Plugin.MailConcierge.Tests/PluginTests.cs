using System;
using System.IO;
using System.Text.RegularExpressions;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MailConcierge.Tests;

public class PluginTests
{
    [Fact]
    public void Plugin_HasExpectedNameAndId()
    {
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.PluginConfigurationsPath).Returns(Path.GetTempPath());
        paths.SetupGet(p => p.PluginsPath).Returns(Path.GetTempPath());

        var plugin = new Plugin(paths.Object, new Mock<IXmlSerializer>().Object);

        Assert.Equal("Mail Concierge", plugin.Name);
        Assert.Equal(ReadGuidFromBuildYaml(), plugin.Id);
    }

    [Fact]
    public void AssemblyVersion_Is_12_4_0_0()
        => Assert.Equal(new Version(12, 4, 0, 0), typeof(Plugin).Assembly.GetName().Version);

    private static Guid ReadGuidFromBuildYaml()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "build.yaml")))
        {
            dir = dir.Parent;
        }

        Assert.True(
            dir is not null,
            $"build.yaml not found in any parent directory of '{AppContext.BaseDirectory}'; run the tests from a checkout of the repository.");
        var text = File.ReadAllText(Path.Combine(dir.FullName, "build.yaml"));
        var match = Regex.Match(text, "^guid:\\s*\"([^\"]+)\"", RegexOptions.Multiline);
        Assert.True(match.Success);
        return Guid.Parse(match.Groups[1].Value);
    }
}

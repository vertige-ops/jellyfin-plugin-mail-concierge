using System;
using System.Linq;
using Jellyfin.Plugin.MailConcierge.Configuration;
using Xunit;

namespace Jellyfin.Plugin.MailConcierge.Tests.Configuration;

public class SmtpSettingsTests
{
    [Theory]
    [InlineData("smtp.example.org")]
    [InlineData("localhost")]
    [InlineData("smtp-relay.example.org")]
    [InlineData("192.0.2.10")]

    // CheckHostName accepts an IPv6 address both bare and in brackets: both forms are kept, the bare one is what an administrator types.
    [InlineData("2001:db8::1")]
    [InlineData("[2001:db8::1]")]
    [InlineData("smtp.example.org.")]
    [InlineData("bücher.example")]
    [InlineData("postfix_relay")]
    public void IsValidHost_AcceptsAHostNameOrAnAddress(string host)
        => Assert.True(SmtpSettings.IsValidHost(host));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://smtp.example.org")]
    [InlineData("smtp://smtp.example.org")]
    [InlineData("smtp.example.org:587")]
    [InlineData("smtp.example.org/x")]
    [InlineData("a b")]
    [InlineData(" smtp.example.org")]
    [InlineData("smtp.example.org ")]
    [InlineData("user@example.org")]
    [InlineData("-bad.example")]
    [InlineData("a..b")]
    public void IsValidHost_RefusesAWebAddressAnEmailOrAnythingWithASpaceOrAPort(string? host)
        => Assert.False(SmtpSettings.IsValidHost(host));

    [Fact]
    public void IsValidHost_AcceptsAtMost253Characters()
    {
        var longest = string.Join('.', Enumerable.Repeat("a", 127));
        Assert.Equal(253, longest.Length);
        Assert.True(SmtpSettings.IsValidHost(longest));

        var tooLong = "a" + longest;
        Assert.Equal(254, tooLong.Length);
        Assert.False(SmtpSettings.IsValidHost(tooLong));
    }

    [Fact]
    public void IsComplete_TrueWhenUrlHostPortAndSenderAreValid()
        => Assert.True(SmtpSettings.IsComplete(Complete()));

    [Fact]
    public void IsComplete_DoesNotRequireACredential()
    {
        var c = Complete();
        c.SmtpUsername = string.Empty;
        c.SmtpPassword = string.Empty;

        Assert.True(SmtpSettings.IsComplete(c));
    }

    [Theory]
    [InlineData("PublicUrl", "")]
    [InlineData("PublicUrl", "  ")]
    [InlineData("SmtpHost", "")]
    [InlineData("SmtpHost", " ")]
    [InlineData("SmtpHost", "smtp.example.org:587")]
    [InlineData("SmtpHost", "https://smtp.example.org")]
    [InlineData("FromAddress", "")]
    [InlineData("FromAddress", " ")]
    [InlineData("FromAddress", "not an address")]
    [InlineData("FromAddress", "jf@")]
    public void IsComplete_FalseForEachMissingOrInvalidText(string field, string value)
    {
        var c = Complete();
        typeof(PluginConfiguration).GetProperty(field)!.SetValue(c, value);

        Assert.False(SmtpSettings.IsComplete(c));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void IsComplete_FalseForAPortOutOfRange(int port)
    {
        var c = Complete();
        c.SmtpPort = port;

        Assert.False(SmtpSettings.IsComplete(c));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(65535)]
    public void IsComplete_TrueForTheLimitsOfThePortRange(int port)
    {
        var c = Complete();
        c.SmtpPort = port;

        Assert.True(SmtpSettings.IsComplete(c));
    }

    [Fact]
    public void CanSendEmail_IsTheSharedPredicate()
    {
        var c = Complete();
        Assert.True(c.CanSendEmail());

        c.SmtpHost = "smtp.example.org:587";
        Assert.False(c.CanSendEmail());
    }

    private static PluginConfiguration Complete() => new()
    {
        PublicUrl = "https://jf.example.org",
        SmtpHost = "smtp.example.org",
        SmtpPort = 587,
        FromAddress = "jf@example.org",
    };
}

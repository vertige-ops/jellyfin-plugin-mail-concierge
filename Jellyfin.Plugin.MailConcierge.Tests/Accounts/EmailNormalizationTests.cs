using Jellyfin.Plugin.MailConcierge.Accounts;
using Xunit;

namespace Jellyfin.Plugin.MailConcierge.Tests.Accounts;

public class EmailNormalizationTests
{
    [Theory]
    [InlineData("bob@example.org")]
    [InlineData("bob+tag@example.org")]
    [InlineData("first.last@mail.example.co.uk")]
    [InlineData("o'brien@example.org")]
    [InlineData("a_b-c@sub.domain-name.example.org")]
    [InlineData("  bob@example.org  ")]
    [InlineData("bob@xn--bcher-kva.example")]
    public void TryNormalize_AcceptsCommonAddresses(string input)
    {
        Assert.True(EmailService.TryNormalize(input, out var normalized));
        Assert.Equal(input.Trim(), normalized);
    }

    [Theory]
    [InlineData("\"bob smith\"@example.org")]
    [InlineData("\"a<b\"@example.org")]
    [InlineData("bob smith@example.org")]
    [InlineData("bob@[1.2.3.4]")]
    [InlineData("bob@[IPv6:::1]")]
    [InlineData("bob@1.2.3.4.5")]
    [InlineData("bob@localhost")]
    [InlineData("bob@example..org")]
    [InlineData(".bob@example.org")]
    [InlineData("bob.@example.org")]
    [InlineData("bo..b@example.org")]
    [InlineData("bob@-example.org")]
    [InlineData("bob@example-.org")]
    [InlineData("bob@example.org.")]
    [InlineData("bób@example.org")]
    [InlineData("bob(comment)@example.org")]
    [InlineData("bob@example.org\r\nBcc: x@y.z")]
    [InlineData("a@b@example.org")]
    [InlineData("")]
    [InlineData(null)]
    public void TryNormalize_RefusesExoticForms(string? input)
        => Assert.False(EmailService.TryNormalize(input, out _));

    [Fact]
    public void TryNormalize_RefusesTooLongLocalPart()
        => Assert.False(EmailService.TryNormalize(new string('a', 65) + "@example.org", out _));
}

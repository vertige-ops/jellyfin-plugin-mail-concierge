using Jellyfin.Plugin.MailConcierge.Configuration;
using Xunit;

namespace Jellyfin.Plugin.MailConcierge.Tests.Configuration;

public class PublicUrlSuggestionTests
{
    [Fact]
    public void From_TakesTheAllEntry()
        => Assert.Equal("https://jf.example.org", PublicUrlSuggestion.From(new[] { "all=https://jf.example.org" }));

    [Fact]
    public void From_AllWinsOverExternal()
        => Assert.Equal("https://all.example.org", PublicUrlSuggestion.From(new[] { "external=https://ext.example.org", "all=https://all.example.org" }));

    [Fact]
    public void From_ExternalAlone()
        => Assert.Equal("http://ext.example.org:8096", PublicUrlSuggestion.From(new[] { "192.168.0.0/24=http://192.168.0.2:8096", "external=http://ext.example.org:8096" }));

    [Fact]
    public void From_SubnetEntriesAloneGiveNothing()
        => Assert.Null(PublicUrlSuggestion.From(new[] { "192.168.0.0/24=http://192.168.0.2:8096", "10.0.0.0/8=https://lan.example.org" }));

    [Theory]
    [InlineData("all=ftp://x.example.org")]
    [InlineData("all=pas-une-url")]
    [InlineData("all=")]
    [InlineData("all")]
    [InlineData("all=https://")]
    [InlineData("all=//jf.example.org")]
    [InlineData("=https://jf.example.org")]
    public void From_RefusesAnAddressThatIsNotHttp(string entry)
        => Assert.Null(PublicUrlSuggestion.From(new[] { entry }));

    [Fact]
    public void From_AnInvalidAllDoesNotFallBackToExternal()
        => Assert.Null(PublicUrlSuggestion.From(new[] { "all=ftp://x.example.org", "external=https://ext.example.org" }));

    [Fact]
    public void From_NoEntriesGivesNothing()
    {
        Assert.Null(PublicUrlSuggestion.From(null));
        Assert.Null(PublicUrlSuggestion.From(System.Array.Empty<string>()));
        Assert.Null(PublicUrlSuggestion.From(new string?[] { null, " " }!));
    }

    [Theory]
    [InlineData("ALL=https://jf.example.org")]
    [InlineData("All = https://jf.example.org ")]
    public void From_TheKeyIsCaseInsensitive(string entry)
        => Assert.Equal("https://jf.example.org", PublicUrlSuggestion.From(new[] { entry }));

    [Fact]
    public void From_KeepsTheQueryAndPathOfTheAddress()
        => Assert.Equal("https://example.org/jellyfin", PublicUrlSuggestion.From(new[] { "all=https://example.org/jellyfin" }));
}

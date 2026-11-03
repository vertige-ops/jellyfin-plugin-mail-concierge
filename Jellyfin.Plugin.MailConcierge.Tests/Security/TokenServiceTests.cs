using System;
using System.IO;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.MailConcierge.Security;
using Xunit;

namespace Jellyfin.Plugin.MailConcierge.Tests.Security;

public sealed class TokenServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mailconcierge-test-" + Guid.NewGuid().ToString("N"));
    private readonly string _dir2 = Path.Combine(Path.GetTempPath(), "mailconcierge-test-" + Guid.NewGuid().ToString("N"));

    public TokenServiceTests()
    {
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(_dir2);
    }

    public void Dispose()
    {
        Directory.Delete(_dir, true);
        Directory.Delete(_dir2, true);
    }

    [Fact]
    public void Token_Is43CharsBase64Url()
    {
        var token = new TokenService(_dir).NewToken();
        Assert.Matches("^[A-Za-z0-9_-]{43}$", token);
    }

    [Fact]
    public void Pin_MatchesPattern()
    {
        var service = new TokenService(_dir);
        for (var i = 0; i < 50; i++)
        {
            Assert.Matches(new Regex(@"^\d{4}-\d{4}$"), service.NewPin());
        }
    }

    [Fact]
    public void Hash_SameInputSameKey_Equal_DifferentKey_Different()
    {
        var a = new TokenService(_dir);
        var a2 = new TokenService(_dir);
        var b = new TokenService(_dir2);

        Assert.Equal(a.Hash("secret"), a2.Hash("secret"));
        Assert.NotEqual(a.Hash("secret"), a.Hash("other"));
        Assert.NotEqual(a.Hash("secret"), b.Hash("secret"));
        Assert.True(a.Matches("secret", a.Hash("secret")));
        Assert.False(a.Matches("secret", a.Hash("other")));
        Assert.False(a.Matches("secret", "not-base64!"));
    }

    [Fact]
    public void Matches_IgnoresPinDash()
    {
        var service = new TokenService(_dir);
        var hash = service.Hash("1234-5678");
        Assert.True(service.Matches("12345678", hash));
        Assert.True(service.Matches("1234-5678", hash));
        Assert.Equal(service.Hash("12345678"), hash);
    }

    [Fact]
    public void KeyFile_CreatedWithMode600()
    {
        var service = new TokenService(_dir);
        service.Hash("x");
        var path = Path.Combine(_dir, "mailconcierge.key");
        Assert.True(File.Exists(path));
        Assert.Equal(32, new FileInfo(path).Length);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
    }

    [Fact]
    public void Hash_TokenWithDashes_DiffersFromDashless()
    {
        var service = new TokenService(_dir);
        Assert.NotEqual(service.Hash("ab-cd_efgh-ijkl"), service.Hash("abcd_efghijkl"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    public void ExistingKeyWithWrongLength_HashThrows(int length)
    {
        File.WriteAllBytes(Path.Combine(_dir, "mailconcierge.key"), new byte[length]);
        Assert.Throws<InvalidDataException>(() => new TokenService(_dir).Hash("x"));
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.MailConcierge.Accounts;
using Jellyfin.Plugin.MailConcierge.Configuration;
using Jellyfin.Plugin.MailConcierge.Email;
using Jellyfin.Plugin.MailConcierge.Security;
using Jellyfin.Plugin.MailConcierge.Web;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MailConcierge.Tests.Web;

public sealed class AdminControllerTests : IDisposable
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
    private readonly PluginConfiguration _config = new() { SmtpPassword = "smtp-secret", SeerrApiKey = "seerr-secret", PublicUrl = "https://jf.example/" };
    private readonly Mock<IUserManager> _users = new();
    private readonly EmailQueue _queue;
    private readonly NetworkConfiguration _net = new() { KnownProxies = Array.Empty<string>() };
    private readonly ProxyGuard _guard;
    private int _saves;
    private AdminController _controller = null!;

    public AdminControllerTests()
    {
        _queue = new EmailQueue(() => _config, Mock.Of<IEmailTransport>(), new RateLimiter(_time), NullLogger<EmailQueue>.Instance, _time);
        var cfg = new Mock<IServerConfigurationManager>();
        cfg.Setup(c => c.GetConfiguration("network")).Returns(() => _net);
        _guard = new ProxyGuard(cfg.Object);
        Rebuild();
    }

    public void Dispose() => _queue.Dispose();

    [Fact]
    public void GetConfig_BlanksSecrets()
    {
        var result = Assert.IsType<ContentResult>(_controller.GetConfig());

        Assert.DoesNotContain("smtp-secret", result.Content!, StringComparison.Ordinal);
        Assert.DoesNotContain("seerr-secret", result.Content!, StringComparison.Ordinal);
        var root = JsonDocument.Parse(result.Content!).RootElement;
        Assert.Equal(string.Empty, root.GetProperty("smtpPassword").GetString());
        Assert.Equal(string.Empty, root.GetProperty("seerrApiKey").GetString());
        Assert.True(root.GetProperty("smtpPasswordSet").GetBoolean());
        Assert.True(root.GetProperty("seerrApiKeySet").GetBoolean());
    }

    [Fact]
    public void GetConfig_ExposesTheSupportedLanguages()
    {
        var root = JsonDocument.Parse(Assert.IsType<ContentResult>(_controller.GetConfig()).Content!).RootElement;

        var languages = root.GetProperty("languages").EnumerateArray().ToDictionary(l => l.GetProperty("code").GetString()!, l => l.GetProperty("name").GetString()!);
        Assert.Equal("Français", languages["fr"]);
        Assert.Equal("English", languages["en"]);
        var listed = JsonDocument.Parse(Assert.IsType<ContentResult>(_controller.GetLanguages()).Content!).RootElement;
        Assert.Equal(languages.Count, listed.GetArrayLength());
    }

    [Theory]
    [InlineData("config", "fr-FR", "fr", "cfg.title", "title")]
    [InlineData("invitations", "xx", "en", "inv.title", "title")]
    public void GetStrings_ReturnsThePageTextsInTheLanguage_WithoutPrefix(string page, string lang, string expectedLanguage, string prefixedKey, string key)
    {
        var result = Assert.IsType<ContentResult>(_controller.GetStrings(lang, page));

        var root = JsonDocument.Parse(result.Content!).RootElement;
        Assert.Equal(expectedLanguage, root.GetProperty("language").GetString());
        Assert.Equal(EmailTemplates.GetString(expectedLanguage, prefixedKey), root.GetProperty("strings").GetProperty(key).GetString());
    }

    [Theory]
    [InlineData("ja-JP", "en")]
    [InlineData("fr-FR", "fr")]
    [InlineData("", "en")]
    public void GetStrings_UnsupportedOrMissingLanguage_IsEnglish_NotTheEmailDefault(string lang, string expected)
    {
        _config.DefaultLanguage = "fr";

        var root = JsonDocument.Parse(Assert.IsType<ContentResult>(_controller.GetStrings(lang, "config")).Content!).RootElement;

        Assert.Equal(expected, root.GetProperty("language").GetString());
    }

    [Fact]
    public void GetStrings_FrenchHasOwnTexts_AndUnknownPageIsRejected()
    {
        var fr = JsonDocument.Parse(Assert.IsType<ContentResult>(_controller.GetStrings("fr", "config")).Content!).RootElement.GetProperty("strings");
        var en = JsonDocument.Parse(Assert.IsType<ContentResult>(_controller.GetStrings("en", "config")).Content!).RootElement.GetProperty("strings");

        Assert.NotEqual(en.GetProperty("save").GetString(), fr.GetProperty("save").GetString());
        Assert.Equal(400, Assert.IsType<ContentResult>(_controller.GetStrings("fr", "nope")).StatusCode);
    }

    [Fact]
    public void PostConfig_EmptySecret_KeepsStored()
    {
        var result = Assert.IsType<ContentResult>(_controller.SetConfig(new AdminConfigRequest { SmtpHost = "smtp.example", SmtpPassword = string.Empty, SeerrApiKey = string.Empty }));

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("smtp-secret", _config.SmtpPassword);
        Assert.Equal("seerr-secret", _config.SeerrApiKey);
        Assert.Equal("smtp.example", _config.SmtpHost);
        Assert.Equal(1, _saves);

        _controller.SetConfig(new AdminConfigRequest { SmtpPassword = "new", SeerrApiKey = "newkey" });
        Assert.Equal("new", _config.SmtpPassword);
        Assert.Equal("newkey", _config.SeerrApiKey);
    }

    private static readonly JsonSerializerOptions BinderJson = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("{\"smtpHost\":\"x\",\"smtpPort\":25,\"smtpSecurity\":\"None\",\"fromAddress\":\"a@example.org\"}")]
    [InlineData("{\"seerrEnabled\":true,\"seerrUrl\":\"https://seerr.example\"}")]
    public void PostConfig_WithOnlySmtpKeys_KeepsEnabledAndCheckboxes(string json)
    {
        // The body goes through the same JSON deserialization as the MVC binder (camelCase, case-insensitive).
        var request = JsonSerializer.Deserialize<AdminConfigRequest>(json, BinderJson);
        _config.Enabled = true;
        _config.NativeForgotPassword = true;
        _config.AllowAdministrators = true;

        var result = Assert.IsType<ContentResult>(_controller.SetConfig(request));

        Assert.Equal(200, result.StatusCode);
        Assert.True(_config.Enabled);
        Assert.True(_config.NativeForgotPassword);
        Assert.True(_config.AllowAdministrators);
    }

    [Fact]
    public void PostConfig_WithOnlySmtpKeys_KeepsGeneralAndSeerrSettings()
    {
        _config.Enabled = true;
        _config.DefaultLanguage = "fr";
        _config.PinMinutes = 17;
        _config.SessionsOnReset = SessionClosePolicy.None;
        _config.SeerrEnabled = true;
        _config.SeerrUrl = "https://seerr.example";
        _config.TemplateOverrides["fr/invitation.html"] = "custom";

        var result = Assert.IsType<ContentResult>(_controller.SetConfig(new AdminConfigRequest { SmtpHost = "x" }));

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("x", _config.SmtpHost);
        Assert.True(_config.Enabled);
        Assert.Equal("fr", _config.DefaultLanguage);
        Assert.Equal(17, _config.PinMinutes);
        Assert.Equal(SessionClosePolicy.None, _config.SessionsOnReset);
        Assert.True(_config.SeerrEnabled);
        Assert.Equal("https://seerr.example", _config.SeerrUrl);
        Assert.Equal("smtp-secret", _config.SmtpPassword);
        Assert.Equal("seerr-secret", _config.SeerrApiKey);
        Assert.Equal("custom", _config.TemplateOverrides["fr/invitation.html"]);
    }

    [Theory]
    [InlineData("SmtpPort", 0)]
    [InlineData("SmtpPort", 65536)]
    [InlineData("PinMinutes", 0)]
    [InlineData("MaxRecoveryEmailsPerHour", -1)]
    [InlineData("MaxInvitationEmailsPerHour", 0)]
    [InlineData("MaxAccountEmailsPerHour", -1)]
    public void PostConfig_InvalidNumber_IsRejected_AndNothingSaved(string field, int value)
    {
        var request = new AdminConfigRequest { SmtpHost = "changed" };
        typeof(AdminConfigRequest).GetProperty(field)!.SetValue(request, value);

        var result = Assert.IsType<ContentResult>(_controller.SetConfig(request));

        Assert.Equal(400, result.StatusCode);
        Assert.Equal(string.Empty, _config.SmtpHost);
        Assert.Equal(0, _saves);
    }

    [Theory]
    [InlineData("PublicUrl", "ftp://x.example", "invalid_public_url")]
    [InlineData("PublicUrl", "not a url", "invalid_public_url")]
    [InlineData("SeerrUrl", "javascript:alert(1)", "invalid_seerr_url")]
    [InlineData("DefaultLanguage", "ja", "invalid_language")]
    [InlineData("SmtpSecurity", "Bogus", "invalid_smtp_security")]
    [InlineData("SessionsOnReset", "7", "invalid_sessions")]
    public void PostConfig_InvalidValue_IsRejectedWithCode(string field, string value, string code)
    {
        var request = new AdminConfigRequest();
        typeof(AdminConfigRequest).GetProperty(field)!.SetValue(request, value);

        var result = Assert.IsType<ContentResult>(_controller.SetConfig(request));

        Assert.Equal(400, result.StatusCode);
        Assert.Equal(code, JsonDocument.Parse(result.Content!).RootElement.GetProperty("code").GetString());
        Assert.Equal(0, _saves);
    }

    [Fact]
    public void PostConfig_RejectsTextTemplateWithoutSubjectLine()
    {
        var result = Assert.IsType<ContentResult>(_controller.SetConfig(new AdminConfigRequest
        {
            SmtpHost = "changed.example",
            TemplateOverrides = new Dictionary<string, string> { ["en/invitation.txt"] = "no subject here\nbody" },
        }));

        Assert.Equal(400, result.StatusCode);
        Assert.Contains("invalid_template_subject", result.Content!, StringComparison.Ordinal);
        Assert.Empty(_config.TemplateOverrides);
        Assert.NotEqual("changed.example", _config.SmtpHost);
        Assert.Equal(0, _saves);
    }

    [Fact]
    public void PostConfig_TemplateEqualToShippedOne_IsNotAnOverride_EvenWithCrLf()
    {
        var shipped = EmailTemplates.ReadEmbedded("en", "invitation", "txt")!;
        var html = EmailTemplates.ReadEmbedded("fr", "recovery", "html")!;

        var result = Assert.IsType<ContentResult>(_controller.SetConfig(new AdminConfigRequest
        {
            TemplateOverrides = new Dictionary<string, string>
            {
                ["en/invitation.txt"] = shipped.ReplaceLineEndings("\r\n"),
                ["fr/recovery.html"] = html,
                ["en/recovery.html"] = "<p>custom</p>",
            },
        }));

        Assert.Equal(200, result.StatusCode);
        Assert.Equal(new[] { "en/recovery.html" }, _config.TemplateOverrides.Keys.ToArray());
    }

    [Fact]
    public void PostConfig_AcceptsEmptyPublicUrl_ValidTemplates_AndRemovesBlankOverride()
    {
        _config.TemplateOverrides["fr/recovery.html"] = "old";

        var result = Assert.IsType<ContentResult>(_controller.SetConfig(new AdminConfigRequest
        {
            PublicUrl = string.Empty,
            DefaultLanguage = "fr",
            SmtpSecurity = "SslOnConnect",
            TemplateOverrides = new Dictionary<string, string> { ["fr/recovery.html"] = " ", ["en/invitation.txt"] = "Subject: x\nbody" },
        }));

        Assert.Equal(200, result.StatusCode);
        Assert.Equal(SmtpSecurity.SslOnConnect, _config.SmtpSecurity);
        Assert.Equal(new[] { "en/invitation.txt" }, _config.TemplateOverrides.Keys.ToArray());

        var bad = Assert.IsType<ContentResult>(_controller.SetConfig(new AdminConfigRequest { TemplateOverrides = new Dictionary<string, string> { ["../x/y.html"] = "a" } }));
        Assert.Equal(400, bad.StatusCode);
    }

    [Fact]
    public async Task AssignProvider_SkipsAdminsAndLdap()
    {
        var plain = NewUser("plain", AccountPolicy.DefaultAuthProviderId, admin: false);
        var admin = NewUser("admin", AccountPolicy.DefaultAuthProviderId, admin: true);
        var ldap = NewUser("ldap", "LDAP-Authentication.LdapAuthenticationProviderPlugin", admin: false);
        _users.Setup(u => u.GetUsers()).Returns(new[] { plain, admin, ldap });

        var result = Assert.IsType<ContentResult>(await _controller.AssignProvider());

        var root = JsonDocument.Parse(result.Content!).RootElement;
        Assert.Equal(1, root.GetProperty("assigned").GetInt32());
        Assert.Equal(1, root.GetProperty("skippedAdmins").GetInt32());
        Assert.Equal(1, root.GetProperty("skippedExternal").GetInt32());
        Assert.Equal(MailConciergePasswordResetProvider.Id, plain.PasswordResetProviderId);
        Assert.NotEqual(MailConciergePasswordResetProvider.Id, admin.PasswordResetProviderId);
        Assert.NotEqual(MailConciergePasswordResetProvider.Id, ldap.PasswordResetProviderId);
        _users.Verify(u => u.UpdateUserAsync(plain), Times.Once);
        _users.Verify(u => u.UpdateUserAsync(admin), Times.Never);
        _users.Verify(u => u.UpdateUserAsync(ldap), Times.Never);
    }

    [Fact]
    public async Task AssignProvider_IncludesAdmins_WhenAllowed()
    {
        _config.AllowAdministrators = true;
        var admin = NewUser("admin", AccountPolicy.DefaultAuthProviderId, admin: true);
        _users.Setup(u => u.GetUsers()).Returns(new[] { admin });

        var result = Assert.IsType<ContentResult>(await _controller.AssignProvider());

        var root = JsonDocument.Parse(result.Content!).RootElement;
        Assert.Equal(1, root.GetProperty("assigned").GetInt32());
        Assert.Equal(0, root.GetProperty("skippedAdmins").GetInt32());
        Assert.Equal(MailConciergePasswordResetProvider.Id, admin.PasswordResetProviderId);
    }

    [Fact]
    public void Status_ReportsCodeNotSentence()
    {
        var ok = JsonDocument.Parse(Assert.IsType<ContentResult>(_controller.Status()).Content!).RootElement;
        Assert.False(ok.GetProperty("proxyGuardTripped").GetBoolean());
        Assert.Equal(JsonValueKind.Null, ok.GetProperty("reason").ValueKind);

        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("10.0.0.2");
        ctx.Request.Headers["X-Forwarded-For"] = "203.0.113.5";
        _guard.Observe(ctx);

        var tripped = JsonDocument.Parse(Assert.IsType<ContentResult>(_controller.Status()).Content!).RootElement;
        Assert.True(tripped.GetProperty("proxyGuardTripped").GetBoolean());
        Assert.Equal("unknown_proxy", tripped.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task TestEmail_NotConfigured_ReturnsACode()
    {
        var result = Assert.IsType<ContentResult>(await _controller.TestEmail(new TestEmailRequest("me@example.org")));

        var root = JsonDocument.Parse(result.Content!).RootElement;
        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.Equal("test_not_configured", root.GetProperty("error").GetString());
    }

    [Fact]
    public async Task TestEmail_Failure_NeverReturnsTheExceptionText_AndLogsIt()
    {
        _config.SmtpHost = "internal-smtp.corp.lan";
        _config.FromAddress = "jf@example.org";
        var transport = new Mock<IEmailTransport>();
        transport.Setup(t => t.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<System.Threading.CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Connection refused by 10.1.2.3:25 (internal-smtp.corp.lan)"));
        var queue = new EmailQueue(() => _config, transport.Object, new RateLimiter(_time), NullLogger<EmailQueue>.Instance, _time);
        var logger = new Mock<Microsoft.Extensions.Logging.ILogger<AdminController>>();
        logger.Setup(l => l.IsEnabled(It.IsAny<Microsoft.Extensions.Logging.LogLevel>())).Returns(true);
        var controller = new AdminController(_users.Object, queue, _guard, () => _config, _ => { }, logger.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        var result = Assert.IsType<ContentResult>(await controller.TestEmail(new TestEmailRequest("me@example.org")));

        Assert.DoesNotContain("10.1.2.3", result.Content!, StringComparison.Ordinal);
        Assert.DoesNotContain("refused", result.Content!, StringComparison.Ordinal);
        var root = JsonDocument.Parse(result.Content!).RootElement;
        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.Equal("test_send_failed", root.GetProperty("error").GetString());
        logger.Verify(
            l => l.Log(
                Microsoft.Extensions.Logging.LogLevel.Warning,
                It.IsAny<Microsoft.Extensions.Logging.EventId>(),
                It.Is<It.IsAnyType>((v, _) => $"{v}".Contains("10.1.2.3", StringComparison.Ordinal)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    private static User NewUser(string name, string provider, bool admin)
    {
        var user = new User(name, provider, "Jellyfin.Server.Implementations.Users.DefaultPasswordResetProvider");
        user.SetPermission(PermissionKind.IsAdministrator, admin);
        return user;
    }

    private void Rebuild()
    {
        _controller = new AdminController(_users.Object, _queue, _guard, () => _config, _ => _saves++, NullLogger<AdminController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }
}

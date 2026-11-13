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
using Jellyfin.Plugin.MailConcierge.Data;
using Jellyfin.Plugin.MailConcierge.Email;
using Jellyfin.Plugin.MailConcierge.Security;
using Jellyfin.Plugin.MailConcierge.Seerr;
using Jellyfin.Plugin.MailConcierge.Web;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Logging;
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
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mailconcierge-admin-" + Guid.NewGuid().ToString("N"));
    private readonly MailConciergeStore _store;
    private int _saves;
    private EmailLoginProbe _probe = Probe(available: true);
    private AdminController _controller = null!;

    public AdminControllerTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new MailConciergeStore(_dir);
        _queue = new EmailQueue(() => _config, Mock.Of<IEmailTransport>(), new RateLimiter(_time), NullLogger<EmailQueue>.Instance, _time);
        var cfg = new Mock<IServerConfigurationManager>();
        cfg.Setup(c => c.GetConfiguration("network")).Returns(() => _net);
        _guard = new ProxyGuard(cfg.Object);
        Rebuild();
    }

    public void Dispose()
    {
        _queue.Dispose();
        _store.Dispose();
        Directory.Delete(_dir, true);
    }

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
    public void SetConfig_TrimsSmtpUsername()
    {
        _controller.SetConfig(new AdminConfigRequest { SmtpUsername = "  user@example.org \n" });

        Assert.Equal("user@example.org", _config.SmtpUsername);
    }

    [Fact]
    public void SetConfig_ClearSmtpPassword_EmptiesIt()
    {
        var result = Assert.IsType<ContentResult>(_controller.SetConfig(new AdminConfigRequest { ClearSmtpPassword = true }));

        Assert.Equal(200, result.StatusCode);
        Assert.Equal(string.Empty, _config.SmtpPassword);
        Assert.Equal("seerr-secret", _config.SeerrApiKey);
    }

    [Fact]
    public void SetConfig_EmptyPassword_KeepsIt()
    {
        _controller.SetConfig(new AdminConfigRequest { SmtpPassword = string.Empty, ClearSmtpPassword = false });

        Assert.Equal("smtp-secret", _config.SmtpPassword);
    }

    [Fact]
    public void SetConfig_ClearWithNewPassword_KeepsTheNewOne()
    {
        _controller.SetConfig(new AdminConfigRequest { SmtpPassword = "new-fake", ClearSmtpPassword = true });

        Assert.Equal("new-fake", _config.SmtpPassword);
    }

    [Fact]
    public void PostConfig_EmptySecret_KeepsStored()
    {
        _config.SmtpHost = "smtp.example";
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

    [Fact]
    public void SetConfig_SmtpHostChanges_WithoutPassword_IsRefused_AndNothingIsStored()
    {
        _config.SmtpHost = "smtp.example";
        var before = JsonSerializer.Serialize(_config);

        var result = Assert.IsType<ContentResult>(_controller.SetConfig(new AdminConfigRequest { SmtpHost = "other.example", PinMinutes = 33 }));

        Assert.Equal(400, result.StatusCode);
        Assert.Contains("smtp_server_changed", result.Content!, StringComparison.Ordinal);
        Assert.Equal(before, JsonSerializer.Serialize(_config));
        Assert.Equal(0, _saves);
    }

    [Fact]
    public void SetConfig_SmtpHostChanges_WithANewPassword_IsSaved()
    {
        _config.SmtpHost = "smtp.example";

        var result = Assert.IsType<ContentResult>(_controller.SetConfig(new AdminConfigRequest { SmtpHost = "other.example", SmtpPassword = "typed-again" }));

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("other.example", _config.SmtpHost);
        Assert.Equal("typed-again", _config.SmtpPassword);
    }

    [Fact]
    public void SetConfig_SmtpHostChanges_WithClear_IsSavedWithoutPassword()
    {
        _config.SmtpHost = "smtp.example";

        var result = Assert.IsType<ContentResult>(_controller.SetConfig(new AdminConfigRequest { SmtpHost = "other.example", ClearSmtpPassword = true }));

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("other.example", _config.SmtpHost);
        Assert.Equal(string.Empty, _config.SmtpPassword);
    }

    [Theory]
    [InlineData("SMTP.Example")]
    [InlineData("  smtp.example  ")]
    public void SetConfig_SmtpHostCaseOrSpaceOnlyChange_IsSaved_AndThePasswordIsKept(string host)
    {
        _config.SmtpHost = "smtp.example";

        var result = Assert.IsType<ContentResult>(_controller.SetConfig(new AdminConfigRequest { SmtpHost = host, SmtpPort = 465 }));

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("smtp-secret", _config.SmtpPassword);
        Assert.Equal(465, _config.SmtpPort);
    }

    [Fact]
    public void SetConfig_SmtpHostChanges_WithNoStoredPassword_IsSaved()
    {
        _config.SmtpHost = "smtp.example";
        _config.SmtpPassword = string.Empty;

        Assert.Equal(200, Assert.IsType<ContentResult>(_controller.SetConfig(new AdminConfigRequest { SmtpHost = "other.example" })).StatusCode);
    }

    [Theory]
    [InlineData("https://other.example/", true)]
    [InlineData("http://seerr.example/", true)]
    [InlineData("https://seerr.example:8443/", true)]
    [InlineData("https://seerr.example/sub/path", false)]
    [InlineData("HTTPS://SEERR.example/", false)]
    public void SetConfig_SeerrAddressChanges_WithoutKey_IsRefusedOnlyWhenSchemeHostOrPortChange(string url, bool refused)
    {
        _config.SeerrUrl = "https://seerr.example/";

        var result = Assert.IsType<ContentResult>(_controller.SetConfig(new AdminConfigRequest { SeerrUrl = url }));

        if (refused)
        {
            Assert.Equal(400, result.StatusCode);
            Assert.Contains("seerr_address_changed", result.Content!, StringComparison.Ordinal);
            Assert.Equal("https://seerr.example/", _config.SeerrUrl);
        }
        else
        {
            Assert.Equal(200, result.StatusCode);
            Assert.Equal("seerr-secret", _config.SeerrApiKey);
        }
    }

    [Fact]
    public void SetConfig_SeerrAddressChanges_WithAKey_IsSaved()
    {
        _config.SeerrUrl = "https://seerr.example/";

        var result = Assert.IsType<ContentResult>(_controller.SetConfig(new AdminConfigRequest { SeerrUrl = "https://other.example/", SeerrApiKey = "typed-again" }));

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("typed-again", _config.SeerrApiKey);
    }

    [Theory]
    [InlineData("StartTls", "None", null, false, false, "smtp_encryption_off")]
    [InlineData("SslOnConnect", "None", null, false, false, "smtp_encryption_off")]
    [InlineData("StartTls", "None", "typed-again", false, true, null)]
    [InlineData("StartTls", "None", null, true, true, null)]
    [InlineData("None", "None", null, false, true, null)]
    [InlineData("StartTls", "SslOnConnect", null, false, true, null)]
    [InlineData("None", "StartTls", null, false, true, null)]
    public void SetConfig_SmtpSecurityChanges_AsksForThePasswordAgainOnlyWhenEncryptionIsTurnedOff(string stored, string posted, string? password, bool clear, bool saved, string? code)
    {
        _config.SmtpSecurity = Enum.Parse<SmtpSecurity>(stored);
        var before = JsonSerializer.Serialize(_config);

        var result = Assert.IsType<ContentResult>(_controller.SetConfig(new AdminConfigRequest { SmtpSecurity = posted, SmtpPassword = password, ClearSmtpPassword = clear ? true : null, PinMinutes = 33 }));

        if (saved)
        {
            Assert.Equal(200, result.StatusCode);
            Assert.Equal(Enum.Parse<SmtpSecurity>(posted), _config.SmtpSecurity);
            Assert.Equal(33, _config.PinMinutes);
            Assert.Equal(clear ? string.Empty : password ?? "smtp-secret", _config.SmtpPassword);
        }
        else
        {
            Assert.Equal(400, result.StatusCode);
            Assert.Contains(code!, result.Content!, StringComparison.Ordinal);
            Assert.Equal(before, JsonSerializer.Serialize(_config));
            Assert.Equal(0, _saves);
        }
    }

    [Fact]
    public void SetConfig_SmtpSecurityTurnedOff_WithNoStoredPassword_IsSaved()
    {
        _config.SmtpSecurity = SmtpSecurity.StartTls;
        _config.SmtpPassword = string.Empty;

        var result = Assert.IsType<ContentResult>(_controller.SetConfig(new AdminConfigRequest { SmtpSecurity = "None" }));

        Assert.Equal(200, result.StatusCode);
        Assert.Equal(SmtpSecurity.None, _config.SmtpSecurity);
    }

    [Fact]
    public void SetConfig_HostChangedAndEncryptionTurnedOff_GivesTheHostCode()
    {
        _config.SmtpHost = "smtp.example";
        _config.SmtpSecurity = SmtpSecurity.StartTls;

        var result = Assert.IsType<ContentResult>(_controller.SetConfig(new AdminConfigRequest { SmtpHost = "other.example", SmtpSecurity = "None" }));

        Assert.Equal(400, result.StatusCode);
        Assert.Contains("smtp_server_changed", result.Content!, StringComparison.Ordinal);
    }

    private static readonly JsonSerializerOptions BinderJson = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("{\"smtpHost\":\"x\",\"smtpPort\":25,\"smtpSecurity\":\"None\",\"fromAddress\":\"a@example.org\"}")]
    [InlineData("{\"seerrEnabled\":true,\"seerrUrl\":\"https://seerr.example\"}")]
    public void PostConfig_WithOnlySmtpKeys_KeepsEnabledAndCheckboxes(string json)
    {
        // The body goes through the same JSON deserialization as the MVC binder (camelCase, case-insensitive).
        var request = JsonSerializer.Deserialize<AdminConfigRequest>(json, BinderJson);
        _config.SmtpHost = "x";
        _config.SmtpSecurity = SmtpSecurity.None;
        _config.SeerrUrl = "https://seerr.example";
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
        _config.SmtpHost = "x";

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
    public async Task Status_ReportsServiceOpen()
    {
        Assert.True((await StatusRoot()).GetProperty("serviceOpen").GetBoolean());

        _config.Enabled = false;
        Assert.False((await StatusRoot()).GetProperty("serviceOpen").GetBoolean());

        _config.Enabled = true;
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("10.0.0.2");
        ctx.Request.Headers["X-Forwarded-For"] = "203.0.113.5";
        _guard.Observe(ctx);
        Assert.False((await StatusRoot()).GetProperty("serviceOpen").GetBoolean());
    }

    [Fact]
    public void GetConfig_ReturnsEmailLogin()
    {
        _config.EmailLogin = true;

        var root = JsonDocument.Parse(Assert.IsType<ContentResult>(_controller.GetConfig()).Content!).RootElement;

        Assert.True(root.GetProperty("emailLogin").GetBoolean());
    }

    [Fact]
    public void SetConfig_SavesEmailLogin()
    {
        var result = Assert.IsType<ContentResult>(_controller.SetConfig(new AdminConfigRequest { EmailLogin = true }));

        Assert.Equal(200, result.StatusCode);
        Assert.True(_config.EmailLogin);
        Assert.Equal(1, _saves);

        _controller.SetConfig(new AdminConfigRequest { EmailLogin = false });
        Assert.False(_config.EmailLogin);
    }

    [Fact]
    public void SetConfig_WithoutEmailLogin_KeepsIt()
    {
        _config.EmailLogin = true;

        _controller.SetConfig(new AdminConfigRequest { SmtpHost = "smtp.example" });

        Assert.True(_config.EmailLogin);
    }

    [Fact]
    public async Task Status_CountsSharedConfirmedAddresses()
    {
        var when = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
        await _store.WriteAsync(d =>
        {
            // Two accounts on one confirmed address (case differs): counts once.
            d.Emails.Add(new UserEmail(Guid.NewGuid(), "shared@example.org", null, "en", when));
            d.Emails.Add(new UserEmail(Guid.NewGuid(), "Shared@Example.org", null, "en", when));

            // A pending duplicate elsewhere, an unconfirmed address and a single owner: none counts.
            d.Emails.Add(new UserEmail(Guid.NewGuid(), "alone@example.org", "shared@example.org", "en", when));
            d.Emails.Add(new UserEmail(Guid.NewGuid(), null, "alone@example.org", "en", null));
            d.Emails.Add(new UserEmail(Guid.NewGuid(), "unconfirmed@example.org", null, "en", null));
            d.Emails.Add(new UserEmail(Guid.NewGuid(), "unconfirmed@example.org", null, "en", null));
        });

        var root = await StatusRoot();

        Assert.Equal(1, root.GetProperty("sharedEmailCount").GetInt32());
        Assert.DoesNotContain("example.org", root.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_ReportsEmailLoginAvailability()
    {
        Assert.True((await StatusRoot()).GetProperty("emailLoginAvailable").GetBoolean());

        _probe = Probe(available: false);
        Rebuild();

        Assert.False((await StatusRoot()).GetProperty("emailLoginAvailable").GetBoolean());
    }

    [Fact]
    public async Task Status_CountsNothing_WhenNoOneSharesAnAddress()
    {
        Assert.Equal(0, (await StatusRoot()).GetProperty("sharedEmailCount").GetInt32());
    }

    [Fact]
    public async Task Status_StoreReadFails_ReportsNoSharedAddress_AndTheRestIntact()
    {
        // An unreadable data file: the store read throws.
        await File.WriteAllTextAsync(Path.Combine(_dir, "mailconcierge.json"), "{\"Emails\":[{\"Email\":\"alice@example.org\"", TestContext.Current.CancellationToken);
        var logger = new ListLogger();
        _controller = new AdminController(_users.Object, _queue, _guard, new ServiceGate(() => _config, _guard), NewEmailService(_queue), _probe, () => _config, _ => _saves++, logger)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        var result = Assert.IsType<ContentResult>(await _controller.Status());

        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
        var root = JsonDocument.Parse(result.Content!).RootElement;
        Assert.Equal(0, root.GetProperty("sharedEmailCount").GetInt32());
        Assert.Equal(0, root.GetProperty("sharedEmailAccounts").GetArrayLength());
        Assert.False(root.GetProperty("proxyGuardTripped").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("reason").ValueKind);
        Assert.Equal(_config.CanSendEmail(), root.GetProperty("canSendEmail").GetBoolean());
        Assert.True(root.GetProperty("serviceOpen").GetBoolean());
        Assert.True(root.GetProperty("emailLoginAvailable").GetBoolean());
        var line = Assert.Single(logger.Lines);
        Assert.Contains(nameof(JsonException), line, StringComparison.Ordinal);
        Assert.DoesNotContain("example.org", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alice", line, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Status_NamesTheAccountsOfEachSharedAddress_SortedAndWithoutAnyAddress()
    {
        var when = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
        var zoe = KnownAccount("Zoe");
        var bob = KnownAccount("bob");
        var alice = KnownAccount("Alice");
        var carol = KnownAccount("carol");
        var dave = KnownAccount("dave");
        await _store.WriteAsync(d =>
        {
            d.Emails.Add(new UserEmail(zoe, "one@example.org", null, "en", when));
            d.Emails.Add(new UserEmail(bob, "ONE@example.org", null, "en", when));
            d.Emails.Add(new UserEmail(carol, "two@example.org", null, "en", when));
            d.Emails.Add(new UserEmail(alice, "two@example.org", null, "en", when));
            d.Emails.Add(new UserEmail(dave, "alone@example.org", null, "en", when));
        });

        var root = await StatusRoot();

        Assert.Equal(2, root.GetProperty("sharedEmailCount").GetInt32());
        var groups = root.GetProperty("sharedEmailAccounts").EnumerateArray().Select(g => g.EnumerateArray().Select(n => n.GetString()).ToArray()).ToArray();
        Assert.Equal(new[] { new[] { "Alice", "carol" }, new[] { "bob", "Zoe" } }, groups);
        Assert.DoesNotContain("example.org", root.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_CapsTheSharedAccountsAtTwentyEntriesAndTenNames_AndKeepsTheFullCount()
    {
        var when = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
        await _store.WriteAsync(d =>
        {
            for (var g = 0; g < 25; g++)
            {
                for (var n = 0; n < 12; n++)
                {
                    d.Emails.Add(new UserEmail(KnownAccount($"g{g:D2}n{n:D2}"), $"shared{g}@example.org", null, "en", when));
                }
            }
        });

        var root = await StatusRoot();

        Assert.Equal(25, root.GetProperty("sharedEmailCount").GetInt32());
        var groups = root.GetProperty("sharedEmailAccounts").EnumerateArray().ToArray();
        Assert.Equal(20, groups.Length);
        Assert.All(groups, g => Assert.Equal(10, g.GetArrayLength()));
        Assert.Equal("g00n00", groups[0][0].GetString());
        Assert.Equal("g19n09", groups[19][9].GetString());
    }

    [Fact]
    public async Task Status_LeavesOutAnIdWithoutAnAccount()
    {
        var when = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
        var alice = KnownAccount("alice");
        var bob = KnownAccount("bob");
        await _store.WriteAsync(d =>
        {
            d.Emails.Add(new UserEmail(alice, "one@example.org", null, "en", when));
            d.Emails.Add(new UserEmail(Guid.NewGuid(), "one@example.org", null, "en", when));
            d.Emails.Add(new UserEmail(bob, "one@example.org", null, "en", when));
        });

        var root = await StatusRoot();

        Assert.Equal(1, root.GetProperty("sharedEmailCount").GetInt32());
        var entry = Assert.Single(root.GetProperty("sharedEmailAccounts").EnumerateArray());
        Assert.Equal(new[] { "alice", "bob" }, entry.EnumerateArray().Select(n => n.GetString()).ToArray());
    }

    [Fact]
    public async Task Status_NoSharedAddress_GivesAnEmptyAccountList()
    {
        var root = await StatusRoot();

        Assert.Equal(JsonValueKind.Array, root.GetProperty("sharedEmailAccounts").ValueKind);
        Assert.Equal(0, root.GetProperty("sharedEmailAccounts").GetArrayLength());
    }

    private Guid KnownAccount(string name)
    {
        var user = new User(name, AccountPolicy.DefaultAuthProviderId, "reset") { Id = Guid.NewGuid() };
        _users.Setup(u => u.GetUserById(user.Id)).Returns(user);
        return user.Id;
    }

    private async Task<JsonElement> StatusRoot() => JsonDocument.Parse(Assert.IsType<ContentResult>(await _controller.Status()).Content!).RootElement;

    [Fact]
    public async Task Status_ReportsCodeNotSentence()
    {
        var ok = await StatusRoot();
        Assert.False(ok.GetProperty("proxyGuardTripped").GetBoolean());
        Assert.Equal(JsonValueKind.Null, ok.GetProperty("reason").ValueKind);

        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("10.0.0.2");
        ctx.Request.Headers["X-Forwarded-For"] = "203.0.113.5";
        _guard.Observe(ctx);

        var tripped = await StatusRoot();
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
        var controller = new AdminController(_users.Object, queue, _guard, new ServiceGate(() => _config, _guard), NewEmailService(queue), _probe, () => _config, _ => { }, logger.Object)
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

    private static EmailLoginProbe Probe(bool available)
    {
        var actions = available
            ? new[]
            {
                new ControllerActionDescriptor
                {
                    ControllerName = EmailLoginProbe.ControllerName,
                    ActionName = EmailLoginProbe.ActionName,
                    Parameters = new List<ParameterDescriptor> { new() { Name = "request", ParameterType = typeof(SignInRequest) } },
                },
            }
            : Array.Empty<ControllerActionDescriptor>();
        var provider = new Mock<IActionDescriptorCollectionProvider>();
        provider.SetupGet(p => p.ActionDescriptors).Returns(new ActionDescriptorCollection(actions, 1));
        return new EmailLoginProbe(provider.Object);
    }

    private static User NewUser(string name, string provider, bool admin)
    {
        var user = new User(name, provider, "Jellyfin.Server.Implementations.Users.DefaultPasswordResetProvider");
        user.SetPermission(PermissionKind.IsAdministrator, admin);
        return user;
    }

    private EmailService NewEmailService(EmailQueue queue) => new(
        _store,
        new TokenService(_dir),
        new RateLimiter(_time),
        queue,
        _users.Object,
        new SessionCloser(Mock.Of<IDeviceManager>(), Mock.Of<ISessionManager>(), _time, NullLogger<SessionCloser>.Instance),
        Mock.Of<ISeerrSync>(),
        Mock.Of<IServerApplicationHost>(),
        () => _config,
        new ServiceGate(() => _config, _guard),
        _time,
        NullLogger<EmailService>.Instance);

    private void Rebuild()
    {
        _controller = new AdminController(_users.Object, _queue, _guard, new ServiceGate(() => _config, _guard), NewEmailService(_queue), _probe, () => _config, _ => _saves++, NullLogger<AdminController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    public sealed class SignInRequest
    {
        public string? Username { get; set; }
    }

    private sealed class ListLogger : ILogger<AdminController>
    {
        private readonly List<string> _lines = new();

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_lines)
                {
                    return _lines.ToList();
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_lines)
            {
                _lines.Add(formatter(state, exception) + " " + exception);
            }
        }
    }
}

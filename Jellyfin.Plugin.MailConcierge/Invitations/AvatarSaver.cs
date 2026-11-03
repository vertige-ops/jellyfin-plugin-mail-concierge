using System;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MailConcierge.Invitations;

/// <summary>
/// Sets the profile picture of a user through Jellyfin's own services, the way its <c>Users/{id}/Images/Primary</c> endpoint does.
/// </summary>
public class AvatarSaver
{
    private readonly IUserManager _users;
    private readonly IProviderManager _providers;
    private readonly IServerConfigurationManager _configuration;
    private readonly ILogger<AvatarSaver> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AvatarSaver"/> class.
    /// </summary>
    /// <param name="users">The user manager.</param>
    /// <param name="providers">The provider manager, which writes the image file.</param>
    /// <param name="configuration">The server configuration, for the user data path.</param>
    /// <param name="logger">The logger.</param>
    public AvatarSaver(IUserManager users, IProviderManager providers, IServerConfigurationManager configuration, ILogger<AvatarSaver> logger)
    {
        _users = users;
        _providers = providers;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Sets the primary image of a user.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="bytes">The image, already validated.</param>
    /// <param name="mimeType">Its MIME type.</param>
    /// <returns>True when the image was saved, false on any failure (which is logged without the content).</returns>
    public virtual async Task<bool> SaveAsync(Guid userId, byte[] bytes, string mimeType)
    {
        try
        {
            var user = _users.GetUserById(userId);
            if (user is null)
            {
                return false;
            }

            var extension = mimeType switch
            {
                "image/png" => ".png",
                "image/jpeg" => ".jpg",
                "image/webp" => ".webp",
                _ => null,
            };
            if (extension is null)
            {
                return false;
            }

            var directory = Path.Combine(_configuration.ApplicationPaths.UserConfigurationDirectoryPath, user.Username);
            if (user.ProfileImage is not null)
            {
                await _users.ClearProfileImageAsync(user).ConfigureAwait(false);
            }

            user.ProfileImage = new ImageInfo(Path.Combine(directory, "profile" + extension));
            using var stream = new MemoryStream(bytes);
            await _providers.SaveImage(stream, mimeType, user.ProfileImage.Path).ConfigureAwait(false);
            await _users.UpdateUserAsync(user).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Saving the profile picture of user {UserId} failed ({ErrorType})", userId, ex.GetType().Name);
            return false;
        }
    }
}

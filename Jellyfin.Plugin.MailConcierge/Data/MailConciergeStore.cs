using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.MailConcierge.Data;

/// <summary>
/// JSON file store kept in the plugin data folder. All access is serialized.
/// </summary>
public sealed class MailConciergeStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;
    private readonly string _tempPath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private MailConciergeData? _data;

    /// <summary>
    /// Initializes a new instance of the <see cref="MailConciergeStore"/> class.
    /// </summary>
    /// <param name="dataFolder">The plugin data folder.</param>
    public MailConciergeStore(string dataFolder)
    {
        _path = Path.Combine(dataFolder, "mailconcierge.json");
        _tempPath = _path + ".tmp";
    }

    /// <summary>Reads data under the store lock.</summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="read">The read function. It must not retain or mutate the data.</param>
    /// <returns>The result of <paramref name="read"/>.</returns>
    public async Task<T> ReadAsync<T>(Func<MailConciergeData, T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            return read(await LoadAsync().ConfigureAwait(false));
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Mutates data under the store lock and persists it atomically.</summary>
    /// <param name="write">The mutation.</param>
    /// <returns>A task that completes once the data is saved.</returns>
    public async Task WriteAsync(Action<MailConciergeData> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var data = await LoadAsync().ConfigureAwait(false);
            try
            {
                write(data);
                await SaveAsync(data).ConfigureAwait(false);
            }
            catch
            {
                // Drop the possibly half-mutated cache; the next access reloads from disk.
                _data = null;
                throw;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Removes expired tokens and marks overdue pending invitations as expired.</summary>
    /// <param name="now">The current time (UTC).</param>
    /// <returns>A task that completes once the data is saved.</returns>
    public Task PurgeExpiredAsync(DateTime now) => WriteAsync(data =>
    {
        data.Tokens.RemoveAll(t => t.ExpiresAt <= now);
        for (var i = 0; i < data.Invitations.Count; i++)
        {
            var invitation = data.Invitations[i];
            if (invitation.State == InvitationState.Pending && invitation.ExpiresAt <= now)
            {
                data.Invitations[i] = invitation with { State = InvitationState.Expired };
            }
        }
    });

    /// <inheritdoc />
    public void Dispose() => _lock.Dispose();

    private async Task<MailConciergeData> LoadAsync()
    {
        if (_data is not null)
        {
            return _data;
        }

        if (File.Exists(_path))
        {
            var stream = File.OpenRead(_path);
            await using (stream.ConfigureAwait(false))
            {
                _data = await JsonSerializer.DeserializeAsync<MailConciergeData>(stream, JsonOptions).ConfigureAwait(false);
            }
        }

        _data ??= new MailConciergeData();
        return _data;
    }

    private async Task SaveAsync(MailConciergeData data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.Delete(_tempPath);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        var stream = new FileStream(_tempPath, options);
        await using (stream.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(stream, data, JsonOptions).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
#pragma warning disable CA1849 // No async overload takes flushToDisk.
            stream.Flush(true);
#pragma warning restore CA1849
        }

        File.Move(_tempPath, _path, true);
    }
}

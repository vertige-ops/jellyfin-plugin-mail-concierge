using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;

namespace Jellyfin.Plugin.MailConcierge.Security;

/// <summary>
/// Generates tokens and PINs and hashes them with a per-install secret key.
/// </summary>
public sealed class TokenService
{
    private const int KeyLength = 32;

    private static readonly Regex PinPattern = new(@"^\d{4}-?\d{4}$", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private readonly string _keyPath;
    private readonly object _keyLock = new();
    private byte[]? _key;

    /// <summary>
    /// Initializes a new instance of the <see cref="TokenService"/> class.
    /// </summary>
    /// <param name="dataFolder">The plugin data folder holding <c>mailconcierge.key</c>.</param>
    public TokenService(string dataFolder)
    {
        _keyPath = Path.Combine(dataFolder, "mailconcierge.key");
    }

    /// <summary>Creates a token: 32 random bytes, base64url (43 characters).</summary>
    /// <returns>The token.</returns>
    public string NewToken() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    /// <summary>Creates an 8 digit PIN formatted <c>1234-5678</c>.</summary>
    /// <returns>The PIN.</returns>
    public string NewPin()
    {
        var n = RandomNumberGenerator.GetInt32(0, 100_000_000);
        var digits = n.ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
        return string.Concat(digits.AsSpan(0, 4), "-", digits.AsSpan(4));
    }

    /// <summary>Hashes a token or PIN (the PIN dash is ignored) with HMAC-SHA256.</summary>
    /// <param name="value">The clear value.</param>
    /// <returns>The base64url hash.</returns>
    public string Hash(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var normalized = PinPattern.IsMatch(value) ? value.Replace("-", string.Empty, StringComparison.Ordinal) : value;
        var bytes = HMACSHA256.HashData(GetKey(), Encoding.UTF8.GetBytes(normalized));
        return WebEncoders.Base64UrlEncode(bytes);
    }

    /// <summary>Compares a clear value with a stored hash in constant time.</summary>
    /// <param name="value">The clear value.</param>
    /// <param name="hash">The stored hash.</param>
    /// <returns>True if they match.</returns>
    public bool Matches(string value, string hash)
    {
        ArgumentNullException.ThrowIfNull(hash);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(Hash(value)),
            Encoding.ASCII.GetBytes(hash));
    }

    private byte[] GetKey()
    {
        if (_key is not null)
        {
            return _key;
        }

        lock (_keyLock)
        {
            return _key ??= LoadOrCreateKey();
        }
    }

    private byte[] LoadOrCreateKey()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_keyPath)!);
        if (File.Exists(_keyPath))
        {
            return ReadKey();
        }

        var temp = _keyPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        var key = RandomNumberGenerator.GetBytes(KeyLength);
        try
        {
            using (var stream = new FileStream(temp, options))
            {
                stream.Write(key);
                stream.Flush(true);
            }

            File.Move(temp, _keyPath, false);
            return key;
        }
        catch (IOException) when (File.Exists(_keyPath))
        {
            // Another instance created it first: use the winner.
            return ReadKey();
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private byte[] ReadKey()
    {
        var key = File.ReadAllBytes(_keyPath);
        if (key.Length != KeyLength)
        {
            throw new InvalidDataException("mailconcierge.key is corrupt: expected " + KeyLength + " bytes.");
        }

        return key;
    }
}

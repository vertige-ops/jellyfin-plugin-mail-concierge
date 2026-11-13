using System;

namespace Jellyfin.Plugin.MailConcierge.Invitations;

/// <summary>
/// Validation of the optional profile picture sent with an invitation acceptance.
/// </summary>
public static class AvatarImage
{
    /// <summary>
    /// The largest accepted image, in bytes once decoded.
    /// </summary>
    public const int MaxBytes = 1_048_576;

    /// <summary>
    /// The largest accepted width or height, in pixels. The browser sends 256 px at most, so this leaves a wide margin.
    /// </summary>
    public const int MaxSide = 1024;

    /// <summary>
    /// The largest accepted number of pixels.
    /// </summary>
    public const int MaxPixels = 1_048_576;

    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>
    /// Checks an image without decoding it: PNG, JPEG or WebP, at most <see cref="MaxBytes"/>, whose header is well formed and
    /// declares at most <see cref="MaxSide"/> pixels on a side and <see cref="MaxPixels"/> in all. Animated images (APNG,
    /// animated WebP) and truncated or malformed containers are refused. The image is stored as sent, not re-encoded.
    /// </summary>
    /// <param name="bytes">The image.</param>
    /// <param name="mimeType">The MIME type when valid, otherwise empty.</param>
    /// <returns>True when the image is accepted.</returns>
    public static bool TryValidate(byte[] bytes, out string mimeType)
    {
        mimeType = string.Empty;
        if (bytes is null || bytes.Length == 0 || bytes.Length > MaxBytes)
        {
            return false;
        }

        var span = bytes.AsSpan();
        (int Width, int Height)? size = null;
        string type = string.Empty;
        if (span.StartsWith(PngSignature))
        {
            type = "image/png";
            size = ReadPng(span);
        }
        else if (span.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }))
        {
            type = "image/jpeg";

            // A complete JPEG ends with the end-of-image marker.
            size = span.Length >= 4 && span[^2] == 0xFF && span[^1] == 0xD9 ? ReadJpeg(span) : null;
        }
        else if (span.Length >= 12 && span[..4].SequenceEqual("RIFF"u8) && span.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            type = "image/webp";
            size = ReadWebp(span);
        }

        if (size is not { } dims || dims.Width < 1 || dims.Height < 1 || dims.Width > MaxSide || dims.Height > MaxSide
            || (long)dims.Width * dims.Height > MaxPixels)
        {
            return false;
        }

        mimeType = type;
        return true;
    }

    /// <summary>
    /// Decodes and validates a base64 image. Anything wrong means no avatar.
    /// </summary>
    /// <param name="base64">The base64 text, without a data-URL prefix.</param>
    /// <param name="bytes">The decoded image when valid.</param>
    /// <param name="mimeType">The MIME type when valid.</param>
    /// <returns>True when there is a valid image.</returns>
    public static bool TryDecode(string? base64, out byte[] bytes, out string mimeType)
    {
        bytes = Array.Empty<byte>();
        mimeType = string.Empty;
        if (string.IsNullOrWhiteSpace(base64) || base64.Length > (MaxBytes / 3 * 4) + 8)
        {
            return false;
        }

        var buffer = new byte[(base64.Length / 4 * 3) + 3];
        if (!Convert.TryFromBase64String(base64.Trim(), buffer, out var written))
        {
            return false;
        }

        var decoded = buffer.AsSpan(0, written).ToArray();
        if (!TryValidate(decoded, out mimeType))
        {
            return false;
        }

        bytes = decoded;
        return true;
    }

    private static (int, int)? ReadPng(ReadOnlySpan<byte> d)
    {
        // Chunks: length (4, big endian), type (4), data, CRC (4). IHDR must come first; acTL before IDAT means an animation.
        // The file must be complete: every chunk fits, there is image data, and IEND closes it.
        var pos = 8;
        var first = true;
        var sawData = false;
        while (pos + 12 <= d.Length)
        {
            var length = (long)BigEndian32(d, pos);
            var type = d.Slice(pos + 4, 4);
            if (pos + 12L + length > d.Length)
            {
                return null;
            }

            if (first && (!type.SequenceEqual("IHDR"u8) || length != 13))
            {
                return null;
            }

            if (type.SequenceEqual("acTL"u8))
            {
                return null;
            }

            if (type.SequenceEqual("IDAT"u8))
            {
                sawData = true;
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                return sawData ? Ihdr(d) : null;
            }

            first = false;
            pos = (int)(pos + 12L + length);
        }

        return null;
    }

    private static (int, int)? Ihdr(ReadOnlySpan<byte> d)
    {
        var width = BigEndian32(d, 16);
        var height = BigEndian32(d, 20);
        return width is 0 or > int.MaxValue || height is 0 or > int.MaxValue ? null : ((int)width, (int)height);
    }

    private static (int, int)? ReadJpeg(ReadOnlySpan<byte> d)
    {
        var pos = 2;
        (int, int)? frame = null;
        while (pos + 1 < d.Length)
        {
            if (d[pos] != 0xFF)
            {
                return null;
            }

            while (pos + 1 < d.Length && d[pos + 1] == 0xFF)
            {
                pos++;
            }

            if (pos + 1 >= d.Length)
            {
                return null;
            }

            var marker = d[pos + 1];
            pos += 2;
            if (marker is 0x01 or (>= 0xD0 and <= 0xD7))
            {
                continue;
            }

            if (marker is 0xD9 or 0x00 or 0xD8)
            {
                // End of image before the scan, or a stray marker: malformed.
                return null;
            }

            if (pos + 2 > d.Length)
            {
                return null;
            }

            var length = (d[pos] << 8) | d[pos + 1];
            if (length < 2 || pos + length > d.Length)
            {
                return null;
            }

            if (marker == 0xDA)
            {
                // Start of the scan: the headers are over, exactly one frame header must have been met.
                return frame;
            }

            // SOF0..SOF15, except DHT (C4), JPG (C8) and DAC (CC).
            if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
            {
                if (frame is not null || length < 8)
                {
                    return null;
                }

                // Bounds: length >= 8 and pos + length <= d.Length, so pos + 7 (Nf) is inside the segment.
                var height = (d[pos + 3] << 8) | d[pos + 4];
                var width = (d[pos + 5] << 8) | d[pos + 6];
                var components = d[pos + 7];
                if (width == 0 || height == 0 || components is < 1 or > 4 || length != 8 + (3 * components))
                {
                    return null;
                }

                frame = (width, height);
            }

            pos += length;
        }

        return null;
    }

    private static (int, int)? ReadWebp(ReadOnlySpan<byte> d)
    {
        // RIFF size (little endian) counts the bytes after the first 8; it must fit in the file.
        var riff = LittleEndian32(d, 4);
        if (riff < 4 || riff + 8L > d.Length)
        {
            return null;
        }

        var end = (int)(riff + 8);
        var pos = 12;
        (int, int)? canvas = null;
        (int, int)? bitstream = null;
        while (pos + 8 <= end)
        {
            var type = d.Slice(pos, 4);
            var length = LittleEndian32(d, pos + 4);
            var data = pos + 8;
            if (data + (long)length > end)
            {
                return null;
            }

            if (type.SequenceEqual("ANIM"u8) || type.SequenceEqual("ANMF"u8))
            {
                return null;
            }

            if (type.SequenceEqual("VP8X"u8))
            {
                if (length < 10 || (d[data] & 0x02) != 0 || canvas is not null || bitstream is not null)
                {
                    return null;
                }

                canvas = ((int)(LittleEndian24(d, data + 4) + 1), (int)(LittleEndian24(d, data + 7) + 1));
            }
            else if (type.SequenceEqual("VP8 "u8))
            {
                if (length < 10 || d[data + 3] != 0x9D || d[data + 4] != 0x01 || d[data + 5] != 0x2A || (d[data] & 0x01) != 0)
                {
                    return null;
                }

                bitstream = ((d[data + 6] | (d[data + 7] << 8)) & 0x3FFF, (d[data + 8] | (d[data + 9] << 8)) & 0x3FFF);
            }
            else if (type.SequenceEqual("VP8L"u8))
            {
                if (length < 5 || d[data] != 0x2F)
                {
                    return null;
                }

                var bits = LittleEndian32(d, data + 1);
                bitstream = ((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1);
            }

            if (bitstream is not null)
            {
                break;
            }

            pos = data + (int)length + (int)(length & 1);
        }

        if (bitstream is not { } bs)
        {
            return null;
        }

        // With a VP8X header the canvas must be the size of the bitstream; the larger of the two is checked either way.
        return canvas is { } c ? (Math.Max(c.Item1, bs.Item1), Math.Max(c.Item2, bs.Item2)) : bs;
    }

    private static uint BigEndian32(ReadOnlySpan<byte> d, int at)
        => ((uint)d[at] << 24) | ((uint)d[at + 1] << 16) | ((uint)d[at + 2] << 8) | d[at + 3];

    private static uint LittleEndian32(ReadOnlySpan<byte> d, int at)
        => d[at] | ((uint)d[at + 1] << 8) | ((uint)d[at + 2] << 16) | ((uint)d[at + 3] << 24);

    private static uint LittleEndian24(ReadOnlySpan<byte> d, int at)
        => d[at] | ((uint)d[at + 1] << 8) | ((uint)d[at + 2] << 16);
}

using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.MailConcierge.Invitations;
using Xunit;

namespace Jellyfin.Plugin.MailConcierge.Tests.Invitations;

public sealed class AvatarImageTests
{
    [Fact]
    public void ValidPng_Passes()
    {
        Assert.True(AvatarImage.TryValidate(Png(256, 256), out var mime));
        Assert.Equal("image/png", mime);
    }

    [Fact]
    public void ValidJpeg_Passes()
    {
        Assert.True(AvatarImage.TryValidate(Jpeg(256, 200), out var mime));
        Assert.Equal("image/jpeg", mime);
    }

    [Theory]
    [InlineData("VP8 ")]
    [InlineData("VP8L")]
    [InlineData("VP8X")]
    public void ValidWebp_Passes(string kind)
    {
        Assert.True(AvatarImage.TryValidate(Webp(kind, 300, 150), out var mime));
        Assert.Equal("image/webp", mime);
    }

    [Fact]
    public void ProgressiveJpeg_Passes()
        => Assert.True(AvatarImage.TryValidate(Jpeg(256, 256, sof: 0xC2), out _));

    [Fact]
    public void Jpeg_WithExtraSegmentsBeforeFrameHeader_Passes()
        => Assert.True(AvatarImage.TryValidate(Jpeg(100, 100, withApp: true), out _));

    [Fact]
    public void Gif_Fails()
        => Assert.False(AvatarImage.TryValidate(System.Text.Encoding.ASCII.GetBytes("GIF89a\x01\x00\x01\x00"), out _));

    [Fact]
    public void HtmlRenamedPng_Fails()
        => Assert.False(AvatarImage.TryValidate(System.Text.Encoding.ASCII.GetBytes("<html><script>alert(1)</script></html>"), out _));

    [Fact]
    public void Empty_Fails()
        => Assert.False(AvatarImage.TryValidate(Array.Empty<byte>(), out _));

    [Fact]
    public void RiffThatIsNotWebp_Fails()
        => Assert.False(AvatarImage.TryValidate(new byte[] { 0x52, 0x49, 0x46, 0x46, 1, 2, 3, 4, 0x57, 0x41, 0x56, 0x45 }, out _));

    [Fact]
    public void BareSignatures_AreMalformed()
    {
        Assert.False(AvatarImage.TryValidate(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 }, out _));
        Assert.False(AvatarImage.TryValidate(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46 }, out _));
        Assert.False(AvatarImage.TryValidate(new byte[] { 0x52, 0x49, 0x46, 0x46, 1, 2, 3, 4, 0x57, 0x45, 0x42, 0x50, 0x56, 0x50 }, out _));
    }

    [Theory]
    [InlineData(1024, 1024, true)]
    [InlineData(1025, 10, false)]
    [InlineData(10, 1025, false)]
    [InlineData(65535, 65535, false)]
    [InlineData(0, 10, false)]
    [InlineData(10, 0, false)]
    public void Png_DimensionLimits(int width, int height, bool accepted)
        => Assert.Equal(accepted, AvatarImage.TryValidate(Png(width, height), out _));

    [Theory]
    [InlineData(1024, 1024, true)]
    [InlineData(1025, 10, false)]
    [InlineData(65535, 65535, false)]
    [InlineData(10, 0, false)]
    public void Jpeg_DimensionLimits(int width, int height, bool accepted)
        => Assert.Equal(accepted, AvatarImage.TryValidate(Jpeg(width, height), out _));

    [Theory]
    [InlineData("VP8 ", 1024, 1024, true)]
    [InlineData("VP8 ", 1025, 10, false)]
    [InlineData("VP8L", 1024, 1024, true)]
    [InlineData("VP8L", 16384, 16384, false)]
    [InlineData("VP8L", 10, 1025, false)]
    [InlineData("VP8X", 1024, 1024, true)]
    [InlineData("VP8X", 16000000, 1, false)]
    public void Webp_DimensionLimits(string kind, int width, int height, bool accepted)
        => Assert.Equal(accepted, AvatarImage.TryValidate(Webp(kind, width, height), out _));

    [Fact]
    public void Apng_IsRefused()
        => Assert.False(AvatarImage.TryValidate(Png(64, 64, animated: true), out _));

    [Fact]
    public void FilesCutAfterTheirHeader_AreRefused()
    {
        var png = Png(64, 64);
        Assert.True(AvatarImage.TryValidate(png, out _));
        Assert.False(AvatarImage.TryValidate(png[..^6], out _));
        Assert.False(AvatarImage.TryValidate(png[..^12], out _));

        var jpeg = Jpeg(64, 64);
        Assert.True(AvatarImage.TryValidate(jpeg, out _));
        Assert.False(AvatarImage.TryValidate(jpeg[..^1], out _));
        Assert.False(AvatarImage.TryValidate(jpeg[..^2], out _));
    }

    [Fact]
    public void AnimatedWebp_IsRefused()
    {
        Assert.False(AvatarImage.TryValidate(Webp("VP8X", 64, 64, animated: true), out _));
        Assert.False(AvatarImage.TryValidate(Webp("VP8X", 64, 64, animChunk: true), out _));
    }

    [Fact]
    public void Truncated_AreRefused()
    {
        foreach (var image in new[] { Png(64, 64), Jpeg(64, 64), Webp("VP8 ", 64, 64), Webp("VP8L", 64, 64), Webp("VP8X", 64, 64) })
        {
            for (var cut = 1; cut < image.Length - 1; cut += 3)
            {
                // A truncated file is either refused or, if the header was complete, still within the limits: never an exception.
                var copy = image[..cut];
                _ = AvatarImage.TryValidate(copy, out _);
            }
        }

        Assert.False(AvatarImage.TryValidate(Png(64, 64)[..20], out _));
        Assert.False(AvatarImage.TryValidate(Jpeg(64, 64)[..8], out _));
        Assert.False(AvatarImage.TryValidate(Webp("VP8 ", 64, 64)[..24], out _));
    }

    [Fact]
    public void WebpWithRiffSizeBeyondTheFile_IsRefused()
    {
        var image = Webp("VP8 ", 64, 64);
        image[4] = 0xFF;
        image[5] = 0xFF;

        Assert.False(AvatarImage.TryValidate(image, out _));
    }

    [Fact]
    public void JpegWithoutFrameHeader_IsRefused()
        => Assert.False(AvatarImage.TryValidate(new byte[] { 0xFF, 0xD8, 0xFF, 0xDA, 0, 4, 0, 0, 0xFF, 0xD9 }, out _));

    [Fact]
    public void MaxSize_Passes_OneMore_Fails()
    {
        var atLimit = Padded(Png(64, 64), 1_048_576);
        Assert.True(AvatarImage.TryValidate(atLimit, out _));
        Assert.False(AvatarImage.TryValidate(Padded(Png(64, 64), 1_048_577), out _));
    }

    [Fact]
    public void TryDecode_InvalidBase64_IsNoAvatar()
    {
        var png = Png(64, 64);
        Assert.False(AvatarImage.TryDecode("not base64 !!!", out _, out _));
        Assert.False(AvatarImage.TryDecode(null, out _, out _));
        Assert.True(AvatarImage.TryDecode(Convert.ToBase64String(png), out var bytes, out var mime));
        Assert.Equal(png, bytes);
        Assert.Equal("image/png", mime);
    }

    [Fact]
    public void TryDecode_RefusesOversizedDimensions()
        => Assert.False(AvatarImage.TryDecode(Convert.ToBase64String(Png(65535, 65535)), out _, out _));

    // A PNG made of the signature, IHDR, an optional acTL, a junk IDAT and IEND (the CRCs are not checked).
    private static byte[] Png(int width, int height, bool animated = false)
    {
        var bytes = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var ihdr = new List<byte>();
        ihdr.AddRange(BigEndian(width));
        ihdr.AddRange(BigEndian(height));
        ihdr.AddRange(new byte[] { 8, 2, 0, 0, 0 });
        Chunk(bytes, "IHDR", ihdr.ToArray());
        if (animated)
        {
            Chunk(bytes, "acTL", new byte[] { 0, 0, 0, 2, 0, 0, 0, 0 });
        }

        Chunk(bytes, "IDAT", new byte[] { 1, 2, 3, 4 });
        Chunk(bytes, "IEND", Array.Empty<byte>());
        return bytes.ToArray();
    }

    private static void Chunk(List<byte> bytes, string type, byte[] data)
    {
        bytes.AddRange(BigEndian(data.Length));
        bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(type));
        bytes.AddRange(data);
        bytes.AddRange(new byte[4]);
    }

    private static byte[] BigEndian(int value)
        => new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value };

    private static byte[] Jpeg(int width, int height, byte sof = 0xC0, bool withApp = false)
    {
        var bytes = new List<byte> { 0xFF, 0xD8 };
        if (withApp)
        {
            bytes.AddRange(new byte[] { 0xFF, 0xE0, 0, 6, 0x4A, 0x46, 0x49, 0x46 });
            bytes.AddRange(new byte[] { 0xFF, 0xFF, 0xFE, 0, 4, 0x61, 0x62 });
        }
        else
        {
            bytes.AddRange(new byte[] { 0xFF, 0xE0, 0, 4, 0, 0 });
        }

        bytes.AddRange(new byte[] { 0xFF, sof, 0, 11, 8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 1, 1, 0x11, 0 });
        bytes.AddRange(new byte[] { 0xFF, 0xDA, 0, 4, 0, 0, 1, 2, 3, 0xFF, 0xD9 });
        return bytes.ToArray();
    }

    private static byte[] Webp(string kind, int width, int height, bool animated = false, bool animChunk = false)
    {
        var body = new List<byte>();
        body.AddRange(System.Text.Encoding.ASCII.GetBytes("WEBP"));
        if (kind == "VP8X")
        {
            body.AddRange(WebpChunk("VP8X", new byte[] { (byte)(animated ? 0x02 : 0), 0, 0, 0, (byte)(width - 1), (byte)((width - 1) >> 8), (byte)((width - 1) >> 16), (byte)(height - 1), (byte)((height - 1) >> 8), (byte)((height - 1) >> 16) }));
            if (animChunk)
            {
                body.AddRange(WebpChunk("ANIM", new byte[6]));
            }

            body.AddRange(WebpChunk("VP8L", Vp8l(width, height)));
        }
        else if (kind == "VP8L")
        {
            body.AddRange(WebpChunk("VP8L", Vp8l(width, height)));
        }
        else
        {
            body.AddRange(WebpChunk("VP8 ", new byte[] { 0x10, 0, 0, 0x9D, 0x01, 0x2A, (byte)width, (byte)((width >> 8) & 0x3F), (byte)height, (byte)((height >> 8) & 0x3F), 0, 0 }));
        }

        var bytes = new List<byte>(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        bytes.AddRange(BitConverter.GetBytes(body.Count));
        bytes.AddRange(body);
        return bytes.ToArray();
    }

    private static byte[] Vp8l(int width, int height)
    {
        var bits = (uint)(width - 1) | ((uint)(height - 1) << 14);
        return new byte[] { 0x2F }.Concat(BitConverter.GetBytes(bits)).Concat(new byte[] { 0, 0 }).ToArray();
    }

    private static byte[] WebpChunk(string type, byte[] data)
    {
        var chunk = new List<byte>(System.Text.Encoding.ASCII.GetBytes(type));
        chunk.AddRange(BitConverter.GetBytes(data.Length));
        chunk.AddRange(data);
        if (data.Length % 2 == 1)
        {
            chunk.Add(0);
        }

        return chunk.ToArray();
    }

    private static byte[] Padded(byte[] image, int size)
    {
        // Junk in a chunk before IDAT would still be a valid container: pad with a trailing ignored area.
        var bytes = new byte[size];
        image.CopyTo(bytes, 0);
        return bytes;
    }
}

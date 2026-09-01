using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using GDeflateNet;
using ReeLib;
using ReeLib.DDS;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ReExtractor.Core;

/// <summary>
/// Decodes RE Engine .tex files to RGBA32 images.
///
/// Strategy:
/// - For ALL block-compressed formats (BC1-BC7): use CreateIterator + BCnEncoder.
///   BC7 verified correct for this game's data (probe7: body/hand albedos decode cleanly).
///   The earlier WIC detour was dead code: System.Drawing.Bitmap has no DDS codec on this
///   machine — it throws "Parameter is not valid" for EVERY DDS flavor incl. legacy DXT1.
/// - For uncompressed: iterator + manual pixel swizzle
///
/// Use <see cref="DecodeToImageDiag"/> instead of <see cref="DecodeToImage"/> when you need
/// per-decode diagnostics (format, decode branch, pixel statistics) for debugging.
/// </summary>
public sealed class TexService
{
    /// <summary>Per-decode diagnostics, surfaced in the GUI when TEX_DEBUG is on.</summary>
    public sealed class TexDiagnostics
    {
        public string Format = "";
        public int Width, Height, MipCount;
        public string Branch = "";
        public string PixelStats = "";
        public string Notes = "";
    }

    /// <summary>Decode mip 0 of a .tex stream into an RGBA32 image (diagnostics discarded).</summary>
    public Image<Rgba32> DecodeToImage(Stream texStream, string nativePath)
    {
        var (img, _) = DecodeToImageCore(texStream, nativePath, collectDiagnostics: false);
        return img;
    }

    /// <summary>
    /// Decode mip 0 and return diagnostics alongside the image. Used by the GUI TEX_DEBUG panel.
    /// </summary>
    public (Image<Rgba32> Image, TexDiagnostics Diag) DecodeToImageDiag(Stream texStream, string nativePath)
        => DecodeToImageCore(texStream, nativePath, collectDiagnostics: true);

    private (Image<Rgba32> Image, TexDiagnostics Diag) DecodeToImageCore(
        Stream texStream, string nativePath, bool collectDiagnostics)
    {
        // GDeflate decompression replaces the compressed mip payload in-place in
        // ReeLib's FileHandler. PAK reads already return a writable MemoryStream,
        // whereas an extracted-folder texture arrives as a read-only FileStream.
        // Copy the latter before parsing so newer GDeflate games (Wilds, MHS3,
        // RE9) decode identically from either source.
        using var writableCopy = texStream.CanWrite ? null : new MemoryStream();
        if (writableCopy != null)
        {
            if (texStream.CanSeek) texStream.Position = 0;
            texStream.CopyTo(writableCopy);
            writableCopy.Position = 0;
            texStream = writableCopy;
        }

        var tex = new TexFile(new FileHandler(texStream, nativePath));
        if (!tex.Read())
            throw new InvalidDataException($"Failed to parse .tex: {nativePath}");

        // MHWILDS and newer RE Engine games store the mip payloads in GDeflate
        // tiles.  TexFile exposes the compressed mip table, but its iterator reads
        // the payload as if it were already decompressed, which produces the
        // characteristic rainbow noise when sent straight to a BC decoder.
        if (tex.IsCompressed)
        {
            tex.DecompressGDeflate((_, compressed, decompressed) =>
                GDeflate.Decompress(compressed.ToArray(), decompressed));
        }

        var w = Math.Max(1, (int)tex.Header.width);
        var h = Math.Max(1, (int)tex.Header.height);
        var format = tex.Header.format;

        var diag = new TexDiagnostics
        {
            Format = $"{format} ({(int)format})",
            Width = w,
            Height = h,
            MipCount = tex.Header.mipCount,
        };

        // All block-compressed (incl. BC7): CreateIterator + BCnEncoder
        if (format.IsBlockCompressedFormat())
        {
            diag.Branch = "BCnEncoder (CreateIterator + DecodeRaw2D)";
            return DecodeCompressed(tex, w, h, format, diag, collectDiagnostics);
        }

        // Uncompressed
        diag.Branch = "iterator (uncompressed)";
        return DecodeUncompressed(tex, w, h, format, diag, collectDiagnostics);
    }

    /// <summary>
    /// For block-compressed textures (BC1-BC7): use CreateIterator (padding-safe) + BCnEncoder.
    /// </summary>
    private static (Image<Rgba32> Image, TexDiagnostics Diag) DecodeCompressed(
        TexFile tex, int width, int height, DxgiFormat format, TexDiagnostics diag, bool collectDiagnostics)
    {
        using var iter = tex.CreateIterator(0, 0);
        var mip = new DDSFile.MipMapLevelData();
        if (!iter.Next(ref mip) || mip.data.IsEmpty)
            throw new InvalidDataException("No readable mip 0 data");

        var w = (int)mip.width;
        var h = (int)mip.height;
        var raw = new byte[mip.data.Length];
        mip.data.CopyTo(raw);

        var cf = ToCompressionFormat(format);
        var decoder = new BcDecoder();
        var decoded = decoder.DecodeRaw2D(raw, w, h, cf);
        var pixels = new Rgba32[w * h];
        var span = decoded.Span;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var c = span[y, x];
                pixels[y * w + x] = new Rgba32(c.r, c.g, c.b, c.a);
            }
        var img = Image.LoadPixelData<Rgba32>(pixels, w, h);
        if (collectDiagnostics) ComputePixelStats(img, diag);
        return (img, diag);
    }

    /// <summary>
    /// For uncompressed textures: read via iterator (padding-safe), then convert pixel format.
    /// </summary>
    private static (Image<Rgba32> Image, TexDiagnostics Diag) DecodeUncompressed(
        TexFile tex, int width, int height, DxgiFormat format, TexDiagnostics diag, bool collectDiagnostics)
    {
        using var iter = tex.CreateIterator(0, 0);
        var mip = new DDSFile.MipMapLevelData();
        if (!iter.Next(ref mip) || mip.data.IsEmpty)
            throw new InvalidDataException("No readable mip 0 data");

        var w = (int)mip.width;
        var h = (int)mip.height;
        var raw = new byte[mip.data.Length];
        mip.data.CopyTo(raw);

        var bpp = format.GetBitsPerPixel() / 8;
        var pixels = new Rgba32[w * h];
        switch (format)
        {
            case DxgiFormat.R8G8B8A8_UNORM or DxgiFormat.R8G8B8A8_UNORM_SRGB:
                for (var i = 0; i < pixels.Length; i++)
                    pixels[i] = new Rgba32(raw[i * 4], raw[i * 4 + 1], raw[i * 4 + 2], raw[i * 4 + 3]);
                break;
            case DxgiFormat.B8G8R8A8_UNORM or DxgiFormat.B8G8R8A8_UNORM_SRGB:
                for (var i = 0; i < pixels.Length; i++)
                    pixels[i] = new Rgba32(raw[i * 4 + 2], raw[i * 4 + 1], raw[i * 4], raw[i * 4 + 3]);
                break;
            case DxgiFormat.B8G8R8X8_UNORM or DxgiFormat.B8G8R8X8_UNORM_SRGB:
                for (var i = 0; i < pixels.Length; i++)
                    pixels[i] = new Rgba32(raw[i * 4 + 2], raw[i * 4 + 1], raw[i * 4], 255);
                break;
            case DxgiFormat.R8_UNORM:
                for (var i = 0; i < pixels.Length; i++)
                    pixels[i] = new Rgba32(raw[i], raw[i], raw[i], 255);
                break;
            case DxgiFormat.R8G8_UNORM:
                for (var i = 0; i < pixels.Length; i++)
                    pixels[i] = new Rgba32(raw[i * 2], raw[i * 2 + 1], 0, 255);
                break;
            default:
                throw new NotSupportedException($"Unsupported DXGI format: {format} ({(int)format})");
        }
        var img = Image.LoadPixelData<Rgba32>(pixels, w, h);
        if (collectDiagnostics) ComputePixelStats(img, diag);
        return (img, diag);
    }

    /// <summary>
    /// Walk every pixel once to gather min/max/avg and sanity flags.
    /// Cheap for normal game textures (&lt;=2048²); flags ALL-BLACK / ALL-WHITE / ALPHA count.
    /// </summary>
    private static void ComputePixelStats(Image<Rgba32> img, TexDiagnostics diag)
    {
        int minR = 255, minG = 255, minB = 255, minA = 255;
        int maxR = 0, maxG = 0, maxB = 0, maxA = 0;
        ulong sR = 0, sG = 0, sB = 0, sA = 0;
        long allBlack = 0, allWhite = 0, anyAlpha = 0;
        int n = img.Width * img.Height;
        for (var y = 0; y < img.Height; y++)
        {
            for (var x = 0; x < img.Width; x++)
            {
                var p = img[x, y];
                if (p.R < minR) minR = p.R;
                if (p.G < minG) minG = p.G;
                if (p.B < minB) minB = p.B;
                if (p.A < minA) minA = p.A;
                if (p.R > maxR) maxR = p.R;
                if (p.G > maxG) maxG = p.G;
                if (p.B > maxB) maxB = p.B;
                if (p.A > maxA) maxA = p.A;
                sR += p.R; sG += p.G; sB += p.B; sA += p.A;
                if (p.R == 0 && p.G == 0 && p.B == 0) allBlack++;
                if (p.R == 255 && p.G == 255 && p.B == 255) allWhite++;
                if (p.A < 255) anyAlpha++;
            }
        }
        diag.PixelStats = $"R[{minR}..{maxR}] G[{minG}..{maxG}] B[{minB}..{maxB}] A[{minA}..{maxA}] avg({sR / (ulong)n},{sG / (ulong)n},{sB / (ulong)n},{sA / (ulong)n})";
        var flags = new System.Collections.Generic.List<string>();
        if (allBlack == n) flags.Add("ALL-BLACK");
        if (allWhite == n) flags.Add("ALL-WHITE");
        if (anyAlpha > 0) flags.Add($"ALPHA×{anyAlpha}");
        diag.Notes = flags.Count > 0 ? string.Join(" | ", flags) : "ok";
    }

    public string ConvertToPng(Stream texStream, string nativePath, string outputPath)
    {
        using var img = DecodeToImage(texStream, nativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        img.SaveAsPng(outputPath);
        return outputPath;
    }

    private static CompressionFormat ToCompressionFormat(DxgiFormat f) => f switch
    {
        DxgiFormat.BC1_TYPELESS or DxgiFormat.BC1_UNORM or DxgiFormat.BC1_UNORM_SRGB => CompressionFormat.Bc1WithAlpha,
        DxgiFormat.BC2_TYPELESS or DxgiFormat.BC2_UNORM or DxgiFormat.BC2_UNORM_SRGB => CompressionFormat.Bc2,
        DxgiFormat.BC3_TYPELESS or DxgiFormat.BC3_UNORM or DxgiFormat.BC3_UNORM_SRGB => CompressionFormat.Bc3,
        DxgiFormat.BC4_TYPELESS or DxgiFormat.BC4_UNORM or DxgiFormat.BC4_SNORM => CompressionFormat.Bc4,
        DxgiFormat.BC5_TYPELESS or DxgiFormat.BC5_UNORM or DxgiFormat.BC5_SNORM => CompressionFormat.Bc5,
        DxgiFormat.BC6H_TYPELESS or DxgiFormat.BC6H_UF16 => CompressionFormat.Bc6U,
        DxgiFormat.BC6H_SF16 => CompressionFormat.Bc6S,
        DxgiFormat.BC7_TYPELESS or DxgiFormat.BC7_UNORM or DxgiFormat.BC7_UNORM_SRGB => CompressionFormat.Bc7,
        _ => throw new NotSupportedException($"BC format: {f} ({(int)f})"),
    };
}

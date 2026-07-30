#:project ../src/ReExtractor.Core/ReExtractor.Core.csproj

using System.Numerics;
using ReExtractor.Core;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

// Final validation: streaming-first textures (patched Core OpenNormalized) rendered with
// S = mip0 bilinear (current GUI path) vs T = proper trilinear mip filtering
// (per-triangle constant lambda from affine UV derivatives, two-mip blend).
var gameDir = @"E:\Steam\steamapps\common\OnimushaWotS_Demo";
var listPath = @"D:\OnimushaWotS_Demo_Tools\REasy_v0.7.3\resources\data\lists\ONIWOTS_STM.list";
var meshPath = "natives/stm/art/model/character/ch0/ch001_00/00/ch001_00_00.mesh.251215606";
var outDir = @"D:\texdump\probe8";
Directory.CreateDirectory(outDir);

var pak = new PakService();
pak.AddPaksFromGameDir(gameDir);
pak.LoadListFile(listPath);
Stream? Open(string p) { try { return pak.ReadFile(p); } catch { return null; } }

using var ms = pak.ReadFile(meshPath);
var vm = ViewportDataLoader.LoadMesh(ms, meshPath, 1, Open);
Console.WriteLine($"verts={vm.VertexCount} faces={vm.FaceCount} tex={vm.Textures.Length} viscon={vm.VisconInfo}");
for (var i = 0; i < vm.Textures.Length; i++)
    Console.WriteLine($"  tex[{i}] {vm.Textures[i].Name} {vm.Textures[i].Width}x{vm.Textures[i].Height} mips={vm.Textures[i].Mips.Length}");

var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
foreach (var v in vm.Vertices) { min = Vector3.Min(min, v); max = Vector3.Max(max, v); }
var target = (min + max) * 0.5f;
var extent = MathF.Max(max.X - min.X, MathF.Max(max.Y - min.Y, max.Z - min.Z));
var dist = MathF.Max(0.3f, extent * 1.2f);
const float yaw = 0.7f, pitch = 0.35f;
var eye = target + new Vector3(MathF.Cos(pitch) * MathF.Sin(yaw), MathF.Sin(pitch), MathF.Cos(pitch) * MathF.Cos(yaw)) * dist;

const int W = 1000, H = 1000;
var view = Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY);
var proj = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4, W / (float)H, dist * 0.01f, dist * 20f);
var vp = view * proj;

var lightDir = Vector3.Normalize(new Vector3(0.5f, -1f, 0.8f));
var intensity = new float[vm.VertexCount];
for (var i = 0; i < vm.VertexCount; i++)
    intensity[i] = 0.25f + 0.75f * MathF.Abs(Vector3.Dot(vm.Normals[i], lightDir));

Render("S_streaming_mip0", trilinear: false, perspectiveUv: false);
Render("T_streaming_trilinear", trilinear: true, perspectiveUv: false);
Render("P_perspective_uv_trilinear", trilinear: true, perspectiveUv: true);
Console.WriteLine("DONE");

void Render(string tag, bool trilinear, bool perspectiveUv)
{
    var color = new uint[W * H];
    var depth = new float[W * H];
    Array.Fill(color, 0xFF1E1E1Eu);
    Array.Fill(depth, float.MaxValue);

    var vc = vm.VertexCount;
    var sx = new float[vc]; var sy = new float[vc]; var sz = new float[vc]; var invClipW = new float[vc];
    for (var i = 0; i < vc; i++)
    {
        var c = Vector4.Transform(new Vector4(vm.Vertices[i], 1f), vp);
        if (c.W <= 1e-6f) { sz[i] = float.NaN; continue; }
        var inv = 1f / c.W;
        invClipW[i] = inv;
        sx[i] = (c.X * inv * 0.5f + 0.5f) * W;
        sy[i] = (0.5f - c.Y * inv * 0.5f) * H;
        sz[i] = c.Z * inv;
    }

    var faces = vm.Faces; var faceTex = vm.FaceTexture; var uvs = vm.Uvs; var textures = vm.Textures;
    for (var f = 0; f < faces.Length; f++)
    {
        var (a, b, c2) = faces[f];
        var za = sz[a]; var zb = sz[b]; var zc = sz[c2];
        if (float.IsNaN(za) || float.IsNaN(zb) || float.IsNaN(zc)) continue;
        var slot = faceTex[f];
        var tex = slot >= 0 && slot < textures.Length ? textures[slot] : null;
        RasterTri(color, depth, sx[a], sy[a], za, invClipW[a], intensity[a], uvs[a],
            sx[b], sy[b], zb, invClipW[b], intensity[b], uvs[b],
            sx[c2], sy[c2], zc, invClipW[c2], intensity[c2], uvs[c2], tex, trilinear, perspectiveUv);
    }

    var img = new Image<Rgba32>(W, H);
    img.ProcessPixelRows(acc =>
    {
        for (var y = 0; y < H; y++)
        {
            var row = acc.GetRowSpan(y);
            for (var x = 0; x < W; x++)
            {
                var p = color[y * W + x];
                row[x] = new Rgba32((byte)(p >> 16), (byte)(p >> 8), (byte)p, 255);
            }
        }
    });
    var outPath = Path.Combine(outDir, tag + ".png");
    using (img) img.SaveAsPng(outPath);
    Console.WriteLine("-> " + outPath);
}

void RasterTri(uint[] pixels, float[] depth,
    float x0, float y0, float z0, float iw0, float i0, Vector2 uv0,
    float x1, float y1, float z1, float iw1, float i1, Vector2 uv1,
    float x2, float y2, float z2, float iw2, float i2, Vector2 uv2,
    ViewportTexture? tex, bool trilinear, bool perspectiveUv)
{
    var yMin = MathF.Min(y0, MathF.Min(y1, y2));
    var yMax = MathF.Max(y0, MathF.Max(y1, y2));
    var minX = (int)MathF.Max(0, MathF.Floor(MathF.Min(x0, MathF.Min(x1, x2))));
    var maxX = (int)MathF.Min(W - 1, MathF.Ceiling(MathF.Max(x0, MathF.Max(x1, x2))));
    var minY = (int)MathF.Max(0, MathF.Floor(yMin));
    var maxY = (int)MathF.Min(H - 1, MathF.Ceiling(yMax));
    if (minX > maxX || minY > maxY) return;

    var d = (y1 - y2) * (x0 - x2) + (x2 - x1) * (y0 - y2);
    if (MathF.Abs(d) < 1e-8f) return;
    var invD = 1f / d;

    var tw = tex?.Width ?? 0; var th = tex?.Height ?? 0; var tp = tex?.Pixels;

    // trilinear setup: per-triangle constant UV derivatives (affine interpolation)
    int mip0 = 0, mip1 = 0; float mipBlend = 0f;
    uint[][]? mips = null; int[]? mipW = null; int[]? mipH = null;
    if (trilinear && tex != null && tex.Mips.Length > 1)
    {
        var dw0dx = (y1 - y2) * invD; var dw1dx = (y2 - y0) * invD; var dw2dx = -(dw0dx + dw1dx);
        var dw0dy = (x2 - x1) * invD; var dw1dy = (x0 - x2) * invD; var dw2dy = -(dw0dy + dw1dy);
        var dudx = dw0dx * uv0.X + dw1dx * uv1.X + dw2dx * uv2.X;
        var dvdx = dw0dx * uv0.Y + dw1dx * uv1.Y + dw2dx * uv2.Y;
        var dudy = dw0dy * uv0.X + dw1dy * uv1.X + dw2dy * uv2.X;
        var dvdy = dw0dy * uv0.Y + dw1dy * uv1.Y + dw2dy * uv2.Y;
        var fx = MathF.Sqrt(dudx * tw * dudx * tw + dvdx * th * dvdx * th);
        var fy = MathF.Sqrt(dudy * tw * dudy * tw + dvdy * th * dvdy * th);
        var lambda = MathF.Log2(MathF.Max(1e-6f, MathF.Max(fx, fy)));
        var maxMip = tex.Mips.Length - 1;
        var mipF = Math.Clamp(lambda, 0f, maxMip);
        mip0 = (int)MathF.Floor(mipF);
        mip1 = Math.Min(mip0 + 1, maxMip);
        mipBlend = mipF - mip0;
        mips = tex.Mips; mipW = tex.MipW; mipH = tex.MipH;
    }

    for (var y = minY; y <= maxY; y++)
    {
        var row = y * W;
        for (var x = minX; x <= maxX; x++)
        {
            var px = x + 0.5f; var py = y + 0.5f;
            var w0 = ((y1 - y2) * (px - x2) + (x2 - x1) * (py - y2)) * invD;
            var w1 = ((y2 - y0) * (px - x2) + (x0 - x2) * (py - y2)) * invD;
            var w2 = 1f - w0 - w1;
            if (w0 < 0 || w1 < 0 || w2 < 0) continue;

            var z = w0 * z0 + w1 * z1 + w2 * z2;
            var idx = row + x;
            if (z >= depth[idx]) continue;
            depth[idx] = z;

            var lum = w0 * i0 + w1 * i1 + w2 * i2;
            uint color;
            if (tp != null)
            {
                float u, v;
                if (perspectiveUv)
                {
                    var pw0 = w0 * iw0; var pw1 = w1 * iw1; var pw2 = w2 * iw2;
                    var invPw = 1f / (pw0 + pw1 + pw2);
                    u = (pw0 * uv0.X + pw1 * uv1.X + pw2 * uv2.X) * invPw;
                    v = (pw0 * uv0.Y + pw1 * uv1.Y + pw2 * uv2.Y) * invPw;
                }
                else
                {
                    u = w0 * uv0.X + w1 * uv1.X + w2 * uv2.X;
                    v = w0 * uv0.Y + w1 * uv1.Y + w2 * uv2.Y;
                }
                uint texel;
                if (mips != null && mipW != null && mipH != null)
                {
                    var t0 = SampleBilinear(mips[mip0], mipW[mip0], mipH[mip0], u, v);
                    if (mip1 != mip0 && mipBlend > 0.001f)
                    {
                        var t1 = SampleBilinear(mips[mip1], mipW[mip1], mipH[mip1], u, v);
                        texel = Blend(t0, t1, mipBlend);
                    }
                    else texel = t0;
                }
                else
                {
                    texel = SampleBilinear(tp, tw, th, u, v);
                }
                var bB = (texel & 0xFF) * lum;
                var bG = ((texel >> 8) & 0xFF) * lum;
                var bR = ((texel >> 16) & 0xFF) * lum;
                color = 0xFF000000u | ((uint)MathF.Min(255, bR) << 16) | ((uint)MathF.Min(255, bG) << 8) | (uint)MathF.Min(255, bB);
            }
            else
            {
                var shade = (uint)MathF.Min(255, 210 * lum);
                color = 0xFF000000u | (shade << 16) | (shade << 8) | shade;
            }
            pixels[idx] = color;
        }
    }
}

static uint Blend(uint a, uint b, float f)
{
    var fb = 1f - f;
    var r = (uint)(((a >> 16) & 0xFF) * fb + ((b >> 16) & 0xFF) * f);
    var g = (uint)(((a >> 8) & 0xFF) * fb + ((b >> 8) & 0xFF) * f);
    var bl = (uint)((a & 0xFF) * fb + (b & 0xFF) * f);
    return 0xFF000000u | (r << 16) | (g << 8) | bl;
}

static uint SampleBilinear(uint[] tp, int tw, int th, float u, float v)
{
    u -= MathF.Floor(u);
    v -= MathF.Floor(v);
    var fx = u * tw - 0.5f;
    var fy = v * th - 0.5f;
    var x0 = (int)MathF.Floor(fx);
    var y0 = (int)MathF.Floor(fy);
    var tx = fx - x0;
    var ty = fy - y0;
    x0 = ((x0 % tw) + tw) % tw;
    y0 = ((y0 % th) + th) % th;
    var x1 = (x0 + 1) % tw;
    var y1 = (y0 + 1) % th;
    var c00 = tp[y0 * tw + x0];
    var c10 = tp[y0 * tw + x1];
    var c01 = tp[y1 * tw + x0];
    var c11 = tp[y1 * tw + x1];
    var b = Chan(c00, c10, c01, c11, 0, tx, ty);
    var g = Chan(c00, c10, c01, c11, 8, tx, ty);
    var r = Chan(c00, c10, c01, c11, 16, tx, ty);
    return 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b;

    static int Chan(uint c00, uint c10, uint c01, uint c11, int shift, float tx, float ty)
    {
        var b00 = (c00 >> shift) & 0xFF; var b10 = (c10 >> shift) & 0xFF;
        var b01 = (c01 >> shift) & 0xFF; var b11 = (c11 >> shift) & 0xFF;
        var top = b00 + (b10 - b00) * tx;
        var bot = b01 + (b11 - b01) * tx;
        return (int)(top + (bot - top) * ty);
    }
}

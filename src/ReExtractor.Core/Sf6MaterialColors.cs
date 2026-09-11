using System.Numerics;
using ReeLib;
using ReeLib.Mdf;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ReExtractor.Core;

internal static class Sf6MaterialColors
{
    public static bool IsCharacter(string path) => path.Replace('\\', '/').Contains("/product/model/esf/", StringComparison.OrdinalIgnoreCase);

    // Shared heads live in 000. Prefer the first costume without its own head:
    // Ryu/Ken costume 001 has a separate head and its palette does not describe 000.
    public static void ReadDefaultPalette(string meshPath, MdfFile mdf, Func<string, Stream?> open)
    {
        var parts = meshPath.Replace('\\', '/').Split('/');
        var character = Array.FindIndex(parts, p => p.Length == 6 && p.StartsWith("esf", StringComparison.OrdinalIgnoreCase) && p.Skip(3).All(char.IsDigit));
        if (character < 0 || character + 1 >= parts.Length) return;
        var id = parts[character];
        var costume = parts[character + 1] == "000" ? "001" : parts[character + 1];
        var directory = string.Join('/', parts.Take(character + 1));
        if (parts[character + 1] == "000")
        {
            using var ownHead = open($"{directory}/001/00/{id}_001_00.mesh.230110883");
            if (ownHead != null)
            {
                using var secondPalette = open($"{directory}/002/{id}_002_cmd_000.user.2");
                if (secondPalette != null) costume = "002";
            }
        }
        using var workspace = new Workspace(new GameConfig(new GameIdentifier("sf6")));
        ApplyPalette("000", false);
        ApplyPalette("001", true);

        void ApplyPalette(string colorIndex, bool enabledOverridesOnly)
        {
            var path = $"{directory}/{costume}/{id}_{costume}_cmd_{colorIndex}.user.2";
            using var stream = open(path);
            if (stream == null) return;
            using var user = new UserFile(workspace.RszFileOption, new FileHandler(stream, path));
            if (!user.Read()) return;
            var part = character + 2 < parts.Length ? parts[character + 2] : "";
            var partType = part switch { "01" => 0, "00" => 1, "02" => 2, _ => -1 };
            var groups = user.RSZ.InstanceList.Where(i => i.RszClass.name == "app.CostumeMaterialData.MaterialData");
            var clusters = groups.Where(g => partType < 0 || Convert.ToInt32(g.GetFieldValue("Type")) == partType)
                .SelectMany(g => (g.GetFieldValue("Clusters") as System.Collections.IEnumerable)?.Cast<object>().OfType<RszInstance>() ?? []);
            foreach (var cluster in clusters)
            {
                var name = cluster.GetFieldValue("Name") as string;
                var material = mdf.Materials.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (material == null || cluster.GetFieldValue("CustomizeColors") is not System.Collections.IEnumerable entries) continue;
                var slot = 0;
                foreach (var value in entries)
                {
                    if (value is RszInstance entry && entry.GetFieldValue("Color") is ReeLib.via.Color color)
                    {
                        if (enabledOverridesOnly && entry.GetFieldValue("Enable") is not true) { slot++; continue; }
                        // Base CMD includes default colors even when its override flag is false.
                        var parameter = material.Parameters.FirstOrDefault(p => p.paramName.Equals($"CustomizeColor_{slot}", StringComparison.OrdinalIgnoreCase));
                        if (parameter != null) parameter.parameter = new Vector4(Linear(color.R / 255f), Linear(color.G / 255f), Linear(color.B / 255f), color.A / 255f);
                        if (entry.GetFieldValue("Option") is RszInstance option && option.GetFieldValue("BlendRate") is RszInstance rate && rate.GetFieldValue("Enable") is true && rate.GetFieldValue("_Value") is float amount)
                        {
                            var blend = material.Parameters.FirstOrDefault(p => p.paramName.Equals($"CustomizeColor_{slot}_BlendRate", StringComparison.OrdinalIgnoreCase));
                            if (blend != null) blend.parameter = new Vector4(amount, 0, 0, 0);
                        }
                    }
                    slot++;
                }
            }
        }
    }

    // SF6's swatches are linear colors with 0.5 as neutral (2 * color).
    // BlendSwitch chooses multiplicative vs replacement blending of the tint,
    // rather than enabling/disabling the entire customization feature.
    public static void Apply(ViewportTexture texture, MaterialData material, Func<string, Stream?> open)
    {
        var layers = new List<(Image<Rgba32> Mask, Vector4[] Colors, float[] Rates)>();
        try
        {
            for (var layer = 0; layer < 2; layer++)
            {
                var slot = layer == 0 ? "CustomizeColor_Mask" : "CustomizeColor_Mask2";
                var header = material.Textures.FirstOrDefault(t => t.texType.Equals(slot, StringComparison.OrdinalIgnoreCase));
                if (header == null || (header.texPath.Contains("Null", StringComparison.OrdinalIgnoreCase) &&
                    !header.texPath.Contains("Null_RGBA_", StringComparison.OrdinalIgnoreCase))) continue;
                using var stream = open(header.texPath);
                if (stream == null) continue;
                var mask = new TexService().DecodeToImage(stream, header.texPath);
                // CMASK alpha is a fourth independent color region, not transparency.
                if (mask.Width != texture.Width || mask.Height != texture.Height)
                    mask.Mutate(c => c.Resize(new ResizeOptions { Size = new Size(texture.Width, texture.Height), PremultiplyAlpha = false }));
                var colors = Enumerable.Range(layer * 4, 4).Select(i => Parameter(material, $"CustomizeColor_{i}", new Vector4(.5f))).ToArray();
                var rates = Enumerable.Range(layer * 4, 4).Select(i => Parameter(material, $"CustomizeColor_{i}_BlendRate", Vector4.One).X).ToArray();
                layers.Add((mask, colors, rates));
            }
            var hair = layers.Count == 0 && material.Textures.Any(t => t.texType.Equals("BaseAnisoShiftMap", StringComparison.OrdinalIgnoreCase));
            if (layers.Count == 0 && !hair) return;
            var multiply = Parameter(material, "CustomizeColor_BlendSwitch", Vector4.Zero).X > .5f;
            var hairColor = Parameter(material, "CustomizeColor_0", new Vector4(.5f));
            var hairRate = Math.Clamp(Parameter(material, "CustomizeColor_0_BlendRate", Vector4.One).X, 0, 1);
            for (var y = 0; y < texture.Height; y++)
            for (var x = 0; x < texture.Width; x++)
            {
                var tint = hair ? Vector4.Lerp(Vector4.One, hairColor * 2, hairRate) : Vector4.One;
                foreach (var layer in layers)
                {
                    var mask = layer.Mask[x, y];
                    for (var channel = 0; channel < 4; channel++)
                    {
                        var weight = (channel switch { 0 => mask.R, 1 => mask.G, 2 => mask.B, _ => mask.A }) / 255f;
                        weight = Math.Clamp(weight * layer.Rates[channel], 0, 1);
                        var target = layer.Colors[channel] * 2;
                        tint = Vector4.Lerp(tint, multiply ? tint * target : target, weight);
                    }
                }
                var index = y * texture.Width + x;
                var pixel = texture.Pixels[index];
                var red = Encode(Linear(((pixel >> 16) & 255) / 255f) * tint.X);
                var green = Encode(Linear(((pixel >> 8) & 255) / 255f) * tint.Y);
                var blue = Encode(Linear((pixel & 255) / 255f) * tint.Z);
                texture.Pixels[index] = (pixel & 0xFF000000) | (red << 16) | (green << 8) | blue;
            }
        }
        finally { foreach (var layer in layers) layer.Mask.Dispose(); }
    }
    private static Vector4 Parameter(MaterialData m, string name, Vector4 fallback) => m.Parameters.FirstOrDefault(p => p.paramName.Equals(name, StringComparison.OrdinalIgnoreCase))?.parameter ?? fallback;
    private static float Linear(float c) => c <= .04045f ? c / 12.92f : MathF.Pow((c + .055f) / 1.055f, 2.4f);
    private static uint Encode(float c) { c = Math.Clamp(c, 0, 1); return (uint)Math.Clamp((int)MathF.Round((c <= .0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1 / 2.4f) - .055f) * 255), 0, 255); }
}

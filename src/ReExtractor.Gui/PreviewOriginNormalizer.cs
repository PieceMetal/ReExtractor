using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ReExtractor.Core;

namespace ReExtractor.Gui;

/// <summary>
/// Center a standalone character preview on its mesh bind origin. Cinematic MOTs can
/// store scene-space placement in their root track; the source clip remains untouched
/// so exports retain that placement and all relative root motion.
/// </summary>
internal static class PreviewOriginNormalizer
{
    public static AnimationClip CenterRootAtBindOrigin(AnimationClip clip, IReadOnlyList<ViewportMesh> meshes)
    {
        foreach (var mesh in meshes.OrderByDescending(m => m.DeformToBone.Length)
                     .ThenByDescending(m => m.VertexCount))
        {
            var rootIndex = Array.FindIndex(mesh.Bones, bone =>
                bone.ParentIndex < 0 && bone.Name.Equals("root", StringComparison.OrdinalIgnoreCase));
            if (rootIndex < 0) continue;
            var root = mesh.Bones[rootIndex];
            if (!clip.NamedTracks.TryGetValue(root.Name, out var source) ||
                source.Translations is not { Length: > 0 } translations)
                continue;

            var initial = source.ResolveTranslation(translations[0], root.LocalBind);
            var offset = initial - root.LocalBind.Translation;
            if (!float.IsFinite(offset.X) || !float.IsFinite(offset.Y) || !float.IsFinite(offset.Z) ||
                offset.LengthSquared() < 1e-10f)
                return clip;

            var centeredRoot = new BoneTrack
            {
                IsAdditive = source.IsAdditive,
                TransTimes = source.TransTimes,
                Translations = translations.Select(value => value - offset).ToArray(),
                RotTimes = source.RotTimes,
                Rotations = source.Rotations,
            };
            var tracks = clip.Tracks.ToDictionary(
                pair => pair.Key,
                pair => ReferenceEquals(pair.Value, source) ? centeredRoot : pair.Value);
            var namedTracks = new Dictionary<string, BoneTrack>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, track) in clip.NamedTracks)
                namedTracks[name] = ReferenceEquals(track, source) ? centeredRoot : track;

            return new AnimationClip
            {
                Name = clip.Name,
                Duration = clip.Duration,
                FrameRate = clip.FrameRate,
                FrameCount = clip.FrameCount,
                Tracks = tracks,
                NamedTracks = namedTracks,
            };
        }
        return clip;
    }
}

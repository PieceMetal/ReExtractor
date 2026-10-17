using System.Numerics;
using ReeLib;
using ReeLib.Jcns;

namespace ReExtractor.Core;

/// <summary>Costume pose correctives used by Kunitsu-Gami's JCNS 26 resources.</summary>
public sealed class KunitsuJointConstraints
{
    private readonly Constraint[] _constraints;
    private KunitsuJointConstraints(Constraint[] constraints) => _constraints = constraints;

    internal static KunitsuJointConstraints? Load(string meshPath, Func<string, Stream?>? open)
    {
        const string suffix = ".mesh.240306278";
        if (open == null || !meshPath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return null;
        var path = meshPath[..^suffix.Length] + ".jcns.26";
        using var input = open(path);
        if (input == null) return null;
        using var file = new JcnsFile(new FileHandler(input, path));
        if (!file.TryReadRangeConstraints()) return null;
        // These resources use additive scalar channels with local swing/twist inputs.
        // Other JCNS modes need their own evaluation, never guessed Euler conversion.
        if (file.Constraints.Any(c => c.ConeDrivers.Count != 0 || c.uknByte1 != 1 ||
            (int)c.transformType is not (0 or 2 or 14) || (int)c.transformAxis > 2 ||
            c.Sources.Any(s => s.uknByte2 != 3 || (int)s.axis > 2 || s.ComplexMappingInfos.Count != 0)))
            return null;
        return file.Constraints.Count == 0 ? null : new(file.Constraints.ToArray());
    }

    internal static KunitsuJointConstraints? Combine(IEnumerable<ViewportMesh> meshes)
    {
        var constraints = meshes.Where(m => m.JointConstraints != null)
            .SelectMany(m => m.JointConstraints!._constraints)
            .DistinctBy(c => (c.name, c.transformType, c.transformAxis)).ToArray();
        return constraints.Length == 0 ? null : new(constraints);
    }

    public AnimationClip Bake(ViewportMesh mesh, AnimationClip clip)
    {
        var bones = mesh.Bones.Select((b, i) => (b, i)).GroupBy(p => p.b.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var groups = _constraints.Where(c => bones.ContainsKey(c.name) && c.Sources.All(s => bones.ContainsKey(s.jointName)))
            .GroupBy(c => c.name, StringComparer.OrdinalIgnoreCase).ToArray();
        if (groups.Length == 0) return clip;
        var bind = bones.ToDictionary(p => p.Key, p => Components(p.Value.b.LocalBind), StringComparer.OrdinalIgnoreCase);
        var rate = Math.Max(60, clip.FrameRate);
        var count = checked((int)MathF.Ceiling(clip.Duration * rate) + 1);
        var times = Enumerable.Range(0, count).Select(i => Math.Min(i / (float)rate, clip.Duration)).ToArray();
        var named = new Dictionary<string, BoneTrack>(clip.NamedTracks, StringComparer.OrdinalIgnoreCase);
        var indexed = new Dictionary<int, BoneTrack>(clip.Tracks);
        foreach (var group in groups)
        {
            var positions = new Vector3[count]; var rotations = new Quaternion[count]; var scales = new Vector3[count];
            for (var k = 0; k < count; k++)
            {
                var initial = Pose(group.Key, times[k]);
                var position = initial.Position; var scale = initial.Scale; var angles = Vector3.Zero;
                foreach (var c in group)
                {
                    var value = 0f;
                    foreach (var source in c.Sources)
                    {
                        var pose = Pose(source.jointName, times[k]);
                        var relative = Quaternion.Normalize(Quaternion.Inverse(bind[source.jointName].Rotation) * pose.Rotation);
                        value += Map(Axis(ToSwingTwist(relative), (int)source.axis), source.mapFrom, source.mapTo);
                    }
                    var axis = (int)c.transformAxis;
                    switch ((int)c.transformType)
                    {
                        case 0: position = WithAxis(position, axis, Axis(position, axis) + value * 0.01f); break;
                        case 2: scale = WithAxis(scale, axis, Axis(scale, axis) * value); break;
                        case 14: angles = WithAxis(angles, axis, Axis(angles, axis) + value); break;
                    }
                }
                positions[k] = position; scales[k] = scale;
                rotations[k] = Quaternion.Normalize(initial.Rotation * FromSwingTwist(angles));
            }
            var track = new BoneTrack { TransTimes = times, Translations = positions, RotTimes = times,
                Rotations = rotations, ScaleTimes = times, Scales = scales };
            named[group.Key] = track; indexed[bones[group.Key].i] = track;
        }
        return new AnimationClip { Name = clip.Name, Duration = clip.Duration, FrameRate = clip.FrameRate,
            FrameCount = clip.FrameCount, NamedTracks = named, Tracks = indexed, PoseAdaptation = clip.PoseAdaptation };

        (Vector3 Scale, Quaternion Rotation, Vector3 Position) Pose(string name, float time)
        {
            var rest = bind[name];
            if (!clip.NamedTracks.TryGetValue(name, out var track)) return rest;
            return (track.Scales is { Length: > 0 } s ? Sample(s, track.ScaleTimes!, time) : rest.Scale,
                track.Rotations is { Length: > 0 } r ? Sample(r, track.RotTimes!, time) : rest.Rotation,
                track.Translations is { Length: > 0 } p ? Sample(p, track.TransTimes!, time) : rest.Position);
        }
    }

    private static (Vector3 Scale, Quaternion Rotation, Vector3 Position) Components(Matrix4x4 matrix)
    {
        Matrix4x4.Decompose(matrix, out var scale, out var rotation, out var position);
        return (scale, Quaternion.Normalize(rotation), position);
    }

    // Verified against native JointConstraintsLayer.update on the supplied game:
    // mode 3 removes X twist before projecting the remaining Y/Z swing angles.
    private static Vector3 ToSwingTwist(Quaternion q)
    {
        if (q.W < 0) q = new(-q.X, -q.Y, -q.Z, -q.W);
        var length = MathF.Sqrt(q.X * q.X + q.W * q.W);
        var twist = length > 1e-8f ? new Quaternion(q.X / length, 0, 0, q.W / length) : Quaternion.Identity;
        var swing = q * Quaternion.Conjugate(twist);
        return new Vector3(2 * MathF.Atan2(twist.X, twist.W),
            2 * MathF.Atan2(swing.Y, swing.W), 2 * MathF.Atan2(swing.Z, swing.W)) * (180 / MathF.PI);
    }

    private static Quaternion FromSwingTwist(Vector3 degrees)
    {
        var half = degrees * (MathF.PI / 360);
        var swing = Quaternion.Normalize(new Quaternion(0, MathF.Tan(half.Y), MathF.Tan(half.Z), 1));
        var twist = new Quaternion(MathF.Sin(half.X), 0, 0, MathF.Cos(half.X));
        return swing * twist;
    }

    private static float Map(float x, Range3 from, Range3 to) => x < from.mid
        ? to.mid + (to.min - to.mid) * Math.Clamp((from.mid - x) / Math.Max(1e-8f, from.mid - from.min), 0, 1)
        : to.mid + (to.max - to.mid) * Math.Clamp((x - from.mid) / Math.Max(1e-8f, from.max - from.mid), 0, 1);
    private static float Axis(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;
    private static Vector3 WithAxis(Vector3 v, int axis, float value)
    { if (axis == 0) v.X = value; else if (axis == 1) v.Y = value; else v.Z = value; return v; }
    private static (int Index, float Blend) Segment(float[] times, float time)
    {
        var i = Array.BinarySearch(times, time);
        if (i >= 0) return (i, 0);
        i = ~i;
        if (i == 0) return (0, 0);
        if (i >= times.Length) return (times.Length - 1, 0);
        return (i - 1, (time - times[i - 1]) / (times[i] - times[i - 1]));
    }
    private static Vector3 Sample(Vector3[] values, float[] times, float time)
    { var (i, blend) = Segment(times, time); return blend == 0 ? values[i] : Vector3.Lerp(values[i], values[i + 1], blend); }
    private static Quaternion Sample(Quaternion[] values, float[] times, float time)
    { var (i, blend) = Segment(times, time); return blend == 0 ? values[i] : Quaternion.Slerp(values[i], values[i + 1], blend); }
}

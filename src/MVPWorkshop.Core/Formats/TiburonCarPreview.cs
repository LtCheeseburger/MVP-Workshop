using System.Numerics;

namespace MVPWorkshop.Core.Formats;

/// <summary>Preview-only wheel placement fitted to the authored fender arches.
/// The game's suspension/hinge transforms are runtime state, not stored in TMdl.</summary>
public static class TiburonCarPreview
{
    public sealed record Axle(float OuterX, float Y, float Z, int Evidence);
    public sealed record Assembly(Axle Front, Axle Rear, IReadOnlyDictionary<int, Matrix4x4[]> Wheels);
    public static bool IsUnplacedPart(TiburonModel.Part part) => part.Name.Equals("HIDP", StringComparison.OrdinalIgnoreCase)
        || part.Name.StartsWith("RoofFlap", StringComparison.OrdinalIgnoreCase);
    public static bool IsTirePart(TiburonModel.Part part) => part.Name is "Tire" or "Tire2" or "PaceCarTire";

    public static Assembly? Assemble(TiburonModel.Geometry geometry, IReadOnlyList<TiburonModel.Part> parts, string? body = null, bool openWheel = false)
    {
        var tire = parts.FirstOrDefault(IsTirePart);
        if (tire is null) return null;
        var tireIds = tire.Variants.SelectMany(v => v.Meshes).ToHashSet();
        var wheels = geometry.Meshes.Where(m => tireIds.Contains(m.Index)).ToArray();
        var normal = wheels.FirstOrDefault(m => tire.Variants.FirstOrDefault(v => v.Name == "Normal")?.Meshes.Contains(m.Index) == true) ?? wheels.FirstOrDefault();
        if (normal is null || normal.Positions.Length == 0) return null;
        float radius = Math.Max(normal.Positions.Max(p => p.Y) - normal.Positions.Min(p => p.Y), normal.Positions.Max(p => p.Z) - normal.Positions.Min(p => p.Z)) / 2;
        if (!float.IsFinite(radius) || radius <= 0) return null;
        var controlled = parts.SelectMany(p => p.Variants).SelectMany(v => v.Meshes).ToHashSet();
        var visible = parts.Where(p => !IsUnplacedPart(p)).SelectMany(p => p.Variants.ElementAtOrDefault(TiburonModel.DefaultVariant(p, body))?.Meshes ?? []).ToHashSet();
        var side = geometry.Meshes.Where(m => (!controlled.Contains(m.Index) || visible.Contains(m.Index))
            && (new[] { "lf_map", "ld_map", "lr_map" }.Contains(m.Texture, StringComparer.OrdinalIgnoreCase)
                || new[] { "lf", "ld", "lr" }.Contains(m.MaterialName.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9'), StringComparer.OrdinalIgnoreCase))).SelectMany(m => m.Positions).ToArray();
        if (side.Length == 0)
            side = geometry.Meshes.Where(m => (!controlled.Contains(m.Index) || visible.Contains(m.Index))
                && (m.MaterialName.Equals("prodCar", StringComparison.OrdinalIgnoreCase) || m.Texture.Equals("pacecar_map", StringComparison.OrdinalIgnoreCase))).SelectMany(m => m.Positions).ToArray();
        if (side.Length == 0 && !openWheel) return null;
        float width = side.Length == 0 ? 0 : side.Max(p => Math.Abs(p.X));
        var points = side.Where(p => p.X > width * .72f && p.Y > -radius * .1f && p.Y < radius * 2.4f)
            .DistinctBy(p => (MathF.Round(p.Y, 4), MathF.Round(p.Z, 4))).ToArray();
        Axle? Mount(string partName, bool front)
        {
            var part = parts.FirstOrDefault(p => p.Name == partName);
            if (part is null) return null;
            var meshes = part.Variants.ElementAtOrDefault(TiburonModel.DefaultVariant(part, body))?.Meshes ?? [];
            var mounts = geometry.Meshes.Where(m => meshes.Contains(m.Index)).SelectMany(m => m.Positions)
                .Where(p => p.X > radius * .5f && p.Y > radius * .65f && p.Y < radius * 1.6f && (front ? p.Z > radius * 2 : p.Z < -radius * 2)).ToArray();
            if (mounts.Length < 3) return null;
            float outer = mounts.Max(p => p.X);
            mounts = mounts.Where(p => p.X > outer - radius * .13f).Distinct().ToArray();
            if (mounts.Length < 3 || mounts.Max(p => p.Z) - mounts.Min(p => p.Z) > radius * .6f) return null;
            float tireWidth = normal.Positions.Max(p => p.X) - normal.Positions.Min(p => p.X);
            return new(mounts.Average(p => p.X) + tireWidth, (mounts.Min(p => p.Y) + mounts.Max(p => p.Y)) / 2, mounts.Average(p => p.Z), mounts.Length);
        }
        // The modified has exposed wheels rather than fender arches. Its visible
        // suspension/frame endpoints provide a separate preview placement source.
        var front = openWheel ? Mount("ShapeFrame", true) : Fit(points.Where(p => p.Z > 0).ToArray(), radius);
        var rear = openWheel ? Mount("ShapeTubFrame", false) : Fit(points.Where(p => p.Z < 0).ToArray(), radius);
        if (front is null || rear is null || front.Z - rear.Z < radius * 4) return null;
        var instances = new Dictionary<int, Matrix4x4[]>();
        foreach (var wheel in wheels)
        {
            float outer = wheel.Positions.Max(p => p.X), cy = (wheel.Positions.Max(p => p.Y) + wheel.Positions.Min(p => p.Y)) / 2,
                cz = (wheel.Positions.Max(p => p.Z) + wheel.Positions.Min(p => p.Z)) / 2;
            instances[wheel.Index] = new[] { front, rear }.SelectMany(axle => new[] { -1, 1 }.Select(sign =>
                Matrix4x4.CreateScale(sign, 1, 1) * Matrix4x4.CreateTranslation(sign * (axle.OuterX - outer - radius * .025f), axle.Y - cy, axle.Z - cz))).ToArray();
        }
        return new(front, rear, instances);
    }

    private static Axle? Fit(Vector3[] points, float radius)
    {
        // A bounded, deterministic consensus fit rejects flat door/bumper edges.
        if (points.Length > 96) points = points.Where((_, i) => i % (int)Math.Ceiling(points.Length / 96d) == 0).ToArray();
        int bestCount = 0; double bestError = double.MaxValue; Axle? result = null;
        for (int i = 0; i < points.Length; i++)
        for (int j = i + 1; j < points.Length; j++)
        for (int k = j + 1; k < points.Length; k++)
        {
            var a = points[i]; var b = points[j]; var c = points[k];
            double det = 2 * ((double)a.Y * (b.Z - c.Z) + (double)b.Y * (c.Z - a.Z) + (double)c.Y * (a.Z - b.Z));
            if (Math.Abs(det) < 1e-7) continue;
            double aa = a.Y * (double)a.Y + a.Z * (double)a.Z, bb = b.Y * (double)b.Y + b.Z * (double)b.Z, cc = c.Y * (double)c.Y + c.Z * (double)c.Z;
            double y = (aa * (b.Z - c.Z) + bb * (c.Z - a.Z) + cc * (a.Z - b.Z)) / det;
            double z = (aa * (c.Y - b.Y) + bb * (a.Y - c.Y) + cc * (b.Y - a.Y)) / det;
            double r = Math.Sqrt(Math.Pow(a.Y - y, 2) + Math.Pow(a.Z - z, 2));
            if (y < radius * .5 || y > radius * 1.5 || r < radius * 1.01 || r > radius * 1.4) continue;
            var good = points.Where(p => Math.Abs(Math.Sqrt(Math.Pow(p.Y - y, 2) + Math.Pow(p.Z - z, 2)) - r) < radius * .022).ToArray();
            if (good.Length < 8 || good.Length < bestCount || good.Max(p => p.Z) - good.Min(p => p.Z) < radius * .8 || good.Max(p => p.Y) - good.Min(p => p.Y) < radius * .65) continue;
            double error = good.Sum(p => Math.Abs(Math.Sqrt(Math.Pow(p.Y - y, 2) + Math.Pow(p.Z - z, 2)) - r));
            if (good.Length == bestCount && error >= bestError) continue;
            bestCount = good.Length; bestError = error;
            var x = good.Select(p => p.X).Order().ToArray(); result = new(x[x.Length / 2], (float)y, (float)z, good.Length);
        }
        return result;
    }
}

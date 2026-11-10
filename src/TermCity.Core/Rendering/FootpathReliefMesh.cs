using System.Numerics;
using TermCity.Core.Simulation;

namespace TermCity.Core.Rendering;

public readonly record struct FootpathReliefTriangle(Vector3 A, Vector3 B, Vector3 C, int Color);

public static class FootpathReliefMesh
{
    public static IReadOnlyList<FootpathReliefTriangle> Build(IReadOnlyList<FootpathReliefTriangle> source,
        Func<Vector2, float> boundaryDistance)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(boundaryDistance);
        var faces = new List<FootpathReliefTriangle>();
        var lifts = new Dictionary<Vector3, float>();
        foreach (var face in source)
        {
            float minimum = Math.Min(face.A.Y, Math.Min(face.B.Y, face.C.Y));
            float maximum = Math.Max(face.A.Y, Math.Max(face.B.Y, face.C.Y));
            if (maximum - minimum > TravelRoutes.MaximumFootpathStep) continue;
            var center = (face.A + face.B + face.C) / 3;
            var ring = new[] { face.A, (face.A + face.B) / 2, face.B, (face.B + face.C) / 2, face.C, (face.C + face.A) / 2 };
            foreach (var point in ring.Append(center))
            {
                float remaining = Math.Max(0, TravelRoutes.MaximumFootpathStep - (point.Y - minimum) - .00001f);
                float lift;
                if (!lifts.TryGetValue(point, out lift))
                {
                    float edge = boundaryDistance(new(point.X, point.Z));
                    if (!float.IsFinite(edge) || edge < 0)
                        throw new InvalidOperationException("Path boundary distance must be finite and non-negative.");
                    float t = Math.Clamp(edge / FootpathArtwork.ShoulderWidth, 0, 1);
                    lift = FootpathArtwork.ReliefHeight * t * t * (3 - 2 * t);
                }
                lifts[point] = Math.Min(lift, remaining);
            }
            for (int i = 0; i < ring.Length; i++)
                faces.Add(new(center, ring[i], ring[(i + 1) % ring.Length], face.Color));
        }
        Vector3 Lift(Vector3 point) => point + Vector3.UnitY * (FootpathArtwork.SurfaceLift + lifts[point]);
        return faces.Select(face => new FootpathReliefTriangle(Lift(face.A), Lift(face.B), Lift(face.C), face.Color)).ToArray();
    }
}

using Unity.Mathematics;

namespace MeshSlicer {

// A single interpolatable vertex carrying the attributes the slicer supports.
public struct Vertex
{
    public float3 Position;
    public float3 Normal;
    public float4 Tangent;
    public float2 UV;

    // Linear interpolation used when an edge is cut by the plane. Normal and the
    // xyz of tangent are renormalized; the tangent w (handedness) is taken from a.
    public static Vertex Lerp(in Vertex a, in Vertex b, float t)
    {
        var n = math.lerp(a.Normal, b.Normal, t);
        var tan = math.lerp(a.Tangent, b.Tangent, t);
        var tanXyz = math.lerp(a.Tangent.xyz, b.Tangent.xyz, t);
        return new Vertex
        {
            Position = math.lerp(a.Position, b.Position, t),
            Normal = math.normalizesafe(n, new float3(0, 1, 0)),
            Tangent = new float4(math.normalizesafe(tanXyz, new float3(1, 0, 0)), a.Tangent.w),
            UV = math.lerp(a.UV, b.UV, t)
        };
    }
}

} // namespace MeshSlicer

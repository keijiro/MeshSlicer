using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace MeshSlicer {

// Interleaved output vertex layout shared by the Burst slicer and the Advanced
// Mesh API upload (Position, Normal, Tangent, TexCoord0).
public struct SliceVertex
{
    public float3 Position;
    public float3 Normal;
    public float4 Tangent;
    public float2 Uv;

    public static VertexAttributeDescriptor[] Layout => new[]
    {
        new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
        new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3),
        new VertexAttributeDescriptor(VertexAttribute.Tangent, VertexAttributeFormat.Float32, 4),
        new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2),
    };
}

} // namespace MeshSlicer

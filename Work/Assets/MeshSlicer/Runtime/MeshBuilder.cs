using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace MeshSlicer {

// Accumulates triangles into vertex/index buffers and bakes them into a Mesh.
// Used by the naive slicer; correctness first, no vertex sharing.
sealed class MeshBuilder
{
    readonly List<float3> _positions = new();
    readonly List<float3> _normals = new();
    readonly List<float4> _tangents = new();
    readonly List<float2> _uvs = new();
    readonly List<int> _indices = new();

    public int TriangleCount => _indices.Count / 3;

    public void AddTriangle(in Vertex a, in Vertex b, in Vertex c)
    {
        var i = _positions.Count;
        Push(a);
        Push(b);
        Push(c);
        _indices.Add(i);
        _indices.Add(i + 1);
        _indices.Add(i + 2);
    }

    void Push(in Vertex v)
    {
        _positions.Add(v.Position);
        _normals.Add(v.Normal);
        _tangents.Add(v.Tangent);
        _uvs.Add(v.UV);
    }

    public Mesh ToMesh(string name)
    {
        var mesh = new Mesh { name = name };
        if (_positions.Count > ushort.MaxValue)
            mesh.indexFormat = IndexFormat.UInt32;

        var verts = new Vector3[_positions.Count];
        var norms = new Vector3[_positions.Count];
        var tans = new Vector4[_positions.Count];
        var uvs = new Vector2[_positions.Count];
        for (var i = 0; i < _positions.Count; i++)
        {
            verts[i] = _positions[i];
            norms[i] = _normals[i];
            tans[i] = _tangents[i];
            uvs[i] = _uvs[i];
        }

        mesh.vertices = verts;
        mesh.normals = norms;
        mesh.tangents = tans;
        mesh.uv = uvs;
        mesh.triangles = _indices.ToArray();
        mesh.RecalculateBounds();
        return mesh;
    }
}

} // namespace MeshSlicer

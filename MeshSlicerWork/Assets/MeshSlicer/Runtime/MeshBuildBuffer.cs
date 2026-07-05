using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace MeshSlicer {

// Accumulates vertices/indices for one output half and bakes a Mesh.
// Deduplicates source vertices (by original index) and wall intersection
// vertices (by source-edge key) so shared attributes are reused, while cap
// vertices are kept separate (they carry the flat cap normal).
sealed class MeshBuildBuffer
{
    readonly List<Vector3> _pos = new();
    readonly List<Vector3> _nrm = new();
    readonly List<Vector4> _tan = new();
    readonly List<Vector2> _uv = new();
    readonly List<int> _idx = new();

    readonly Dictionary<int, int> _origMap = new();   // source vertex -> local
    readonly Dictionary<long, int> _wallMap = new();  // source-edge key -> local
    readonly Dictionary<int, int> _capMap = new();    // boundary id -> local

    public int TriangleCount => _idx.Count / 3;

    public int AddOriginal(int src, float3 p, float3 n, float4 t, float2 uv)
    {
        if (_origMap.TryGetValue(src, out var i)) return i;
        i = Emit(p, n, t, uv);
        _origMap[src] = i;
        return i;
    }

    public int AddWall(long edgeKey, float3 p, float3 n, float4 t, float2 uv)
    {
        if (_wallMap.TryGetValue(edgeKey, out var i)) return i;
        i = Emit(p, n, t, uv);
        _wallMap[edgeKey] = i;
        return i;
    }

    public int AddCap(int boundaryId, float3 p, float3 n, float4 t, float2 uv)
    {
        if (_capMap.TryGetValue(boundaryId, out var i)) return i;
        i = Emit(p, n, t, uv);
        _capMap[boundaryId] = i;
        return i;
    }

    public void AddTriangle(int a, int b, int c)
    {
        _idx.Add(a); _idx.Add(b); _idx.Add(c);
    }

    int Emit(float3 p, float3 n, float4 t, float2 uv)
    {
        var i = _pos.Count;
        _pos.Add(p); _nrm.Add(n); _tan.Add(t); _uv.Add(uv);
        return i;
    }

    public Mesh ToMesh(string name)
    {
        if (_idx.Count == 0) return null;

        var mesh = new Mesh { name = name };
        if (_pos.Count > 65535) mesh.indexFormat = IndexFormat.UInt32;
        mesh.SetVertices(_pos);
        mesh.SetNormals(_nrm);
        mesh.SetTangents(_tan);
        mesh.SetUVs(0, _uv);
        mesh.SetTriangles(_idx, 0);
        mesh.RecalculateBounds();
        return mesh;
    }
}

} // namespace MeshSlicer

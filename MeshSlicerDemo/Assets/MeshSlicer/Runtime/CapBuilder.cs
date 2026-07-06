using System.Collections.Generic;
using Unity.Mathematics;

namespace MeshSlicer {

// Collects the on-plane segments produced while cutting triangles, welds their
// endpoints, chains them into closed loops, and triangulates the cross section
// (supporting holes) into a set of cap triangles expressed as plane points.
sealed class CapBuilder
{
    struct Edge { public int A; public int B; }

    readonly float3 _n, _u, _v;   // plane basis (cross(u,v) == n)
    readonly float _quant;

    readonly List<float3> _points = new();          // welded boundary points (3D)
    readonly Dictionary<int3, int> _pointMap = new();
    readonly List<Edge> _edges = new();
    readonly HashSet<long> _edgeSet = new();

    public IReadOnlyList<float3> Points => _points;

    public CapBuilder(float3 planeNormal, float boundsExtent)
    {
        _n = math.normalize(planeNormal);
        _u = math.normalize(math.abs(_n.x) < 0.9f
            ? math.cross(_n, new float3(1, 0, 0))
            : math.cross(_n, new float3(0, 1, 0)));
        _v = math.cross(_n, _u);
        _quant = math.max(boundsExtent * 1e-5f, 1e-6f);
    }

    public void AddSegment(float3 p0, float3 p1)
    {
        var a = Weld(p0);
        var b = Weld(p1);
        if (a == b) return; // degenerate
        var key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        if (!_edgeSet.Add(key)) return; // dedup shared boundary edges
        _edges.Add(new Edge { A = a, B = b });
    }

    int Weld(float3 p)
    {
        var q = new int3(math.round(p / _quant));
        if (_pointMap.TryGetValue(q, out var idx)) return idx;
        idx = _points.Count;
        _pointMap.Add(q, idx);
        _points.Add(p);
        return idx;
    }

    // Chains edges into loops and triangulates. Returns triangles as triples of
    // indices into Points. Winding is CCW in the (u,v) basis, i.e. normal == +n.
    public List<int> BuildCapTriangles()
    {
        var loops = ChainLoops();
        if (loops.Count == 0) return new List<int>();

        var pts2D = new float2[_points.Count];
        for (var i = 0; i < _points.Count; i++)
            pts2D[i] = new float2(math.dot(_points[i], _u), math.dot(_points[i], _v));

        var tris = new List<int>();
        PolygonTriangulator.Triangulate(loops, pts2D, tris);
        return tris;
    }

    List<List<int>> ChainLoops()
    {
        var adjacency = new Dictionary<int, List<int>>();
        void Link(int a, int b)
        {
            if (!adjacency.TryGetValue(a, out var l)) { l = new List<int>(); adjacency[a] = l; }
            l.Add(b);
        }
        for (var i = 0; i < _edges.Count; i++)
        {
            Link(_edges[i].A, i);
            Link(_edges[i].B, i);
        }

        var used = new bool[_edges.Count];
        var loops = new List<List<int>>();

        for (var s = 0; s < _edges.Count; s++)
        {
            if (used[s]) continue;
            var start = _edges[s].A;
            var cur = _edges[s].B;
            used[s] = true;
            var loop = new List<int> { start, cur };

            while (cur != start)
            {
                var next = NextEdge(adjacency, used, cur);
                if (next < 0) break; // open chain (non-manifold input); drop trailing point
                used[next] = true;
                cur = _edges[next].A == cur ? _edges[next].B : _edges[next].A;
                if (cur != start) loop.Add(cur);
            }

            if (loop.Count >= 3) loops.Add(loop);
        }
        return loops;
    }

    static int NextEdge(Dictionary<int, List<int>> adjacency, bool[] used, int vertex)
    {
        if (!adjacency.TryGetValue(vertex, out var l)) return -1;
        foreach (var e in l) if (!used[e]) return e;
        return -1;
    }
}

} // namespace MeshSlicer

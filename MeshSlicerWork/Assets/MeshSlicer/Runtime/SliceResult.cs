using UnityEngine;

namespace MeshSlicer {

// Result of a plane slice: two independent meshes, one for each half-space.
// Positive holds the geometry on the side the plane normal points to
// (signed distance >= 0); Negative holds the opposite side. Either may be
// null when the source lies entirely on one side of the plane.
public readonly struct SliceResult
{
    public readonly Mesh Positive;
    public readonly Mesh Negative;

    public SliceResult(Mesh positive, Mesh negative)
    {
        Positive = positive;
        Negative = negative;
    }
}

} // namespace MeshSlicer

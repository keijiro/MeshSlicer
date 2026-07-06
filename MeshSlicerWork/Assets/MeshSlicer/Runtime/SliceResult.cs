using UnityEngine;

namespace MeshSlicer {

// Result of a plane cut. Positive holds the piece on the side the plane normal
// points to (signed distance >= 0); Negative holds the other side. Either can be
// null when the source lies entirely on one side of the plane.
public struct SliceResult
{
    public Mesh Positive;
    public Mesh Negative;
}

} // namespace MeshSlicer

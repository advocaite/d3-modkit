using System.Numerics;

namespace D3ModTool.Import;

/// <summary>
/// glTF space (Y up, metres) -> D3 model space (Z up, feet, forward +X, left +Y; appearance_2.8.0.md §1). Always a proper
/// rotation (no mirroring, winding kept) times a unit scale. <c>--forward</c> names the glTF axis the model faces:
/// <list type="bullet">
/// <item><c>+z</c>: the glTF / Blender convention for characters: (x, y, z) -> (z, x, y). Default for skinned models.</item>
/// <item><c>+x</c>: (x, y, z) -> (x, -z, y). Default for static props (the mapping of the first importer, 2026-11-13).</item>
/// <item><c>-z</c>: (x, y, z) -> (-z, -x, y); <c>-x</c>: (x, y, z) -> (-x, z, y).</item>
/// </list>
/// D3 X = the forward axis, D3 Z = glTF +Y, D3 Y = Z x X (right-handed).
/// </summary>
public sealed class AxisMap
{
    readonly Vector3 _x, _y, _z;    // the glTF directions of D3 +X, +Y, +Z
    public float Unit { get; }
    public string Forward { get; }

    public AxisMap(string forward, float unit)
    {
        Forward = forward;
        Unit = unit;
        _x = forward.ToLowerInvariant() switch
        {
            "+z" or "z" => Vector3.UnitZ,
            "-z" => -Vector3.UnitZ,
            "+x" or "x" => Vector3.UnitX,
            "-x" => -Vector3.UnitX,
            _ => throw new ArgumentException($"--forward must be +z, -z, +x or -x (the glTF axis the model faces), not '{forward}'"),
        };
        _z = Vector3.UnitY;
        _y = Vector3.Cross(_z, _x);
    }

    /// <summary>A direction (no unit scale). The axes are signed glTF axes, so each component is one signed glTF component
    /// (same bits as the first importer's (x, -z, y), including the sign of zero).</summary>
    public Vector3 Dir(Vector3 g) => new(Pick(g, _x), Pick(g, _y), Pick(g, _z));

    static float Pick(Vector3 g, Vector3 axis) =>
        axis.X != 0 ? axis.X * g.X : axis.Y != 0 ? axis.Y * g.Y : axis.Z * g.Z;

    /// <summary>A position or translation (unit scale applied).</summary>
    public Vector3 Pos(Vector3 g) => Dir(g) * Unit;

    /// <summary>A rotation: the same rotation expressed in D3 axes (the axis is mapped, the angle kept).</summary>
    public Quaternion Rot(Quaternion q)
    {
        var v = Dir(new Vector3(q.X, q.Y, q.Z));
        return Quaternion.Normalize(new Quaternion(v, q.W));
    }
}

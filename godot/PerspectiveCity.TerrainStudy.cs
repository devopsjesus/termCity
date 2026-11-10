using Godot;
using TermCity.Core.Rendering;

namespace TermCity.GodotApp;

public partial class PerspectiveCity
{
    internal TerrainStudyTreatment TerrainTreatment { get; init; } = TerrainStudyTreatment.Balanced;

    private void ConfigureTerrainStudy()
    {
        if (!TerrainStudyFixture.LitInfrastructure(TerrainTreatment)) return;
        foreach (var material in new[] { _roadMaterial, _cliffMaterial })
        {
            material.ShadingMode = BaseMaterial3D.ShadingModeEnum.PerPixel;
            material.Roughness = 1;
            material.MetallicSpecular = 0;
        }
    }

    internal bool TerrainStudyNormalsCorrect
    {
        get
        {
            foreach (var chunk in _roadChunks.Values)
                foreach (var mesh in new[] { chunk.Ground, chunk.Roads, chunk.Banks })
                {
                    if (mesh is null || mesh.GetSurfaceCount() == 0) continue;
                    using var arrays = mesh.SurfaceGetArrays(0);
                    if (arrays[(int)Mesh.ArrayType.Normal].VariantType != Variant.Type.PackedVector3Array) return false;
                    var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                    var normals = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
                    if (vertices.Length != normals.Length) return false;
                    var indices = arrays[(int)Mesh.ArrayType.Index].VariantType == Variant.Type.Nil
                        ? Enumerable.Range(0, vertices.Length).ToArray()
                        : arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
                    for (int i = 0; i < indices.Length; i += 3)
                    {
                        int a = indices[i], b = indices[i + 1], c = indices[i + 2];
                        var cross = (vertices[b] - vertices[a]).Cross(vertices[c] - vertices[a]);
                        if (cross.LengthSquared() < 1e-10f) continue;
                        if (!normals[a].IsFinite() || normals[a].LengthSquared() < .99f ||
                            cross.Dot(normals[a]) > .000001f) return false;
                    }
                }
            return true;
        }
    }
}

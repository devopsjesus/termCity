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
                    var arrays = mesh.SurfaceGetArrays(0);
                    if (arrays[(int)Mesh.ArrayType.Normal].VariantType != Variant.Type.PackedVector3Array) return false;
                    var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                    var normals = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
                    if (vertices.Length != normals.Length) return false;
                    for (int i = 0; i < vertices.Length; i += 3)
                    {
                        var cross = (vertices[i + 1] - vertices[i]).Cross(vertices[i + 2] - vertices[i]);
                        if (cross.LengthSquared() < 1e-10f) continue;
                        if (!normals[i].IsFinite() || normals[i].LengthSquared() < .99f ||
                            cross.Dot(normals[i]) > .000001f) return false;
                    }
                }
            return true;
        }
    }
}

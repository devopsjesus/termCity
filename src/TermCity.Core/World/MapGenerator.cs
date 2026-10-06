using TermCity.Core.Terrain;

namespace TermCity.Core.World;

/// <summary>Builds a fresh map: terrain generators, then existing roads, then natural features.</summary>
public static class MapGenerator
{
    public static GameMap Generate(int width, int height, int seed, GameContent content)
    {
        var map = new GameMap(width, height, content);
        var context = new GenerationContext(map, seed);

        foreach (var terrain in content.Terrains.Where(t => t.Generator is not null).OrderBy(t => t.Generator!.Order).ThenBy(t => t.Id))
        {
            terrain.Generator!.Generate(context, terrain);
        }

        HighwayGenerator.Generate(context);

        foreach (var feature in content.Features.Where(f => f.Generator is not null).OrderBy(f => f.Generator!.Order).ThenBy(f => f.Id))
        {
            feature.Generator!.Generate(context, feature);
        }

        return map;
    }
}

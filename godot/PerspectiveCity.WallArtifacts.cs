using Godot;
using TermCity.Core.Simulation;

namespace TermCity.GodotApp;

public partial class PerspectiveCity
{
    private sealed record WallArtifact(WallGeometrySnapshot Geometry,
        Dictionary<int, Transform3D[]> Bodies, Dictionary<int, Transform3D[]> Extensions,
        (Aabb Bounds, TermCity.Core.Util.Pos Anchor, int Color, Transform3D Transform)[] Picks,
        Transform3D[] Highlights, WallTopFace[] Tops, MovingDefensePart[] Moving, AimedPart[] Aimed,
        int TimberParts, bool AccessLadder);
    private readonly Dictionary<int, WallArtifact> _wallArtifacts = [];
    internal int WallArtifactsReusedLastSync { get; private set; }

    private void DrawOrReuseWall(SettlementState settlement, WallGeometrySnapshot geometry, bool force)
    {
        if (!force && _wallArtifacts.TryGetValue(geometry.Wall.Id, out var artifact) &&
            SameWallGeometry(artifact.Geometry, geometry))
        {
            AppendWallArtifact(artifact);
            WallArtifactsReusedLastSync++;
            return;
        }
        var bodyStarts = _wallBatches.ToDictionary(pair => pair.Key, pair => pair.Value.Count);
        var extensionStarts = _extensionWallBatches.ToDictionary(pair => pair.Key, pair => pair.Value.Count);
        int picking = _siegePickBoxes.Count, tops = _wallTopFaces.Count;
        int moving = _movingDefenseParts.Count, aimed = _aimedParts.Count, timber = TimberWallPartsDrawn;
        DrawWall(_wallBatches, settlement, geometry.Wall);
        var picks = Enumerable.Range(picking, _siegePickBoxes.Count - picking).Select(index =>
        {
            var box = _siegePickBoxes[index];
            return (box.Bounds, box.Anchor, box.Color, _siegePickTransforms[index]);
        }).ToArray();
        _wallArtifacts[geometry.Wall.Id] = new(geometry,
            Capture(_wallBatches, bodyStarts), Capture(_extensionWallBatches, extensionStarts),
            picks, _wallHighlightBoxes[geometry.Wall.Id].ToArray(), _wallTopFaces.Skip(tops).ToArray(),
            _movingDefenseParts.Skip(moving).Select(part => part with
                { Instance = part.Instance - bodyStarts.GetValueOrDefault(part.Color), Pick = part.Pick - picking }).ToArray(),
            _aimedParts.Skip(aimed).Select(part => part with
                { Instance = part.Instance - bodyStarts.GetValueOrDefault(part.Color), Pick = part.Pick - picking }).ToArray(),
            TimberWallPartsDrawn - timber, _accessLadderWallIds.Contains(geometry.Wall.Id));
        WallsRegeneratedLastSync++;

        static Dictionary<int, Transform3D[]> Capture(Dictionary<int, List<Transform3D>> batches,
            Dictionary<int, int> starts) => batches.Where(pair => pair.Value.Count > starts.GetValueOrDefault(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value.Skip(starts.GetValueOrDefault(pair.Key)).ToArray());
    }

    private void AppendWallArtifact(WallArtifact artifact)
    {
        int picking = _siegePickBoxes.Count;
        var starts = artifact.Bodies.Keys.ToDictionary(color => color, color => _wallBatches.GetValueOrDefault(color)?.Count ?? 0);
        foreach (var (color, transforms) in artifact.Bodies)
        {
            if (!_wallBatches.TryGetValue(color, out var batch)) _wallBatches[color] = batch = [];
            batch.AddRange(transforms);
        }
        foreach (var (color, transforms) in artifact.Extensions)
        {
            if (!_extensionWallBatches.TryGetValue(color, out var batch)) _extensionWallBatches[color] = batch = [];
            batch.AddRange(transforms);
        }
        foreach (var box in artifact.Picks)
        {
            int index = _siegePickBoxes.Count;
            _siegePickBoxes.Add((box.Bounds, box.Anchor, box.Color));
            _siegePickTransforms[index] = box.Transform;
            _siegePickWallIds[index] = artifact.Geometry.Wall.Id;
        }
        _wallHighlightBoxes.Add(artifact.Geometry.Wall.Id, new(artifact.Highlights));
        _wallTopFaces.AddRange(artifact.Tops);
        _movingDefenseParts.AddRange(artifact.Moving.Select(part => part with
            { Instance = part.Instance + starts[part.Color], Pick = part.Pick + picking }));
        _aimedParts.AddRange(artifact.Aimed.Select(part => part with
            { Instance = part.Instance + starts[part.Color], Pick = part.Pick + picking }));
        if (artifact.AccessLadder) _accessLadderWallIds.Add(artifact.Geometry.Wall.Id);
        TimberWallPartsDrawn += artifact.TimberParts;
    }
}

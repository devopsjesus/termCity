using Godot;

namespace TermCity.GodotApp;

public partial class PerspectiveCity
{
    internal readonly record struct PreparationTimings(double Ground, double Borders, double Textures,
        double Objects, double Buildings, double Encoding, double Uploads, long GroundNativeBytes,
        int Snapshots, double SnapshotMilliseconds);
    private double _groundPreparationMilliseconds, _borderPreparationMilliseconds, _texturePreparationMilliseconds;
    private double _objectEmissionMilliseconds, _buildingEmissionMilliseconds, _objectEncodingMilliseconds, _objectUploadMilliseconds;
    private int _groundSnapshotsCaptured;
    private double _groundSnapshotMilliseconds;
    internal PreparationTimings PreparationProfile => new(_groundPreparationMilliseconds, _borderPreparationMilliseconds,
        _texturePreparationMilliseconds, _objectEmissionMilliseconds, _buildingEmissionMilliseconds,
        _objectEncodingMilliseconds, _objectUploadMilliseconds, GroundUploadBytes, _groundSnapshotsCaptured, _groundSnapshotMilliseconds);
    internal string PreparationPendingDescription => $"ground={GroundVisiblePending} paths={RequiredPathWorkPending} " +
        $"path-background={PathWorkPending} path-ground={_groundPathsPending} path-worker={_pathMeshTask is not null} " +
        $"path-upload={_pathUpload is not null} path-published={FootpathMeshesPublished} " +
        $"camera={_cameraGeometryPending} objects={ObjectStreamingPending} requests={_objectRequests.Count} " +
        $"preparing={_preparingObject?.Key} upload={_uploadingObject?.Key} encodings={_objectEncodings.Count}";
    internal bool VisibleTerrainReady => !GroundVisiblePending;
    internal bool PathPresentationReady => !RequiredPathWorkPending;
    internal bool CameraPresentationReady => !_cameraGeometryPending;

    internal void PrintPreparationProfile(string label, double zoom, PreparationTimings before)
    {
        var after = PreparationProfile;
        GD.Print($"TERMCITY_PREPARATION_PROFILE experiment={label} zoom={zoom} " +
            $"overview-lod={_overviewLodActive} " +
            $"ground={after.Ground - before.Ground:0.0}ms borders={after.Borders - before.Borders:0.0}ms " +
            $"textures={after.Textures - before.Textures:0.0}ms objects={after.Objects - before.Objects:0.0}ms " +
            $"buildings={after.Buildings - before.Buildings:0.0}ms encoding={after.Encoding - before.Encoding:0.0}ms " +
            $"object-uploads={after.Uploads - before.Uploads:0.0}ms ground-native-bytes={after.GroundNativeBytes - before.GroundNativeBytes} " +
            $"snapshots={after.Snapshots - before.Snapshots} snapshot-host={after.SnapshotMilliseconds - before.SnapshotMilliseconds:0.0}ms");
    }
}

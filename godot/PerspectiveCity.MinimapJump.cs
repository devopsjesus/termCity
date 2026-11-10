using Godot;
using TermCity.Core.Session;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.GodotApp;

public partial class PerspectiveCity
{
    private readonly SubViewport _lastCompleteFrame = new()
    {
        Disable3D = true, TransparentBg = false,
        RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
    };
    private readonly TextureRect _frameCopy = new()
    {
        ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
        StretchMode = TextureRect.StretchModeEnum.Scale,
        TextureFilter = CanvasItem.TextureFilterEnum.Linear,
    };
    private bool _completeFrameAvailable, _copyingCompleteFrame, _jumpReadyToPublish;
    private GameMap? _presentedMap;
    private Vector2[]? _presentedCorners;
    private (float Azimuth, float Elevation) _presentedOrientation;
    private long _jumpStarted;
    private int _jumpBuilt, _jumpTextures, _jumpGroundUploads;
    private long _jumpUploadBytes;
    private GameSession? _jumpSession;
    private bool _jumpCompletionPending;
    internal double MinimapFirstResponseMilliseconds { get; private set; }
    internal const string JumpLoadingMessage = "Preparing the map destination; minimap navigation remains available.";
    internal bool MinimapJumpPending { get; private set; }
    internal int MinimapJumpGeneration { get; private set; }
    internal int MinimapJumpsPublished { get; private set; }
    internal double MinimapJumpMilliseconds { get; private set; }
    internal bool CompleteFrameAvailable => _completeFrameAvailable;
    internal Texture2D PresentedTexture => MinimapJumpPending && _completeFrameAvailable
        ? _lastCompleteFrame.GetTexture() : Texture;
    internal Vector2[] PresentedGroundCorners() =>
        MinimapJumpPending && _presentedCorners is { } corners ? corners : GroundViewCorners();
    internal (float Azimuth, float Elevation) PresentedOrientation =>
        MinimapJumpPending ? _presentedOrientation : (Azimuth, Elevation);

    internal void BeginMinimapJump(GameSession session)
    {
        RecordMinimapNavigation();
        if (!AtomicMinimapPresentationEnabled) return;
        if (DisplayServer.GetName() == "headless") return;
        if (!_completeFrameAvailable || !ReferenceEquals(_presentedMap, session.Game.Map)) return;
        MinimapJumpGeneration++;
        _objectRequestState = null;
        MinimapJumpPending = true;
        _jumpReadyToPublish = false;
        _copyingCompleteFrame = false;
        _lastCompleteFrame.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
        _jumpSession = session;
        _jumpCompletionPending = true;
        _jumpStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        _jumpBuilt = ObjectChunksBuilt;
        _jumpTextures = GroundTexturePacketsBuilt;
        _jumpGroundUploads = GroundChunksUploaded;
        _jumpUploadBytes = ObjectUploadBytes;
        session.SetMessage(JumpLoadingMessage);
    }

    internal void PrepareCompleteFrame()
    {
        if (!AtomicMinimapPresentationEnabled) return;
        if (DisplayServer.GetName() == "headless") return;
        if (_jumpSession is { } session &&
            (!ReferenceEquals(_presentedMap, session.Game.Map) || !ReferenceEquals(_presentedMap, _map)))
            CancelMinimapJump();
        if (MinimapJumpPending)
        {
            _jumpReadyToPublish = TerrainFirstPresentation
                ? !GroundVisiblePending && !_cameraGeometryPending && !_waitingForCameraPaths
                : !SceneGeometryPending;
            _lastCompleteFrame.RenderTargetUpdateMode = _jumpReadyToPublish
                ? SubViewport.UpdateMode.Once : SubViewport.UpdateMode.Disabled;
            if (_jumpReadyToPublish)
            {
                _lastCompleteFrame.Size = RenderSize;
                _frameCopy.Size = RenderSize;
                RequestRender();
            }
            return;
        }
        _copyingCompleteFrame = !SceneGeometryPending && RenderSize.X > 0 && RenderSize.Y > 0;
        if (_copyingCompleteFrame)
        {
            _lastCompleteFrame.Size = RenderSize;
            _frameCopy.Size = RenderSize;
        }
        _lastCompleteFrame.RenderTargetUpdateMode = _copyingCompleteFrame
            ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
    }

    private void CompleteFrameDrawn()
    {
        if (!IsInsideTree() || ProcessMode == ProcessModeEnum.Disabled) return;
        if (MinimapJumpPending)
        {
            if (!_jumpReadyToPublish || GroundVisiblePending || _cameraGeometryPending ||
                _waitingForCameraPaths || !TerrainFirstPresentation && SceneGeometryPending) return;
            MinimapJumpPending = false;
            _jumpReadyToPublish = false;
            MinimapJumpsPublished++;
            RememberPopulatedDestination();
            _presentedMap = _map;
            _presentedCorners = GroundViewCorners();
            _presentedOrientation = (Azimuth, Elevation);
            MinimapFirstResponseMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(_jumpStarted).TotalMilliseconds;
            if (GetParent() is Control publishedParent) publishedParent.QueueRedraw();
        }
        if (_jumpCompletionPending && !SceneGeometryPending && !MinimapJumpPending)
        {
            _jumpCompletionPending = false;
            MinimapJumpMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(_jumpStarted).TotalMilliseconds;
            GD.Print($"TERMCITY_MINIMAP_JUMP generation={MinimapJumpGeneration} ready={MinimapJumpMilliseconds:0.0}ms " +
                $"first-response={MinimapFirstResponseMilliseconds:0.0}ms " +
                $"objects-built={ObjectChunksBuilt - _jumpBuilt} texture-packets={GroundTexturePacketsBuilt - _jumpTextures} " +
                $"ground-uploads={GroundChunksUploaded - _jumpGroundUploads} object-upload-bytes={ObjectUploadBytes - _jumpUploadBytes} " +
                $"gpu-resident={DestinationGpuBytes} gpu-budget={GpuCacheBudgetBytes} gpu-overage={GpuCacheOverageBytes}");
            if (_jumpSession is { } session && session.Message == JumpLoadingMessage)
                session.SetMessage("Map destination ready.");
            _jumpSession = null;
            if (GetParent() is Control parent) parent.QueueRedraw();
        }
        if (!MinimapJumpPending && _copyingCompleteFrame && !SceneGeometryPending)
        {
            _completeFrameAvailable = true;
            _presentedMap = _map;
            _presentedCorners = GroundViewCorners();
            _presentedOrientation = (Azimuth, Elevation);
        }
    }

    private void CancelMinimapJump()
    {
        MinimapJumpPending = false;
        _jumpReadyToPublish = _completeFrameAvailable = _copyingCompleteFrame = false;
        _jumpCompletionPending = false;
        _jumpSession = null;
        _presentedMap = null;
        _presentedCorners = null;
        _lastCompleteFrame.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
    }

    internal async Task AwaitCompleteFrame()
    {
        while (!_completeFrameAvailable)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    internal void ValidatePendingJumpLifecycle(GameSession session)
    {
        BeginMinimapJump(session);
        if (!MinimapJumpPending) throw new InvalidOperationException("Lifecycle check did not begin a pending jump.");
        SetActive(false);
        if (MinimapJumpPending || _jumpSession is not null)
            throw new InvalidOperationException("Disabling perspective did not cancel its minimap presentation.");
        SetActive(true);
    }
}

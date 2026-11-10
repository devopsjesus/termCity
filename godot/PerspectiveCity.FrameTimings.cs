using Godot;

namespace TermCity.GodotApp;

public partial class PerspectiveCity
{
    internal readonly record struct FrameTimings(ulong Frame, double RenderCpu, double RenderGpu,
        double SetupCpu, double Host, double Actors, double Walls, int RegeneratedWalls, long Allocated,
        int Gen0, int Gen1, int Gen2, double WallAssembly, double WallPages, double WallTops,
        double Extensions, double Clipping, double TopUploads, int TopWrites);
    internal double WallExtensionMilliseconds, WallClippingMilliseconds, WallTopUploadMilliseconds;
    internal int WallTopUploadsLastSync;
    private ulong _actorTimingFrame, _wallTimingFrame;
    private long _lastFrameAllocated;
    private int _lastGen0, _lastGen1, _lastGen2;

    internal void EnableFrameTimings(Viewport root)
    {
        RenderingServer.ViewportSetMeasureRenderTime(_viewport.GetViewportRid(), true);
        RenderingServer.ViewportSetMeasureRenderTime(root.GetViewportRid(), true);
        _lastFrameAllocated = GC.GetTotalAllocatedBytes();
        _lastGen0 = GC.CollectionCount(0);
        _lastGen1 = GC.CollectionCount(1);
        _lastGen2 = GC.CollectionCount(2);
    }

    internal FrameTimings ReadFrameTimings(Viewport root)
    {
        ulong frame = Engine.GetProcessFrames();
        long allocated = GC.GetTotalAllocatedBytes();
        int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
        var result = new FrameTimings(frame,
            RenderingServer.ViewportGetMeasuredRenderTimeCpu(_viewport.GetViewportRid()) +
                RenderingServer.ViewportGetMeasuredRenderTimeCpu(root.GetViewportRid()),
            RenderingServer.ViewportGetMeasuredRenderTimeGpu(_viewport.GetViewportRid()) +
                RenderingServer.ViewportGetMeasuredRenderTimeGpu(root.GetViewportRid()),
            RenderingServer.GetFrameSetupTimeCpu(), RendererFrameMilliseconds,
            _actorTimingFrame == frame ? SiegeActorBuildMilliseconds : 0,
            _wallTimingFrame == frame ? SiegeGeometryBuildMilliseconds : 0,
            _wallTimingFrame == frame ? WallsRegeneratedLastSync : 0,
            allocated - _lastFrameAllocated, gen0 - _lastGen0, gen1 - _lastGen1, gen2 - _lastGen2,
            _wallTimingFrame == frame ? WallAssemblyMilliseconds : 0,
            _wallTimingFrame == frame ? WallPageMilliseconds : 0,
            _wallTimingFrame == frame ? WallTopMilliseconds : 0,
            _wallTimingFrame == frame ? WallExtensionMilliseconds : 0,
            _wallTimingFrame == frame ? WallClippingMilliseconds : 0,
            _wallTimingFrame == frame ? WallTopUploadMilliseconds : 0,
            _wallTimingFrame == frame ? WallTopUploadsLastSync : 0);
        _lastFrameAllocated = allocated;
        _lastGen0 = gen0; _lastGen1 = gen1; _lastGen2 = gen2;
        return result;
    }
}

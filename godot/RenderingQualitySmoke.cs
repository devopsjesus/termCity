using Godot;
using TermCity.Core.Util;
using static TermCity.GodotApp.SettlementSmoke;

namespace TermCity.GodotApp;

internal static class RenderingQualitySmoke
{
    internal static async Task Run(Main host, string? capturePath)
    {
        if (DisplayServer.GetName() == "headless") return;
        var renderer = host.Map.Perspective!;
        Require(host.Map.TextureFilter == CanvasItem.TextureFilterEnum.Nearest &&
            host.Map.SmoothPerspectiveComposite && renderer.StablePixelMaterials,
            "The 3D scene must use smooth compositing and linear mipmapped anisotropic materials.");
        Require(PerspectiveCity.NativePixelSize(new(101.25f, 79.5f), new(1.5f, 2)) == new Vector2I(152, 159),
            "Fractional physical-pixel extents must round up before supersampling.");
        var originalScale = host.Map.Scale;
        int geometry = renderer.GeometryBuilds;
        host.Map.Scale = originalScale * 1.25f;
        renderer.Sync(host.Session, host.Map.Size);
        await Frames(host);
        Require(renderer.RenderSize == renderer.NativeRenderSize * PerspectiveCity.SupersampleScale &&
            renderer.DisplayPixelScale.IsEqualApprox(originalScale * 1.25f),
            "Changing canvas scale must resize the target without relying on the OS Retina multiplier.");
        Require(renderer.GeometryBuilds == geometry, "Changing framebuffer density must not rebuild city geometry.");
        host.Map.Scale = originalScale;
        renderer.Sync(host.Session, host.Map.Size);
        await Frames(host);
        var session = host.Session;
        var game = session.Game;
        int zoom = session.ZoomLevel;
        bool areas = session.AreaOverlaysVisible;
        var center = new Pos(session.CameraX + session.ViewWidth * session.Stride / 2,
            session.CameraY + session.ViewHeight * session.Stride / 2);
        var green = game.Map.Content.Buildings.Get("Village Green");
        var site = game.Settlement!.Bounds.Cells().First(p => game.CanPlaceBuilding(green, p.X, p.Y));
        session.PlaceCursor(site);
        session.PreviewBuilding(green);
        session.SetAreaOverlays(true);
        foreach (int level in new[] { -1, 0, 1 })
        {
            session.SetZoom(level);
            host.Map.RefreshCells();
            session.CenterOn(site);
            for (int tilt = 0; tilt < 3; tilt++)
            {
                renderer.Sync(session, host.Map.Size);
                await Frames(host);
                var point = renderer.ScreenPoint(new(site.X + .5f, 0, site.Y + .5f));
                Require(host.Map.TryMapPosition(point, out var picked) && picked == site,
                    $"Picking must survive supersampling: expected={site}, actual={picked}, " +
                    $"zoom={level}, tilt={renderer.Elevation}, pointer={point}.");
                Require(host.Map.PerspectiveRangeBounds.Count > 0 &&
                    host.Map.PerspectiveRangeBounds.All(bounds => Math.Abs(bounds.Size.X - bounds.Size.Y) < .001f),
                    "Service guides must remain circles in display space, not scale with the render texture.");
                host.Map.TiltMap();
            }
        }
        session.CancelPreview();
        session.ClosePrompt();
        session.SetAreaOverlays(areas);
        session.SetZoom(zoom);
        host.Map.RefreshCells();
        session.CenterOn(center);
        await Frames(host);

        const int size = 256;
        var scene = new SubViewport
        {
            OwnWorld3D = true, Size = new(size, size), Msaa3D = Viewport.Msaa.Msaa2X,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };
        var output = new SubViewport
        {
            OwnWorld3D = true, Size = new(size, size),
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };
        using var environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = Colors.Black,
        };
        using var material = new StandardMaterial3D
        {
            AlbedoColor = Colors.White, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            CullMode = BaseMaterial3D.CullModeEnum.Back,
        };
        using var cube = new BoxMesh { Size = new(2.4f, 1.8f, 1.7f) };
        host.AddChild(scene);
        host.AddChild(output);
        scene.AddChild(new WorldEnvironment { Environment = environment });
        scene.AddChild(new MeshInstance3D
        {
            Mesh = cube, MaterialOverride = material, Rotation = new(0, .37f, 0),
        });
        var camera = new Camera3D
        {
            Projection = Camera3D.ProjectionType.Orthogonal, Size = 4.5f, Position = new(4, 3, 5),
        };
        scene.AddChild(camera);
        camera.LookAt(Vector3.Zero, Vector3.Up);
        camera.Current = true;
        using var sourceTexture = scene.GetTexture();
        using var outputTexture = output.GetTexture();
        output.AddChild(new TextureRect
        {
            Texture = sourceTexture, Size = new(size, size),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            TextureFilter = CanvasItem.TextureFilterEnum.Linear,
        });
        var measures = new List<(int Tones, int Pixels)>();
        try
        {
            for (int quality = 0; quality < 2; quality++)
            {
                if (quality == 1)
                {
                    scene.Size = new(size * PerspectiveCity.SupersampleScale, size * PerspectiveCity.SupersampleScale);
                    scene.Msaa3D = renderer.EdgeAntialiasing;
                }
                await Frames(host);
                await Frames(host);
                using var image = outputTexture.GetImage();
                Require(!image.IsEmpty() && image.GetSize() == new Vector2I(size, size),
                    "Edge-quality measurements must use the final downsampled display image.");
                image.Convert(Image.Format.Rgba8);
                var pixels = image.GetData();
                var tones = new HashSet<byte>();
                int mixed = 0;
                for (int index = 0; index < pixels.Length; index += 4)
                    if (pixels[index] is > 0 and < 255 &&
                        Math.Abs(pixels[index] - pixels[index + 1]) <= 1 &&
                        Math.Abs(pixels[index] - pixels[index + 2]) <= 1)
                    {
                        tones.Add(pixels[index]);
                        mixed++;
                    }
                measures.Add((tones.Count, mixed));
                if (capturePath is not null)
                    Require(image.SavePng(Path.ChangeExtension(capturePath,
                        quality == 0 ? ".edge-before.png" : ".edge-after.png")) == Error.Ok,
                        "The matched edge-quality captures must save successfully.");
            }
        }
        finally
        {
            host.RemoveChild(output);
            host.RemoveChild(scene);
            output.Free();
            scene.Free();
        }
        Require(measures[0].Pixels > 50 && measures[1].Pixels >= measures[0].Pixels &&
            measures[1].Tones >= measures[0].Tones + 4,
            $"High quality must improve actual diagonal edge coverage: baseline={measures[0]}, smooth={measures[1]}.");
        GD.Print($"TERMCITY_EDGE_QUALITY_OK baseline-tones={measures[0].Tones} smooth-tones={measures[1].Tones} " +
            $"baseline-edge-pixels={measures[0].Pixels} smooth-edge-pixels={measures[1].Pixels}");
        GD.Print("TERMCITY_QUALITY_COORDINATES_OK canvas-scale=fractional tilts=3 zooms=3 picking=exact guides=circles");
    }

    private static void Require(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}

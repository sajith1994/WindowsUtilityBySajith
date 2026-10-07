using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using WpfColor = System.Windows.Media.Color;
using WpfPoint = System.Windows.Point;

namespace CentricDeviceMonitor.Services;

/// <summary>
/// Creates a dependency-free WPF 3D render workload. WPF submits the scene to
/// Direct3D when hardware rendering is available. This is intentionally a simple
/// cooler/load check rather than a vendor-specific GPU benchmark.
/// </summary>
public sealed class GpuRenderLoadController : IDisposable
{
    private readonly Viewport3D _viewport;
    private readonly List<AxisAngleRotation3D> _rotations = [];
    private ModelVisual3D? _sceneVisual;
    private Stopwatch? _renderClock;
    private TimeSpan _lastFrameAt;

    public GpuRenderLoadController(Viewport3D viewport)
    {
        _viewport = viewport ?? throw new ArgumentNullException(nameof(viewport));
    }

    public bool IsRunning { get; private set; }

    public static int RenderingTier => RenderCapability.Tier >> 16;

    public static bool IsHardwareRenderingAvailable => RenderingTier > 0;

    public void Start(int loadPercent)
    {
        if (loadPercent is < 25 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(loadPercent),
                "GPU load must be between 25 and 100 percent.");
        }

        if (!IsHardwareRenderingAvailable)
        {
            throw new InvalidOperationException(
                "WPF hardware rendering is unavailable. The GPU load test will not run in software-rendering mode.");
        }

        Stop();
        BuildScene(loadPercent);
        _renderClock = Stopwatch.StartNew();
        _lastFrameAt = TimeSpan.Zero;
        CompositionTarget.Rendering += CompositionTarget_Rendering;
        IsRunning = true;
    }

    public void Stop()
    {
        CompositionTarget.Rendering -= CompositionTarget_Rendering;
        IsRunning = false;
        _renderClock?.Stop();
        _renderClock = null;
        _rotations.Clear();

        if (_sceneVisual is not null)
        {
            _viewport.Children.Remove(_sceneVisual);
            _sceneVisual = null;
        }
    }

    private void BuildScene(int loadPercent)
    {
        MeshGeometry3D mesh = CreateSphereMesh(longitudeSegments: 48, latitudeSegments: 28);
        Material material = CreateMaterial();
        Model3DGroup scene = new();

        scene.Children.Add(new AmbientLight(WpfColor.FromRgb(62, 68, 86)));
        scene.Children.Add(new DirectionalLight(Colors.White, new Vector3D(-0.4, -0.6, -1.0)));
        scene.Children.Add(new DirectionalLight(WpfColor.FromRgb(84, 170, 255), new Vector3D(0.7, 0.3, -0.5)));

        int modelCount = 32 + (int)Math.Round(loadPercent * 1.28);
        int columns = 12;
        int rows = (int)Math.Ceiling(modelCount / (double)columns);
        Random random = new(20260830);

        for (int index = 0; index < modelCount; index++)
        {
            int column = index % columns;
            int row = index / columns;
            double x = (column - ((columns - 1) / 2.0)) * 1.12;
            double y = (((rows - 1) / 2.0) - row) * 1.12;
            double z = -2.3 + (random.NextDouble() * 3.8);
            double scale = 0.40 + (random.NextDouble() * 0.16);

            AxisAngleRotation3D rotation = new(
                new Vector3D(
                    0.25 + random.NextDouble(),
                    0.25 + random.NextDouble(),
                    0.25 + random.NextDouble()),
                random.NextDouble() * 360.0);
            _rotations.Add(rotation);

            Transform3DGroup transform = new();
            transform.Children.Add(new ScaleTransform3D(scale, scale, scale));
            transform.Children.Add(new RotateTransform3D(rotation));
            transform.Children.Add(new TranslateTransform3D(x, y, z));

            GeometryModel3D model = new(mesh, material)
            {
                BackMaterial = material,
                Transform = transform
            };
            scene.Children.Add(model);
        }

        _viewport.Camera = new PerspectiveCamera(
            new Point3D(0, 0, 15.5),
            new Vector3D(0, 0, -1),
            new Vector3D(0, 1, 0),
            58);

        _sceneVisual = new ModelVisual3D { Content = scene };
        _viewport.Children.Add(_sceneVisual);
    }

    private void CompositionTarget_Rendering(object? sender, EventArgs e)
    {
        if (!IsRunning || _renderClock is null)
        {
            return;
        }

        TimeSpan now = _renderClock.Elapsed;
        double deltaSeconds = Math.Clamp((now - _lastFrameAt).TotalSeconds, 0.0, 0.05);
        _lastFrameAt = now;

        for (int index = 0; index < _rotations.Count; index++)
        {
            double direction = (index & 1) == 0 ? 1.0 : -1.0;
            _rotations[index].Angle = (_rotations[index].Angle +
                (direction * deltaSeconds * (70.0 + (index % 11) * 8.0))) % 360.0;
        }
    }

    private static MeshGeometry3D CreateSphereMesh(int longitudeSegments, int latitudeSegments)
    {
        MeshGeometry3D mesh = new();

        for (int latitude = 0; latitude <= latitudeSegments; latitude++)
        {
            double v = latitude / (double)latitudeSegments;
            double phi = Math.PI * v;
            double sinPhi = Math.Sin(phi);
            double cosPhi = Math.Cos(phi);

            for (int longitude = 0; longitude <= longitudeSegments; longitude++)
            {
                double u = longitude / (double)longitudeSegments;
                double theta = Math.PI * 2.0 * u;
                Vector3D normal = new(
                    sinPhi * Math.Cos(theta),
                    cosPhi,
                    sinPhi * Math.Sin(theta));
                mesh.Positions.Add(new Point3D(normal.X, normal.Y, normal.Z));
                mesh.Normals.Add(normal);
                mesh.TextureCoordinates.Add(new WpfPoint(u, v));
            }
        }

        int stride = longitudeSegments + 1;
        for (int latitude = 0; latitude < latitudeSegments; latitude++)
        {
            for (int longitude = 0; longitude < longitudeSegments; longitude++)
            {
                int first = (latitude * stride) + longitude;
                int second = first + stride;
                mesh.TriangleIndices.Add(first);
                mesh.TriangleIndices.Add(second);
                mesh.TriangleIndices.Add(first + 1);
                mesh.TriangleIndices.Add(first + 1);
                mesh.TriangleIndices.Add(second);
                mesh.TriangleIndices.Add(second + 1);
            }
        }

        mesh.Freeze();
        return mesh;
    }

    private static Material CreateMaterial()
    {
        MaterialGroup group = new();
        SolidColorBrush diffuseBrush = new(WpfColor.FromRgb(0, 122, 255));
        SolidColorBrush specularBrush = new(WpfColor.FromRgb(220, 240, 255));
        diffuseBrush.Freeze();
        specularBrush.Freeze();
        group.Children.Add(new DiffuseMaterial(diffuseBrush));
        group.Children.Add(new SpecularMaterial(specularBrush, 80));
        group.Freeze();
        return group;
    }

    public void Dispose() => Stop();
}

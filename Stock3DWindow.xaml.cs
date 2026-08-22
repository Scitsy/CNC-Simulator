using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace FanucSimulator
{
    // First-pass 3D preview: the workpiece (revolved from the same StockProfile the 2D canvas
    // already carves) plus a simple chuck stand-in at the held end - "chuck only, not whole
    // machine" per the explicit scope this was built to. Modeless, owned by MainWindow, reused
    // across opens exactly like ToolBuilderWindow.
    //
    // Axis mapping: lathe Z (part length) -> 3D X (horizontal); lathe radius -> the 3D Y/Z plane,
    // revolved around X. Y-up camera convention (standard for a 3D viewer), orbited via mouse drag
    // around the part's own center point.
    //
    // Refined once from the very first pass: BuildChuck now builds a body + 3 jaw blocks (still a
    // simplified stand-in, not a manufacturer-accurate chuck), and Refresh() adds a small marker at
    // the tool's current position. Still not modeled: the toolpath itself in 3D, a cutaway/cross-
    // section view, and mitering a through-bore specially against the end cap.
    public partial class Stock3DWindow : Window
    {
        // Not readonly - MainWindow's Reset replaces its whole _sim instance (a fresh LatheSimulator,
        // fresh Stock) rather than mutating the existing one, so this window needs a way to follow
        // along to the new instance rather than keep rendering the discarded one.
        private LatheSimulator _sim;

        private const int CircumferentialSegments = 32;

        private Point3D _cameraTarget;
        private double _cameraDistance = 200;
        private double _azimuth = 0.7;   // radians, initial ~3/4 view
        private double _elevation = 0.35;
        private bool _hasFramedOnce;
        private double _lastFramedMaxRadius;
        private double _lastFramedPartLength;

        private bool _isDragging;
        private Point _lastMousePos;

        public Stock3DWindow(LatheSimulator sim)
        {
            InitializeComponent();
            _sim = sim;
            Loaded += (_, _) => Refresh();
        }

        public void UpdateSimulator(LatheSimulator sim) => _sim = sim;

        public void Refresh()
        {
            var stock = _sim.Stock;

            // Re-fit distance/target (but never touch the user's chosen azimuth/elevation) whenever
            // the stock's size has changed enough to matter - not just on first open. Otherwise
            // switching to a much bigger or smaller stock than whatever was last framed leaves the
            // camera badly mismatched (too close/inside the part, or too far to see it) until the
            // operator thinks to hit "Reset Orbit" themselves.
            var maxRadius = 0.0;
            for (int i = 0; i <= StockProfile.Resolution; i++)
                maxRadius = Math.Max(maxRadius, stock.OuterX[i] / 2.0);
            var partLength = stock.ZEnd - stock.ZStart;

            var sizeChangedSubstantially = _hasFramedOnce &&
                (Math.Abs(maxRadius - _lastFramedMaxRadius) > _lastFramedMaxRadius * 0.25 ||
                 Math.Abs(partLength - _lastFramedPartLength) > _lastFramedPartLength * 0.25);

            if (!_hasFramedOnce || sizeChangedSubstantially)
            {
                FrameCamera(stock);
                _lastFramedMaxRadius = maxRadius;
                _lastFramedPartLength = partLength;
                _hasFramedOnce = true;
            }
            UpdateCameraPosition();

            var group = new Model3DGroup();
            group.Children.Add(new AmbientLight(Color.FromRgb(70, 70, 75)));
            group.Children.Add(new DirectionalLight(Color.FromRgb(220, 220, 220), new Vector3D(-1, -0.6, -0.4)));
            group.Children.Add(new DirectionalLight(Color.FromRgb(60, 60, 70), new Vector3D(1, 0.3, 0.6)));

            var stockMaterial = new MaterialGroup();
            stockMaterial.Children.Add(new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(150, 155, 160))));
            stockMaterial.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromRgb(200, 200, 210)), 60));

            var outerMesh = BuildRevolvedMesh(stock, i => stock.OuterX[i], inward: false);
            group.Children.Add(new GeometryModel3D(outerMesh, stockMaterial) { BackMaterial = stockMaterial });

            var hasBore = false;
            for (int i = 0; i <= StockProfile.Resolution; i++)
                if (stock.InnerX[i] > 1e-6) { hasBore = true; break; }

            if (hasBore)
            {
                var innerMesh = BuildRevolvedMesh(stock, i => stock.InnerX[i], inward: true);
                group.Children.Add(new GeometryModel3D(innerMesh, stockMaterial) { BackMaterial = stockMaterial });
            }

            // Face-end cap (Z=0, the stock's ZEnd) - only if that end isn't bored fully through.
            var faceInnerDiameter = stock.InnerX[StockProfile.Resolution];
            if (faceInnerDiameter < stock.OuterX[StockProfile.Resolution] - 1e-6)
            {
                var cap = BuildEndCap(stock.ZEnd, faceInnerDiameter, stock.OuterX[StockProfile.Resolution]);
                group.Children.Add(new GeometryModel3D(cap, stockMaterial) { BackMaterial = stockMaterial });
            }

            group.Children.Add(BuildChuck(stock.ZStart, stock.OuterX[0]));
            group.Children.Add(BuildToolMarker(_sim.X, _sim.Z));

            MainViewport.Children.Clear();
            MainViewport.Children.Add(new ModelVisual3D { Content = group });
        }

        private void FrameCamera(StockProfile stock)
        {
            var maxRadius = 0.0;
            for (int i = 0; i <= StockProfile.Resolution; i++)
                maxRadius = Math.Max(maxRadius, stock.OuterX[i] / 2.0);

            var partLength = stock.ZEnd - stock.ZStart;
            _cameraTarget = new Point3D((stock.ZStart + stock.ZEnd) / 2.0, 0, 0);
            _cameraDistance = Math.Max(partLength, maxRadius * 3) * 1.6;
        }

        private void UpdateCameraPosition()
        {
            var offsetX = _cameraDistance * Math.Cos(_elevation) * Math.Cos(_azimuth);
            var offsetY = _cameraDistance * Math.Sin(_elevation);
            var offsetZ = _cameraDistance * Math.Cos(_elevation) * Math.Sin(_azimuth);
            var position = new Point3D(_cameraTarget.X + offsetX, _cameraTarget.Y + offsetY, _cameraTarget.Z + offsetZ);

            MainCamera.Position = position;
            MainCamera.LookDirection = _cameraTarget - position;
            MainCamera.UpDirection = new Vector3D(0, 1, 0);
        }

        // Revolves one boundary (outer or inner diameter, as a function of ring index) 360 degrees
        // around the X axis. `inward` reverses triangle winding so the surface's computed normals
        // face toward the axis (correct for a bore's inside wall) instead of away from it.
        private static MeshGeometry3D BuildRevolvedMesh(StockProfile stock, Func<int, double> diameterAt, bool inward)
        {
            var mesh = new MeshGeometry3D();
            int rings = StockProfile.Resolution + 1;

            for (int i = 0; i < rings; i++)
            {
                var x = stock.SampleZ(i);
                var radius = diameterAt(i) / 2.0;
                for (int j = 0; j < CircumferentialSegments; j++)
                {
                    var angle = j * 2 * Math.PI / CircumferentialSegments;
                    mesh.Positions.Add(new Point3D(x, radius * Math.Cos(angle), radius * Math.Sin(angle)));
                }
            }

            for (int i = 0; i < rings - 1; i++)
            {
                for (int j = 0; j < CircumferentialSegments; j++)
                {
                    var jNext = (j + 1) % CircumferentialSegments;
                    var a = i * CircumferentialSegments + j;
                    var b = i * CircumferentialSegments + jNext;
                    var c = (i + 1) * CircumferentialSegments + j;
                    var d = (i + 1) * CircumferentialSegments + jNext;

                    if (!inward)
                    {
                        mesh.TriangleIndices.Add(a); mesh.TriangleIndices.Add(c); mesh.TriangleIndices.Add(b);
                        mesh.TriangleIndices.Add(b); mesh.TriangleIndices.Add(c); mesh.TriangleIndices.Add(d);
                    }
                    else
                    {
                        mesh.TriangleIndices.Add(a); mesh.TriangleIndices.Add(b); mesh.TriangleIndices.Add(c);
                        mesh.TriangleIndices.Add(b); mesh.TriangleIndices.Add(d); mesh.TriangleIndices.Add(c);
                    }
                }
            }

            return mesh;
        }

        // A flat annulus (or a solid disk, if innerDiameter is ~0) at a fixed X - the stock's face.
        private static MeshGeometry3D BuildEndCap(double x, double innerDiameter, double outerDiameter)
        {
            var mesh = new MeshGeometry3D();
            var outerR = outerDiameter / 2.0;
            var innerR = innerDiameter / 2.0;

            if (innerR < 1e-6)
            {
                mesh.Positions.Add(new Point3D(x, 0, 0));
                for (int j = 0; j <= CircumferentialSegments; j++)
                {
                    var angle = j * 2 * Math.PI / CircumferentialSegments;
                    mesh.Positions.Add(new Point3D(x, outerR * Math.Cos(angle), outerR * Math.Sin(angle)));
                }
                for (int j = 1; j <= CircumferentialSegments; j++)
                {
                    mesh.TriangleIndices.Add(0);
                    mesh.TriangleIndices.Add(j);
                    mesh.TriangleIndices.Add(j == CircumferentialSegments ? 1 : j + 1);
                }
            }
            else
            {
                for (int j = 0; j <= CircumferentialSegments; j++)
                {
                    var angle = j * 2 * Math.PI / CircumferentialSegments;
                    mesh.Positions.Add(new Point3D(x, innerR * Math.Cos(angle), innerR * Math.Sin(angle)));
                }
                var outerBase = mesh.Positions.Count;
                for (int j = 0; j <= CircumferentialSegments; j++)
                {
                    var angle = j * 2 * Math.PI / CircumferentialSegments;
                    mesh.Positions.Add(new Point3D(x, outerR * Math.Cos(angle), outerR * Math.Sin(angle)));
                }
                for (int j = 0; j < CircumferentialSegments; j++)
                {
                    var i0 = j; var i1 = j + 1;
                    var o0 = outerBase + j; var o1 = outerBase + j + 1;
                    mesh.TriangleIndices.Add(i0); mesh.TriangleIndices.Add(o0); mesh.TriangleIndices.Add(i1);
                    mesh.TriangleIndices.Add(i1); mesh.TriangleIndices.Add(o0); mesh.TriangleIndices.Add(o1);
                }
            }

            return mesh;
        }

        // Chuck stand-in at the stock's held (-Z / -X) end: a cylindrical body plus 3 jaw blocks
        // gripping in toward the stock's actual OD at that end - still a simplified stand-in (flat-
        // sided wedges, not real serrated jaw geometry or a real chuck's mechanism), but reads as
        // "a 3-jaw chuck" rather than a plain drum. "Chuck only, not whole machine" per this
        // feature's own explicit scope - no headstock/bed/tailstock modeled.
        private static Model3DGroup BuildChuck(double stockZStart, double stockDiameterAtHeldEnd)
        {
            var group = new Model3DGroup();
            var stockRadius = stockDiameterAtHeldEnd / 2.0;
            var bodyRadius = stockRadius * 1.2;
            var length = Math.Max(15, stockDiameterAtHeldEnd * 0.3);
            var xFront = stockZStart;
            var xBack = stockZStart - length;

            var bodyMaterial = new MaterialGroup();
            bodyMaterial.Children.Add(new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(90, 90, 95))));
            bodyMaterial.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromRgb(140, 140, 145)), 30));

            var mesh = new MeshGeometry3D();
            for (int j = 0; j < CircumferentialSegments; j++)
            {
                var angle = j * 2 * Math.PI / CircumferentialSegments;
                mesh.Positions.Add(new Point3D(xBack, bodyRadius * Math.Cos(angle), bodyRadius * Math.Sin(angle)));
            }
            for (int j = 0; j < CircumferentialSegments; j++)
            {
                var angle = j * 2 * Math.PI / CircumferentialSegments;
                mesh.Positions.Add(new Point3D(xFront, bodyRadius * Math.Cos(angle), bodyRadius * Math.Sin(angle)));
            }
            for (int j = 0; j < CircumferentialSegments; j++)
            {
                var jNext = (j + 1) % CircumferentialSegments;
                var a = j; var b = jNext;
                var c = CircumferentialSegments + j; var d = CircumferentialSegments + jNext;
                mesh.TriangleIndices.Add(a); mesh.TriangleIndices.Add(c); mesh.TriangleIndices.Add(b);
                mesh.TriangleIndices.Add(b); mesh.TriangleIndices.Add(c); mesh.TriangleIndices.Add(d);
            }
            // Back end cap (a solid disk) - front end butts against the stock, left open.
            var backCap = BuildEndCap(xBack, 0, bodyRadius * 2);
            foreach (var p in backCap.Positions) mesh.Positions.Add(p);
            var offset = mesh.Positions.Count - backCap.Positions.Count;
            foreach (var idx in backCap.TriangleIndices) mesh.TriangleIndices.Add(idx + offset);

            group.Children.Add(new GeometryModel3D(mesh, bodyMaterial) { BackMaterial = bodyMaterial });

            var jawMaterial = new MaterialGroup();
            jawMaterial.Children.Add(new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(70, 72, 78))));
            jawMaterial.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromRgb(120, 122, 128)), 40));

            // 3 jaws, 120 degrees apart, each gripping in toward ~90% of the stock's radius (reading
            // as "biting into" the part slightly) out to the chuck body's own radius.
            var jawGripRadius = Math.Max(1, stockRadius * 0.9);
            for (int k = 0; k < 3; k++)
            {
                var jawMesh = BuildJawWedge(k * 2 * Math.PI / 3, Math.PI / 6, jawGripRadius, bodyRadius, xFront, xBack);
                group.Children.Add(new GeometryModel3D(jawMesh, jawMaterial) { BackMaterial = jawMaterial });
            }

            return group;
        }

        // One jaw as a simple 8-vertex wedge (flat radial/angular faces, not curved or serrated) -
        // spans [angleCenter - halfAngle, angleCenter + halfAngle], innerR to outerR, xBack to xFront.
        private static MeshGeometry3D BuildJawWedge(double angleCenter, double halfAngle, double innerR, double outerR, double xFront, double xBack)
        {
            var a0 = angleCenter - halfAngle;
            var a1 = angleCenter + halfAngle;
            Point3D P(double x, double r, double angle) => new(x, r * Math.Cos(angle), r * Math.Sin(angle));

            var backInnerA = P(xBack, innerR, a0);
            var backInnerB = P(xBack, innerR, a1);
            var backOuterA = P(xBack, outerR, a0);
            var backOuterB = P(xBack, outerR, a1);
            var frontInnerA = P(xFront, innerR, a0);
            var frontInnerB = P(xFront, innerR, a1);
            var frontOuterA = P(xFront, outerR, a0);
            var frontOuterB = P(xFront, outerR, a1);

            var mesh = new MeshGeometry3D();
            AddQuad(mesh, backInnerA, backInnerB, frontInnerB, frontInnerA); // inner (grips the stock)
            AddQuad(mesh, backOuterB, backOuterA, frontOuterA, frontOuterB); // outer
            AddQuad(mesh, frontInnerA, frontInnerB, frontOuterB, frontOuterA); // front
            AddQuad(mesh, backInnerB, backInnerA, backOuterA, backOuterB); // back
            AddQuad(mesh, backInnerA, frontInnerA, frontOuterA, backOuterA); // side at a0
            AddQuad(mesh, frontInnerB, backInnerB, backOuterB, frontOuterB); // side at a1
            return mesh;
        }

        // Appends a quad (4 corners, in loop order around its perimeter) as two triangles.
        private static void AddQuad(MeshGeometry3D mesh, Point3D a, Point3D b, Point3D c, Point3D d)
        {
            var baseIndex = mesh.Positions.Count;
            mesh.Positions.Add(a); mesh.Positions.Add(b); mesh.Positions.Add(c); mesh.Positions.Add(d);
            mesh.TriangleIndices.Add(baseIndex); mesh.TriangleIndices.Add(baseIndex + 1); mesh.TriangleIndices.Add(baseIndex + 2);
            mesh.TriangleIndices.Add(baseIndex); mesh.TriangleIndices.Add(baseIndex + 2); mesh.TriangleIndices.Add(baseIndex + 3);
        }

        // A small bright marker at the tool's current position - X is diameter-programmed (the
        // established convention throughout this project), so radius = X/2, matching how the stock
        // mesh itself is built from OuterX/2. Lime green to match the 2D canvas's own tool marker.
        private static GeometryModel3D BuildToolMarker(double toolX, double toolZ)
        {
            var radius = toolX / 2.0;
            const double half = 2.5; // mm - small relative to typical part sizes in the demo programs
            var center = new Point3D(toolZ, radius, 0);

            var corners = new Point3D[8];
            var signs = new (double, double, double)[]
            {
                (-1, -1, -1), (1, -1, -1), (1, 1, -1), (-1, 1, -1),
                (-1, -1, 1), (1, -1, 1), (1, 1, 1), (-1, 1, 1),
            };
            for (int i = 0; i < 8; i++)
            {
                var (sx, sy, sz) = signs[i];
                corners[i] = new Point3D(center.X + sx * half, center.Y + sy * half, center.Z + sz * half);
            }

            var mesh = new MeshGeometry3D();
            AddQuad(mesh, corners[0], corners[1], corners[2], corners[3]); // -Z face
            AddQuad(mesh, corners[5], corners[4], corners[7], corners[6]); // +Z face
            AddQuad(mesh, corners[4], corners[0], corners[3], corners[7]); // -X face
            AddQuad(mesh, corners[1], corners[5], corners[6], corners[2]); // +X face
            AddQuad(mesh, corners[3], corners[2], corners[6], corners[7]); // +Y face
            AddQuad(mesh, corners[4], corners[5], corners[1], corners[0]); // -Y face

            var material = new MaterialGroup();
            material.Children.Add(new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(60, 200, 60))));
            material.Children.Add(new EmissiveMaterial(new SolidColorBrush(Color.FromRgb(30, 120, 30))));
            return new GeometryModel3D(mesh, material) { BackMaterial = material };
        }

        private void Viewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isDragging = true;
            _lastMousePos = e.GetPosition(MainViewport);
            MainViewport.CaptureMouse();
        }

        private void Viewport_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDragging) return;
            var pos = e.GetPosition(MainViewport);
            var dx = pos.X - _lastMousePos.X;
            var dy = pos.Y - _lastMousePos.Y;
            _lastMousePos = pos;

            _azimuth -= dx * 0.008;
            _elevation = Math.Clamp(_elevation + dy * 0.008, -1.4, 1.4);
            UpdateCameraPosition();
        }

        private void Viewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isDragging = false;
            MainViewport.ReleaseMouseCapture();
        }

        private void Viewport_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            _cameraDistance *= e.Delta > 0 ? 0.9 : 1.1;
            _cameraDistance = Math.Clamp(_cameraDistance, 20, 5000);
            UpdateCameraPosition();
        }

        private void ResetOrbit_Click(object sender, RoutedEventArgs e)
        {
            _azimuth = 0.7;
            _elevation = 0.35;
            _hasFramedOnce = false;
            Refresh();
        }

        private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}

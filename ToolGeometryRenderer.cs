using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace FanucSimulator
{
    // The parameters that actually affect a tool's drawn silhouette - a snapshot separate from
    // ToolOffset so the live Tool Builder form (which may hold transient/unparsed text) and a
    // future OFFSET-screen mini-preview can both feed the same renderer.
    public readonly record struct ToolGeometryParams(
        ToolType Type, InsertShape Insert, double NoseRadius, double Width, double ShankSize, double Overhang,
        GrooveReference Reference = GrooveReference.Center, double InsertReach = 6.0)
    {
        public static ToolGeometryParams FromOffset(ToolOffset o) =>
            new(o.Type, o.Insert, o.NoseRadius, o.Width, o.ShankSize, o.Overhang, o.Reference, o.InsertReach);
    }

    // Draws a schematic (not to-scale - the data model has no real insert-IC field) 2D tool
    // silhouette onto a Canvas: insert + holder for turning/boring, a blade + holder for
    // grooving/threading, a pointed cylinder for drills. Follows MainWindow.RenderLathe()'s own
    // pattern - full clear-and-rebuild every call, auto-fit scale from content bounds, shapes
    // built as Polygon/Line/Rectangle since this codebase never uses Path/ArcSegment; curves are
    // hand-sampled into point arrays instead (same idiom RenderLathe uses for the stock profile).
    //
    // Deliberately does not model NoseDirection (the imaginary tool-nose vector 0-9) - that field
    // is already dead everywhere else in the simulator, so every tool here draws as if front-
    // mounted, matching LatheSimulator.MoveTo's own cutter-comp comment ("assumes a front tool post").
    public static class ToolGeometryRenderer
    {
        private static readonly SolidColorBrush InsertBrush = new(Color.FromRgb(0xc8, 0xa0, 0x30));
        private static readonly SolidColorBrush HolderBrush = new(Color.FromRgb(0x55, 0x58, 0x5a));

        public static void Render(Canvas canvas, ToolGeometryParams p)
        {
            canvas.Children.Clear();

            switch (p.Type)
            {
                case ToolType.OdTurning:
                case ToolType.IdBoring:
                    DrawTurningOrBoringTool(canvas, p);
                    break;
                case ToolType.Grooving:
                case ToolType.IdGrooving:
                case ToolType.Threading:
                    DrawGroovingOrThreadingTool(canvas, p);
                    break;
                case ToolType.Drill:
                    DrawDrillTool(canvas, p);
                    break;
                default:
                    DrawUndefinedPlaceholder(canvas);
                    break;
            }
        }

        // Model space (local to this file, not the lathe's own X/Z): tip/cutting point at (0,0),
        // pointing toward -Mx (the workpiece); the holder/shank extends toward +Mx; every shape is
        // mirror-symmetric about My=0 (the tool centerline). Screen mapping mirrors RenderLathe's
        // own PxX/PxY closures, just with the vertical axis centered instead of baseline-anchored.
        private static (double scale, Func<double, double> PxX, Func<double, double> PxY) Fit(
            Canvas canvas, double mxMax, double myHalfExtent)
        {
            const double margin = 28;
            var canvasWidth = canvas.ActualWidth > 0 ? canvas.ActualWidth : 340;
            var canvasHeight = canvas.ActualHeight > 0 ? canvas.ActualHeight : 300;

            var mxExtent = Math.Max(1, mxMax);
            var myExtent = Math.Max(1, myHalfExtent * 2);

            var availableWidth = Math.Max(20, canvasWidth - margin * 2);
            var availableHeight = Math.Max(20, canvasHeight - margin * 2);
            var scale = Math.Min(availableWidth / mxExtent, availableHeight / myExtent);

            var originX = margin;
            var originY = canvasHeight / 2.0;

            double PxX(double mx) => originX + mx * scale;
            double PxY(double my) => originY - my * scale;

            return (scale, PxX, PxY);
        }

        private static void DrawCornerLabel(Canvas canvas, string text)
        {
            var label = new TextBlock { Text = text, Foreground = Brushes.DarkGray, FontSize = 10 };
            Canvas.SetLeft(label, 4);
            Canvas.SetTop(label, 4);
            canvas.Children.Add(label);
        }

        // Small filled dot marking the tool's actual programmed X/Z point - only really needed when
        // that point isn't the visual center of what's drawn (e.g. a Left/Right-referenced groove
        // blade), but drawn for every type so its meaning stays consistent across previews.
        private static void DrawReferenceMarker(Canvas canvas, Func<double, double> PxX, Func<double, double> PxY)
        {
            var dot = new Ellipse { Width = 5, Height = 5, Fill = Brushes.Cyan };
            Canvas.SetLeft(dot, PxX(0) - 2.5);
            Canvas.SetTop(dot, PxY(0) - 2.5);
            canvas.Children.Add(dot);
        }

        // The lever-lock / cam-pin clamp - the most common way a turning/boring insert is actually
        // held down (a central pin engages the insert's own center hole and pulls it back and down
        // into the pocket's seating surfaces). Drawn as a dark screw/pin head over the insert's own
        // center, on top of the insert fill so it reads as sitting IN the insert, not floating.
        private static void DrawCamLockPin(Canvas canvas, Func<double, double> PxX, Func<double, double> PxY, double centerMx, double radius)
        {
            var r = Math.Max(2.0, radius);
            var pin = new Ellipse
            {
                Width = r * 2,
                Height = r * 2,
                Fill = new SolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x2a)),
                Stroke = Brushes.Black,
                StrokeThickness = 0.75
            };
            Canvas.SetLeft(pin, PxX(centerMx) - r);
            Canvas.SetTop(pin, PxY(0) - r);
            canvas.Children.Add(pin);
        }

        private static void DrawCenterline(Canvas canvas, Func<double, double> PxX, Func<double, double> PxY, double mxMax)
        {
            var line = new Line
            {
                X1 = PxX(-4),
                Y1 = PxY(0),
                X2 = PxX(mxMax + 6),
                Y2 = PxY(0),
                Stroke = Brushes.DimGray,
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 3, 3 }
            };
            canvas.Children.Add(line);
        }

        // ---- OD turning / ID boring: insert + holder ----

        private static void DrawTurningOrBoringTool(Canvas canvas, ToolGeometryParams p)
        {
            var shankSize = Math.Max(p.ShankSize, 1.0);
            var overhang = Math.Max(p.Overhang, 5.0);
            var noseRadius = Math.Max(p.NoseRadius, 0.0);
            var insertEdge = Math.Max(shankSize * 0.45, 8.0);

            List<Point> outline;
            double insertHalfHeight;
            double insertRearX;
            double insertCenterX;

            if (p.Insert == InsertShape.Round)
            {
                // A round insert's radius effectively *is* its nose radius - tie the circle
                // directly to NoseRadius so adjusting it visibly changes the preview.
                var r = Math.Max(noseRadius, 1.0);
                outline = new List<Point>();
                for (int i = 0; i <= 32; i++)
                {
                    var a = 2 * Math.PI * i / 32;
                    outline.Add(new Point(r + r * Math.Cos(a), r * Math.Sin(a)));
                }
                insertHalfHeight = r;
                insertRearX = 2 * r;
                insertCenterX = r;
            }
            else if (p.Insert == InsertShape.Triangle60)
            {
                var half = 30 * Math.PI / 180;
                var tip = new Point(0, 0);
                var upper = new Point(insertEdge * Math.Cos(half), insertEdge * Math.Sin(half));
                var lower = new Point(insertEdge * Math.Cos(half), -insertEdge * Math.Sin(half));

                var fillet = SampleFilletedCorner(tip, upper - tip, lower - tip, noseRadius);
                outline = new List<Point> { upper };
                outline.AddRange(fillet);
                outline.Add(lower);

                insertHalfHeight = insertEdge * Math.Sin(half);
                insertRearX = insertEdge * Math.Cos(half);
                insertCenterX = 2 * insertRearX / 3.0; // triangle centroid (tip at 0, base at insertRearX)
            }
            else
            {
                // Diamond80/55/35 and Square (treated as a 90-degree diamond) all share the same
                // rhombus construction - just a different included angle at the tip.
                var halfAngleDeg = p.Insert switch
                {
                    InsertShape.Diamond80 => 40.0,
                    InsertShape.Diamond55 => 27.5,
                    InsertShape.Diamond35 => 17.5,
                    InsertShape.Square => 45.0,
                    _ => 40.0
                };
                var half = halfAngleDeg * Math.PI / 180;
                var tip = new Point(0, 0);
                var upper = new Point(insertEdge * Math.Cos(half), insertEdge * Math.Sin(half));
                var lower = new Point(insertEdge * Math.Cos(half), -insertEdge * Math.Sin(half));
                var farTip = new Point(2 * insertEdge * Math.Cos(half), 0);

                var fillet = SampleFilletedCorner(tip, upper - tip, lower - tip, noseRadius);
                outline = new List<Point> { upper };
                outline.AddRange(fillet);
                outline.Add(lower);
                outline.Add(farTip);

                insertHalfHeight = insertEdge * Math.Sin(half);
                insertRearX = 2 * insertEdge * Math.Cos(half);
                insertCenterX = insertRearX / 2.0; // rhombus center, and where its real clamp hole sits
            }

            // A real insert is centered in a pocket machined into the holder and held down through
            // its own center by a lever-lock cam pin - not offset to one side, and not just abutting
            // the holder's front face. Seat it deep enough that only the front cutting portion
            // protrudes, so it reads as sitting IN a pocket rather than floating next to one.
            var holderLeft = Math.Max(0, insertCenterX - insertEdge * 0.15);
            var holderRight = holderLeft + overhang;
            var holderBottom = -shankSize / 2.0;
            var holderTop = shankSize / 2.0;

            var mxMax = Math.Max(insertRearX, holderRight);
            var myHalf = Math.Max(insertHalfHeight, shankSize / 2.0);

            var (scale, PxX, PxY) = Fit(canvas, mxMax, myHalf);

            DrawCenterline(canvas, PxX, PxY, mxMax);

            var holder = new Rectangle
            {
                Width = Math.Max(1, (holderRight - holderLeft) * scale),
                Height = Math.Max(1, shankSize * scale),
                Fill = HolderBrush,
                Stroke = Brushes.Black,
                StrokeThickness = 1
            };
            Canvas.SetLeft(holder, PxX(holderLeft));
            Canvas.SetTop(holder, PxY(holderTop));
            canvas.Children.Add(holder);

            var insertPolygon = new Polygon
            {
                Points = new PointCollection(outline.Select(pt => new Point(PxX(pt.X), PxY(pt.Y)))),
                Fill = InsertBrush,
                Stroke = Brushes.Black,
                StrokeThickness = 1
            };
            canvas.Children.Add(insertPolygon);

            DrawCamLockPin(canvas, PxX, PxY, insertCenterX, insertEdge * 0.16 * scale);
            DrawReferenceMarker(canvas, PxX, PxY);
            DrawCornerLabel(canvas, "CENTER MOUNT (CAM-LOCK)");
        }

        // ---- Grooving / threading: blade + holder ----

        private static void DrawGroovingOrThreadingTool(Canvas canvas, ToolGeometryParams p)
        {
            var width = Math.Max(p.Width, 1.0);
            var shankSize = Math.Max(p.ShankSize, 1.0);
            var overhang = Math.Max(p.Overhang, 5.0);
            var noseRadius = Math.Max(p.NoseRadius, 0.0);

            // How far the blade extends from the holder in the plunge (X) direction - the real
            // limiting factor on max groove depth, not just a fixed schematic value.
            var bladeDepth = Math.Max(p.InsertReach, 2.0);

            // Reference decides where the programmed X/Z (0,0) sits relative to the blade's own
            // width - matters when stepping the tool sideways to widen a groove, since the operator
            // needs to know whether the program point tracks the blade's left flank, right flank,
            // or center. Center (the previous only behavior) splits the blade evenly either side.
            var (bladeBottom, bladeTop) = p.Reference switch
            {
                GrooveReference.Left => (0.0, width),
                GrooveReference.Right => (-width, 0.0),
                _ => (-width / 2.0, width / 2.0)
            };
            // The shank is physically centered on the blade itself, not on the programmed reference
            // point - a Left/Right reference only changes which edge the control tracks, not where
            // the blade actually sits on the holder.
            var bladeCenterY = (bladeBottom + bladeTop) / 2.0;

            var topFront = new Point(0, bladeTop);
            var bottomFront = new Point(0, bladeBottom);
            var topRear = new Point(bladeDepth, bladeTop);
            var bottomRear = new Point(bladeDepth, bladeBottom);

            var filletRadius = Math.Min(noseRadius, width / 2.0 - 0.01);
            var topFillet = SampleFilletedCorner(topFront, new Vector(1, 0), new Vector(0, -1), filletRadius);
            var bottomFillet = SampleFilletedCorner(bottomFront, new Vector(0, 1), new Vector(1, 0), filletRadius);

            var bladeOutline = new List<Point> { topRear };
            bladeOutline.AddRange(topFillet);
            bladeOutline.AddRange(bottomFillet);
            bladeOutline.Add(bottomRear);

            var holderLeft = Math.Max(0, bladeDepth - 2.0);
            var holderRight = holderLeft + overhang;
            var mxMax = Math.Max(bladeDepth, holderRight);
            var myHalf = Math.Max(
                Math.Max(Math.Abs(bladeBottom), Math.Abs(bladeTop)),
                Math.Abs(bladeCenterY) + shankSize / 2.0);

            var (scale, PxX, PxY) = Fit(canvas, mxMax, myHalf);

            DrawCenterline(canvas, PxX, PxY, mxMax);

            var holder = new Rectangle
            {
                Width = (holderRight - holderLeft) * scale,
                Height = shankSize * scale,
                Fill = HolderBrush,
                Stroke = Brushes.Black,
                StrokeThickness = 1
            };
            Canvas.SetLeft(holder, PxX(holderLeft));
            Canvas.SetTop(holder, PxY(bladeCenterY + shankSize / 2.0));
            canvas.Children.Add(holder);

            var blade = new Polygon
            {
                Points = new PointCollection(bladeOutline.Select(pt => new Point(PxX(pt.X), PxY(pt.Y)))),
                Fill = InsertBrush,
                Stroke = Brushes.Black,
                StrokeThickness = Math.Max(1, 2 * scale * 0.02) // stays visible even for a thin blade
            };
            canvas.Children.Add(blade);

            DrawReferenceMarker(canvas, PxX, PxY);
            DrawCornerLabel(canvas, $"{p.Reference.ToString().ToUpperInvariant()} REF");
        }

        // ---- Drill: pointed cylinder ----

        private static void DrawDrillTool(Canvas canvas, ToolGeometryParams p)
        {
            var diameter = Math.Max(p.Width, 1.0);
            var overhang = Math.Max(p.Overhang, 5.0);
            var halfDia = diameter / 2.0;

            // Standard 118-degree included point angle -> 59 degrees from the drill axis.
            var tipLength = halfDia / Math.Tan(59.0 * Math.PI / 180);

            var mxMax = tipLength + overhang;
            var myHalf = halfDia;
            var (scale, PxX, PxY) = Fit(canvas, mxMax, myHalf);

            DrawCenterline(canvas, PxX, PxY, mxMax);

            var body = new Rectangle
            {
                Width = overhang * scale,
                Height = diameter * scale,
                Fill = HolderBrush,
                Stroke = Brushes.Black,
                StrokeThickness = 1
            };
            Canvas.SetLeft(body, PxX(tipLength));
            Canvas.SetTop(body, PxY(halfDia));
            canvas.Children.Add(body);

            var point = new Polygon
            {
                Points = new PointCollection
                {
                    new Point(PxX(0), PxY(0)),
                    new Point(PxX(tipLength), PxY(halfDia)),
                    new Point(PxX(tipLength), PxY(-halfDia))
                },
                Fill = InsertBrush,
                Stroke = Brushes.Black,
                StrokeThickness = 1
            };
            canvas.Children.Add(point);
        }

        // ---- Undefined: placeholder ----

        private static void DrawUndefinedPlaceholder(Canvas canvas)
        {
            var canvasWidth = canvas.ActualWidth > 0 ? canvas.ActualWidth : 340;
            var canvasHeight = canvas.ActualHeight > 0 ? canvas.ActualHeight : 300;

            var box = new Rectangle
            {
                Width = Math.Min(160, canvasWidth * 0.6),
                Height = Math.Min(80, canvasHeight * 0.4),
                Stroke = Brushes.DimGray,
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 4, 3 }
            };
            Canvas.SetLeft(box, (canvasWidth - box.Width) / 2);
            Canvas.SetTop(box, (canvasHeight - box.Height) / 2 - 10);
            canvas.Children.Add(box);

            var label = new TextBlock
            {
                Text = "No geometry defined",
                Foreground = Brushes.DimGray,
                FontSize = 11
            };
            Canvas.SetLeft(label, (canvasWidth - box.Width) / 2 + 8);
            Canvas.SetTop(label, (canvasHeight - box.Height) / 2 + box.Height / 2 - 6);
            canvas.Children.Add(label);
        }

        // Standard tangent-line corner fillet: given the two edges leaving a sharp vertex (as
        // outward-pointing direction vectors, not necessarily unit length), returns the arc that
        // rounds the corner to the given radius, ordered from the tangent point along dirToA to
        // the tangent point along dirToB - callers splice this into their polygon in place of the
        // sharp vertex, in the same traversal order they were already walking the outline.
        private static Point[] SampleFilletedCorner(Point tip, Vector dirToA, Vector dirToB, double radius, int segments = 12)
        {
            if (radius <= 1e-9)
                return new[] { tip };

            dirToA.Normalize();
            dirToB.Normalize();

            var cosPhi = Math.Clamp(Vector.Multiply(dirToA, dirToB), -1.0, 1.0);
            var halfAngle = Math.Acos(cosPhi) / 2.0;
            if (halfAngle < 1e-6 || halfAngle > Math.PI / 2 - 1e-6)
                return new[] { tip }; // degenerate (near-parallel or near-straight) - fall back to sharp

            var t = radius / Math.Tan(halfAngle);
            var d = radius / Math.Sin(halfAngle);

            var bisector = dirToA + dirToB;
            bisector.Normalize();

            var center = tip + bisector * d;
            var tangentA = tip + dirToA * t;
            var tangentB = tip + dirToB * t;

            var angleA = Math.Atan2(tangentA.Y - center.Y, tangentA.X - center.X);
            var angleB = Math.Atan2(tangentB.Y - center.Y, tangentB.X - center.X);

            var delta = angleB - angleA;
            while (delta > Math.PI) delta -= 2 * Math.PI;
            while (delta < -Math.PI) delta += 2 * Math.PI;

            var points = new Point[segments + 1];
            for (int i = 0; i <= segments; i++)
            {
                var a = angleA + delta * i / segments;
                points[i] = new Point(center.X + radius * Math.Cos(a), center.Y + radius * Math.Sin(a));
            }
            return points;
        }
    }
}

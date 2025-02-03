﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using IxMilia.Dxf;
using IxMilia.Dxf.Entities;
using IxMilia.Pdf;
using IxMilia.Pdf.Encoders;

namespace IxMilia.Converters
{
    public class DxfToPdfConverterOptions
    {
        public PdfMeasurement PageWidth { get; }
        public PdfMeasurement PageHeight { get; }
        public double Scale { get; }
        public ConverterDxfRect DxfRect { get; }
        public ConverterPdfRect PdfRect { get; }

        private Func<string, Task<byte[]>> _contentResolver;

        public DxfToPdfConverterOptions(PdfMeasurement pageWidth, PdfMeasurement pageHeight, double scale, Func<string, Task<byte[]>> contentResolver = null)
        {
            PageWidth = pageWidth;
            PageHeight = pageHeight;
            Scale = scale;
            this.DxfRect = null;
            this.PdfRect = null;
            _contentResolver = contentResolver;
        }

        public DxfToPdfConverterOptions(PdfMeasurement pageWidth, PdfMeasurement pageHeight, ConverterDxfRect dxfSource, ConverterPdfRect pdfDestination, Func<string, Task<byte[]>> contentResolver = null)
        {
            PageWidth = pageWidth;
            PageHeight = pageHeight;
            Scale = 1d;
            this.DxfRect = dxfSource ?? throw new ArgumentNullException(nameof(dxfSource));
            this.PdfRect = pdfDestination ?? throw new ArgumentNullException(nameof(pdfDestination));
            _contentResolver = contentResolver;
        }

        public async Task<byte[]> ResolveContentAsync(string path)
        {
            if (_contentResolver != null)
            {
                var content = await _contentResolver(path);
                return content;
            }
            else
            {
                return null;
            }
        }
    }

    public class DxfToPdfConverter : IConverter<DxfFile, PdfFile, DxfToPdfConverterOptions>
    {
        // TODO How to manage fonts? PDF has a dictionary of fonts...
        private static readonly PdfFont Font = new PdfFontType1(PdfFontType1Type.Helvetica);

        public async Task<PdfFile> Convert(DxfFile source, DxfToPdfConverterOptions options)
        {
            // adapted from https://github.com/ixmilia/bcad/blob/main/src/IxMilia.BCad.FileHandlers/Plotting/Pdf/PdfPlotter.cs
            var transform = CreateTransformation(source.ActiveViewPort, options);
            var pdf = new PdfFile();
            var page = new PdfPage(options.PageWidth, options.PageHeight);
            pdf.Pages.Add(page);

            var builder = new PdfPathBuilder();
            var dimStyles = source.DimensionStyles.ToDictionary(d => d.Name, d => d);

            // do images first so lines and text appear on top...
            foreach (var layer in source.Layers.Where(l => l.IsLayerOn))
            {
                foreach (var image in source.Entities.OfType<DxfImage>().Where(i => i.Layer == layer.Name))
                {
                    await TryConvertEntity(image, layer, dimStyles, source.Header.DrawingUnits, source.Header.UnitFormat, source.Header.UnitPrecision, transform, builder, page, options);
                }
            }

            // ...now do lines and text
            foreach (var layer in source.Layers.Where(l => l.IsLayerOn))
            {
                foreach (var entity in source.Entities.Where(e => e.Layer == layer.Name && e.EntityType != DxfEntityType.Image))
                {
                    await TryConvertEntity(entity, layer, dimStyles, source.Header.DrawingUnits, source.Header.UnitFormat, source.Header.UnitPrecision, transform, builder, page, options);
                    // if that failed, emit some diagnostic hint? Callback?
                }
            }

            if (builder.Items.Count > 0)
            {
                page.Items.Add(builder.ToPath());
            }

            return pdf;
        }

        /// <summary>
        /// Creates an <paramref name="affine"/> transform for DXF to PDF coordinate transformation and
        /// a <paramref name="scale"/> transform for relative values like radii.
        /// </summary>
        /// <param name="viewPort">The DXF view port.</param>
        /// <param name="options">The converter options.</param>
        /// <param name="scale">[out] The (relative) scale transform.</param>
        /// <param name="affine">[out] The (absolute) affine transform including scale.</param>
        private static Matrix4 CreateTransformation(DxfViewPort viewPort, DxfToPdfConverterOptions options)
        {
            Matrix4 result;
            if (options.DxfRect != null && options.PdfRect != null)
            {
                // user supplied source and destination rectangles, no trouble with units
                var dxfRect = options.DxfRect;
                double pdfOffsetX = options.PdfRect.Left.AsPoints();
                double pdfOffsetY = options.PdfRect.Bottom.AsPoints();
                double scaleX = options.PdfRect.Width.AsPoints() / dxfRect.Width;
                double scaleY = options.PdfRect.Height.AsPoints() / dxfRect.Height;
                double dxfOffsetX = dxfRect.Left;
                double dxfOffsetY = dxfRect.Bottom;
                result = Matrix4.CreateTranslate(+pdfOffsetX, +pdfOffsetY, 0.0)
                    * Matrix4.CreateScale(scaleX, scaleY, 0.0)
                    * Matrix4.CreateTranslate(-dxfOffsetX, -dxfOffsetY, 0.0);
            }
            else
            {
                // TODO this code assumes DXF unit inch - use actual unit from header instead!
                // scale depends on the unit, output "pdf points" with 72 DPI
                const double dotsPerInch = 72;
                result = Matrix4.Identity
                    * Matrix4.CreateScale(options.Scale * dotsPerInch, options.Scale * dotsPerInch, 0.0)
                    * Matrix4.CreateTranslate(-viewPort.LowerLeft.X, -viewPort.LowerLeft.Y, 0.0);
            }

            return result;
        }

        #region Entity Conversions

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static async Task<bool> TryConvertEntity(DxfEntity entity, DxfLayer layer, Dictionary<string, DxfDimStyle> dimStyles, DxfDrawingUnits drawingUnits, DxfUnitFormat unitFormat, int unitPrecision, Matrix4 transform, PdfPathBuilder builder, PdfPage page, DxfToPdfConverterOptions options)
        {
            switch (entity)
            {
                case DxfDimensionBase dim:
                    return ConvertDimension(dim, layer, dimStyles, drawingUnits, unitFormat, unitPrecision, transform, builder, page);
                case DxfText text:
                    // TODO flush path builder and recreate
                    page.Items.Add(ConvertText(text, layer, transform));
                    return true;
                case DxfLine line:
                    Add(ConvertLine(line, layer, transform), builder);
                    return true;
                case DxfModelPoint point:
                    Add(ConvertPoint(point, layer, transform), builder);
                    return true;
                case DxfArc arc:
                    Add(ConvertArc(arc, layer, transform), builder);
                    return true;
                case DxfCircle circle:
                    Add(ConvertCircle(circle, layer, transform), builder);
                    return true;
                case DxfLwPolyline lwPolyline:
                    Add(ConvertLwPolyline(lwPolyline, layer, transform), builder);
                    return true;
                case DxfPolyline polyline:
                    Add(ConvertPolyline(polyline, layer, transform), builder);
                    return true;
                case DxfImage image:
                    var imageItem = await TryConvertImage(image, layer, transform, options);
                    if (imageItem != null)
                    {
                        // TODO flush path builder and recreate
                        page.Items.Add(imageItem);
                        return true;
                    }
                    else
                    {
                        return false;
                    }
                case DxfSolid solid:
                    Add(ConvertSolid(solid, layer, transform), builder);
                    return true;
                default:
                    return false;
            }

            void Add(IEnumerable<IPdfPathItem> items, PdfPathBuilder b)
            {
                foreach (IPdfPathItem item in items)
                {
                    b.Add(item);
                }
            }
        }

        private static bool ConvertDimension(DxfDimensionBase dim, DxfLayer layer, Dictionary<string, DxfDimStyle> dimStyles, DxfDrawingUnits drawingUnits, DxfUnitFormat unitFormat, int unitPrecision, Matrix4 transform, PdfPathBuilder builder, PdfPage page)
        {
            var point1 = new Vector();
            var point2 = new Vector();
            var point3 = new Vector();
            var isAligned = false;
            switch (dim.DimensionType)
            {
                case DxfDimensionType.Aligned:
                    var aligned = (DxfAlignedDimension)dim;
                    point1 = aligned.DefinitionPoint2.ToVector();
                    point2 = aligned.DefinitionPoint3.ToVector();
                    point3 = aligned.DefinitionPoint1.ToVector();
                    isAligned = true;
                    break;
                case DxfDimensionType.RotatedHorizontalOrVertical:
                    var rotated = (DxfRotatedDimension)dim;
                    point1 = rotated.DefinitionPoint2.ToVector();
                    point2 = rotated.DefinitionPoint3.ToVector();
                    point3 = rotated.DefinitionPoint1.ToVector();
                    isAligned = false;
                    break;
                default:
                    return false;
            }

            var dimStyle = dimStyles[dim.DimensionStyleName];
            var dimensionSettings = dimStyle.ToDimensionSettings();
            var text = dim.Text;
            if (text is null || text == "<>")
            {
                // compute and format
                var tempDimensionProperties = LinearDimensionProperties.BuildFromValues(
                    point1,
                    point2,
                    point3,
                    isAligned,
                    null,
                    0.0,
                    dimensionSettings);
                text = DimensionExtensions.GenerateLinearDimensionText(tempDimensionProperties.DimensionLength, drawingUnits.ToDrawingUnits(), unitFormat.ToUnitFormat(), unitPrecision);
            }
            else if (text == " ")
            {
                // suppress and display no gap
                text = string.Empty;
            }

            var textWidth = dimStyle.DimensioningTextHeight * text.Length * 0.6; // this is really bad
            var dimensionProperties = LinearDimensionProperties.BuildFromValues(
                point1,
                point2,
                point3,
                isAligned,
                text,
                textWidth,
                dimensionSettings);
            foreach (var item in dimensionProperties.DimensionLineSegments.SelectMany(s =>
                {
                    var line = new DxfLine(s.Start.ToDxfPoint(), s.End.ToDxfPoint())
                    {
                        Color = dim.Color,
                        Layer = dim.Layer,
                    };
                    return ConvertLine(line, layer, transform);
                }))
            {
                builder.Add(item);
            }

            foreach (var item in dimensionProperties.DimensionTriangles.Select(s =>
                {
                    var p1 = transform.Transform(s.P1).ToPdfPoint(PdfMeasurementType.Point);
                    var p2 = transform.Transform(s.P2).ToPdfPoint(PdfMeasurementType.Point);
                    var p3 = transform.Transform(s.P3).ToPdfPoint(PdfMeasurementType.Point);
                    var pdfStreamState = new PdfStreamState(
                        strokeColor: ToPdfColor(dim.Color.ToRGB()),
                        strokeWidth: PdfMeasurement.Points(1.0));
                    return new PdfFilledPolygon(new[] { p1, p2, p3 }, pdfStreamState);
                }))
            {
                builder.Add(item);
            }

            var dxfText = new DxfText(dimensionProperties.TextLocation.ToDxfPoint(), dimStyle.DimensioningTextHeight, text)
            {
                Color = dim.Color,
                Layer = dim.Layer,
                Rotation = dimensionProperties.DimensionLineAngle * 180.0 / Math.PI,
            };
            page.Items.Add(ConvertText(dxfText, layer, transform));

            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static PdfText ConvertText(DxfText text, DxfLayer layer, Matrix4 transform)
        {
            // TODO horizontal and vertical justification (manual calculation for PDF, measure text?)
            // TODO Thickness, Rotation, TextStyleName, SecondAlignmentPoint
            // TODO IsTextUpsideDown, IsTextBackwards
            // TODO RelativeXScaleFactor
            // TODO TextHeight unit? Same as other scale?
            // TODO TextStyleName probably maps to something meaningful (bold, italic, etc?)
            var rotationAngleInRadians = text.Rotation * Math.PI / 180.0;
            var fontSize = transform.TransformedScale(0.0, text.TextHeight).ToPdfPoint(PdfMeasurementType.Point).Y;
            PdfPoint location = transform.Transform(text.Location.ToVector()).ToPdfPoint(PdfMeasurementType.Point);
            var pdfStreamState = new PdfStreamState(GetPdfColor(text, layer));
            return new PdfText(text.Value, Font, fontSize, location, rotationAngleInRadians, pdfStreamState);
        }

        private static IEnumerable<IPdfPathItem> ConvertPoint(DxfModelPoint point, DxfLayer layer, Matrix4 transform)
        {
            var p = transform.Transform(point.Location.ToVector()).ToPdfPoint(PdfMeasurementType.Point);
            var thickness = transform.TransformedScale(point.Thickness, 0.0).ToPdfPoint(PdfMeasurementType.Point).X;
            if (thickness.RawValue < 1)
            {
                thickness = PdfMeasurement.Points(1);
            }
            // TODO fill circle? For now fake it via stroke thickness.
            var pdfStreamState = new PdfStreamState(
                strokeColor: GetPdfColor(point, layer),
                strokeWidth: thickness);
            yield return new PdfCircle(p, thickness / 2, pdfStreamState);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static IEnumerable<IPdfPathItem> ConvertLine(DxfLine line, DxfLayer layer, Matrix4 transform)
        {
            var p1 = transform.Transform(line.P1.ToVector()).ToPdfPoint(PdfMeasurementType.Point);
            var p2 = transform.Transform(line.P2.ToVector()).ToPdfPoint(PdfMeasurementType.Point);
            var pdfStreamState = new PdfStreamState(
                strokeColor: GetPdfColor(line, layer),
                strokeWidth: GetStrokeWidth(line, layer));
            yield return new PdfLine(p1, p2, pdfStreamState);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static IEnumerable<IPdfPathItem> ConvertCircle(DxfCircle circle, DxfLayer layer, Matrix4 transform)
        {
            var pdfStreamState = new PdfStreamState(
                strokeColor: GetPdfColor(circle, layer),
                strokeWidth: GetStrokeWidth(circle, layer));
            // a circle becomes an ellipse, unless aspect ratio is kept.
            var center = transform.Transform(circle.Center.ToVector()).ToPdfPoint(PdfMeasurementType.Point);
            var radius = transform.TransformedScale(new Vector(circle.Radius, circle.Radius, circle.Radius))
                .ToPdfPoint(PdfMeasurementType.Point);
            yield return new PdfEllipse(center, radius.X, radius.Y, state: pdfStreamState);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static IEnumerable<IPdfPathItem> ConvertArc(DxfArc arc, DxfLayer layer, Matrix4 transform)
        {
            var pdfStreamState = new PdfStreamState(
                strokeColor: GetPdfColor(arc, layer),
                strokeWidth: GetStrokeWidth(arc, layer));
            var center = transform.Transform(arc.Center.ToVector()).ToPdfPoint(PdfMeasurementType.Point);
            var radius = transform.TransformedScale(new Vector(arc.Radius, arc.Radius, arc.Radius))
                .ToPdfPoint(PdfMeasurementType.Point);
            const double rotation = 0;
            double startAngleRad = arc.StartAngle * Math.PI / 180;
            double endAngleRad = arc.EndAngle * Math.PI / 180;
            yield return new PdfEllipse(center, radius.X, radius.Y, rotation, startAngleRad, endAngleRad,
                pdfStreamState);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static IEnumerable<IPdfPathItem> ConvertLwPolyline(DxfLwPolyline lwPolyline, DxfLayer layer, Matrix4 transform)
        {
            var pdfStreamState = new PdfStreamState(
                strokeColor: GetPdfColor(lwPolyline, layer),
                strokeWidth: GetStrokeWidth(lwPolyline, layer));
            IList<DxfLwPolylineVertex> vertices = lwPolyline.Vertices;
            int n = vertices.Count;
            DxfLwPolylineVertex vertex = vertices[0];

            for (int i = 1; i < n; i++)
            {
                DxfLwPolylineVertex next = vertices[i];
                yield return ConvertLwPolylineSegment(vertex, next, transform, pdfStreamState);
                vertex = next;
            }
            if (lwPolyline.IsClosed)
            {
                var next = vertices[0];
                yield return ConvertLwPolylineSegment(vertex, next, transform, pdfStreamState);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static IPdfPathItem ConvertLwPolylineSegment(DxfLwPolylineVertex vertex, DxfLwPolylineVertex next, Matrix4 transform, PdfStreamState pdfStreamState)
        {
            var p1 = transform.Transform(new Vector(vertex.X, vertex.Y, 0))
                .ToPdfPoint(PdfMeasurementType.Point);
            var p2 = transform.Transform(new Vector(next.X, next.Y, 0))
                .ToPdfPoint(PdfMeasurementType.Point);
            if (vertex.Bulge.IsCloseTo(0.0))
            {
                return new PdfLine(p1, p2, pdfStreamState);
            }

            double dx = next.X - vertex.X;
            double dy = next.Y - vertex.Y;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length.IsCloseTo(1e-10))
            {
                // segment is very short, avoid numerical problems
                return new PdfLine(p1, p2, pdfStreamState);
            }

            double alpha = 4.0 * Math.Atan(vertex.Bulge);
            double radius = length / (2.0 * Math.Abs(Math.Sin(alpha * 0.5)));

            double bulgeFactor = Math.Sign(vertex.Bulge) * Math.Cos(alpha * 0.5) * radius;
            double normalX = -(dy / length) * bulgeFactor;
            double normalY = +(dx / length) * bulgeFactor;

            // calculate center (dxf coordinate system), start and end angle
            double cx = (vertex.X + next.X) / 2 + normalX;
            double cy = (vertex.Y + next.Y) / 2 + normalY;
            double startAngle;
            double endAngle;
            if (vertex.Bulge > 0) // counter-clockwise
            {
                startAngle = Math.Atan2(vertex.Y - cy, vertex.X - cx);
                endAngle = Math.Atan2(next.Y - cy, next.X - cx);
            }
            else // clockwise: flip start and end angle
            {
                startAngle = Math.Atan2(next.Y - cy, next.X - cx);
                endAngle = Math.Atan2(vertex.Y - cy, vertex.X - cx);
            }

            // transform to PDF coordinate system
            var center = transform.Transform(new Vector(cx, cy, 0)).ToPdfPoint(PdfMeasurementType.Point);
            var pdfRadius = transform.TransformedScale(new Vector(radius, radius, radius)).ToPdfPoint(PdfMeasurementType.Point);
            const double rotation = 0;
            return new PdfEllipse(center, pdfRadius.X, pdfRadius.Y, rotation,
                startAngle, endAngle, pdfStreamState);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static IEnumerable<IPdfPathItem> ConvertPolyline(DxfPolyline polyline, DxfLayer layer, Matrix4 transform)
        {
            var pdfStreamState = new PdfStreamState(
                strokeColor: GetPdfColor(polyline, layer),
                strokeWidth: GetStrokeWidth(polyline, layer));
            var vertices = polyline.Vertices;
            var n = vertices.Count;
            var vertex = vertices[0];
            for (int i = 1; i < n; i++)
            {
                var next = vertices[i];
                yield return ConvertPolylineSegment(vertex, next, transform, pdfStreamState);
                vertex = next;
            }
            if (polyline.IsClosed)
            {
                var next = vertices[0];
                yield return ConvertPolylineSegment(vertex, next, transform, pdfStreamState);
            }
        }

        private static IEnumerable<IPdfPathItem> ConvertSolid(DxfSolid solid, DxfLayer layer, Matrix4 transform)
        {
            var p1 = transform.Transform(solid.FirstCorner.ToVector()).ToPdfPoint(PdfMeasurementType.Point);
            var p2 = transform.Transform(solid.SecondCorner.ToVector()).ToPdfPoint(PdfMeasurementType.Point);
            var p3 = transform.Transform(solid.FourthCorner.ToVector()).ToPdfPoint(PdfMeasurementType.Point); // n.b., the dxf representation of a solid swaps the last two vertices
            var p4 = transform.Transform(solid.ThirdCorner.ToVector()).ToPdfPoint(PdfMeasurementType.Point);
            var pdfStreamState = new PdfStreamState(
                strokeColor: GetPdfColor(solid, layer),
                strokeWidth: GetStrokeWidth(solid, layer));
            yield return new PdfFilledPolygon(new[] { p1, p2, p3, p4 }, pdfStreamState);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static IPdfPathItem ConvertPolylineSegment(DxfVertex vertex, DxfVertex next, Matrix4 transform, PdfStreamState pdfStreamState)
        {
            var p1 = transform.Transform(new Vector(vertex.Location.X, vertex.Location.Y, 0))
                .ToPdfPoint(PdfMeasurementType.Point);
            var p2 = transform.Transform(new Vector(next.Location.X, next.Location.Y, 0))
                .ToPdfPoint(PdfMeasurementType.Point);
            if (vertex.Bulge.IsCloseTo(0.0))
            {
                return new PdfLine(p1, p2, pdfStreamState);
            }

            var dx = next.Location.X - vertex.Location.X;
            var dy = next.Location.Y - vertex.Location.Y;
            var length = Math.Sqrt(dx * dx + dy * dy);
            if (length.IsCloseTo(1e-10))
            {
                // segment is very short, avoid numerical problems
                return new PdfLine(p1, p2, pdfStreamState);
            }

            var alpha = 4.0 * Math.Atan(vertex.Bulge);
            var radius = length / (2.0 * Math.Abs(Math.Sin(alpha * 0.5)));

            var bulgeFactor = Math.Sign(vertex.Bulge) * Math.Cos(alpha * 0.5) * radius;
            var normalX = -(dy / length) * bulgeFactor;
            var normalY = +(dx / length) * bulgeFactor;

            // calculate center (dxf coordinate system), start and end angle
            var cx = (vertex.Location.X + next.Location.X) / 2 + normalX;
            var cy = (vertex.Location.Y + next.Location.Y) / 2 + normalY;
            double startAngle;
            double endAngle;
            if (vertex.Bulge > 0) // counter-clockwise
            {
                startAngle = Math.Atan2(vertex.Location.Y - cy, vertex.Location.X - cx);
                endAngle = Math.Atan2(next.Location.Y - cy, next.Location.X - cx);
            }
            else // clockwise: flip start and end angle
            {
                startAngle = Math.Atan2(next.Location.Y - cy, next.Location.X - cx);
                endAngle = Math.Atan2(vertex.Location.Y - cy, vertex.Location.X - cx);
            }

            // transform to PDF coordinate system
            var center = transform.Transform(new Vector(cx, cy, 0)).ToPdfPoint(PdfMeasurementType.Point);
            var pdfRadius = transform.TransformedScale(new Vector(radius, radius, radius)).ToPdfPoint(PdfMeasurementType.Point);
            const double rotation = 0;
            return new PdfEllipse(center, pdfRadius.X, pdfRadius.Y, rotation,
                startAngle, endAngle, pdfStreamState);
        }

        private static async Task<PdfImageItem> TryConvertImage(DxfImage image, DxfLayer layer, Matrix4 transform, DxfToPdfConverterOptions options)
        {
            // prepare image decoders
            IPdfEncoder[] encoders = Array.Empty<IPdfEncoder>();
            switch (Path.GetExtension(image.ImageDefinition.FilePath).ToLowerInvariant())
            {
                case ".jpg":
                case ".jpeg":
                    encoders = new IPdfEncoder[] { new CustomPassThroughEncoder("DCTDecode") };
                    break;
                case ".png":
                // png isn't directly supported by pdf; the raw pixels will have to be embedded, probably in a FlateDecode filter
                default:
                    // unsupported image
                    return null;
            }

            // get raw image bytes
            var imageBytes = await options.ResolveContentAsync(image.ImageDefinition.FilePath);
            if (imageBytes == null)
            {
                // couldn't resolve image content
                return null;
            }

            var imageSizeDxf = new Vector(image.UVector.Length * image.ImageSize.X, image.VVector.Length * image.ImageSize.Y, 0.0);
            var imageSizeOnPage = transform.TransformedScale(imageSizeDxf);
            var radians = Math.Atan2(image.UVector.Y, image.UVector.X);
            var degrees = radians * 180.0 / Math.PI;
            var locationOnPage = transform.Transform(new Vector(image.Location.X, image.Location.Y, 0.0));
            var imageTransform =
                Matrix4.CreateTranslate(locationOnPage) *
                Matrix4.CreateScale(imageSizeOnPage.X, imageSizeOnPage.Y, 1.0) *
                Matrix4.RotateAboutZ(degrees);
            var colorSpace = PdfColorSpace.DeviceRGB; // TODO: read from image
            var bitsPerComponent = 8; // TODO: read from image
            var imageObject = new PdfImageObject((int)image.ImageSize.X, (int)image.ImageSize.Y, colorSpace, bitsPerComponent, imageBytes, encoders);
            var imageItem = new PdfImageItem(imageObject, imageTransform.ToPdfMatrix());
            return imageItem;
        }

        #endregion

        #region Color and Stroke Width Conversion

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static PdfColor GetPdfColor(DxfEntity entity, DxfLayer layer)
        {
            int rgb = entity.Color24Bit;
            if (rgb > 0)
            {
                return ToPdfColor(rgb);
            }
            DxfColor c = GetFinalDxfColor(entity, layer);
            if (c != null && c.IsIndex)
            {
                rgb = c.ToRGB();
                return ToPdfColor(rgb);
            }
            // default to black, probably not correct.
            return new PdfColor(0, 0, 0);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static PdfColor ToPdfColor(int rgb)
        {
            byte r = (byte)(rgb >> 16);
            byte g = (byte)(rgb >> 8);
            byte b = (byte)rgb;

            // It seems DXF does not distinguish white/black:
            // both map to index=7 which is (r=255,g=255,b=255)
            // but white stroke on white background is crap.
            // This doesn't feel right, better ideas?
            if (r == byte.MaxValue && g == byte.MaxValue && b == byte.MaxValue)
            {
                return new PdfColor(0, 0, 0);
            }
            return new PdfColor(r / 255.0, g / 255.0, b / 255.0);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static DxfColor GetFinalDxfColor(DxfEntity entity, DxfLayer layer)
        {
            DxfColor c = entity.Color;
            if (c == null || c.IsByLayer)
            {
                return layer.Color;
            }
            if (c.IsIndex)
            {
                return c;
            }
            // we could build a Dictionary<DxfBlock, DxfColor> for the c.IsByBlock case
            // not sure how to retrieve color for the remaining cases
            return null;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static PdfMeasurement GetStrokeWidth(DxfEntity entity, DxfLayer layer)
        {
            // TODO many entities have a Thickness property (which is often zero).
            DxfLineWeight lw = new DxfLineWeight { Value = entity.LineweightEnumValue };
            DxfLineWeightType type = lw.LineWeightType;
            if (type == DxfLineWeightType.ByLayer)
            {
                lw = layer.LineWeight;
            }
            if (lw.Value == 0)
            {
                return default(PdfMeasurement);
            }
            if (lw.Value < 0)
            {
                return PdfMeasurement.Points(1); // smallest valid stroke width
            }
            // TODO What is the meaning of this short? Some default app-dependent table? DXF spec doesn't tell.
            // QCad 1mm => lw.Value==100
            return PdfMeasurement.Mm(lw.Value / 100.0);
        }

        #endregion
    }
}

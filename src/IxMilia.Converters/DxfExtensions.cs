using System;
using System.Collections.Generic;
using System.Linq;
using IxMilia.Dxf;
using IxMilia.Dxf.Entities;

namespace IxMilia.Converters
{
    public static class DxfExtensions
    {
        public const double DegreesToRadians = Math.PI / 180.0;
        
        public static DxfPoint GetPointFromAngle(this DxfCircle circle, double angleInDegrees)
        {
            var angleInRadians = angleInDegrees * DegreesToRadians;
            var sin = Math.Sin(angleInRadians);
            var cos = Math.Cos(angleInRadians);
            return new DxfPoint(cos * circle.Radius, sin * circle.Radius, 0.0) + circle.Center;
        }
        
        public static Vector ToVector(this DxfPoint point) => new Vector(point.X, point.Y, point.Z);

        public static DxfPoint ToDxfPoint(this Vector v) => new DxfPoint(v.X, v.Y, v.Z);

        public static DimensionSettings ToDimensionSettings(this DxfDimStyle dimStyle)
        {
            return new DimensionSettings(
                textHeight: dimStyle.DimensioningTextHeight,
                extensionLineOffset: dimStyle.DimensionExtensionLineOffset,
                extensionLineExtension: dimStyle.DimensionExtensionLineExtension,
                dimensionLineGap: dimStyle.DimensionLineGap,
                arrowSize: dimStyle.DimensioningArrowSize,
                tickSize: dimStyle.DimensioningTickSize);
        }

        public static UnitFormat ToUnitFormat(this DxfUnitFormat unitFormat)
        {
            switch (unitFormat)
            {
                case DxfUnitFormat.Architectural:
                case DxfUnitFormat.ArchitecturalStacked:
                    return UnitFormat.Architectural;
                case DxfUnitFormat.Decimal:
                case DxfUnitFormat.Engineering:
                case DxfUnitFormat.Scientific:
                    return UnitFormat.Decimal;
                case DxfUnitFormat.Fractional:
                case DxfUnitFormat.FractionalStacked:
                    return UnitFormat.Fractional;
                default:
                    throw new ArgumentOutOfRangeException(nameof(unitFormat));
            }
        }

        public static DrawingUnits ToDrawingUnits(this DxfDrawingUnits drawingUnits)
        {
            switch (drawingUnits)
            {
                case DxfDrawingUnits.English:
                    return DrawingUnits.English;
                case DxfDrawingUnits.Metric:
                    return DrawingUnits.Metric;
                default:
                    throw new ArgumentOutOfRangeException(nameof(drawingUnits));
            }
        }
    }
}

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing
$systemDrawingAssembly = [System.Drawing.Bitmap].Assembly.Location
$rendererSource = @"
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

public static class VoicePasteIconRenderer
{
    public static byte[] Render(int iconSize)
    {
        var renderSize = iconSize * 4;
        using (var source = new Bitmap(renderSize, renderSize, PixelFormat.Format32bppArgb))
        {
            using (var graphics = Graphics.FromImage(source))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.CompositingMode = CompositingMode.SourceOver;
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

                var scale = renderSize / 256f;
                using (var surfacePath = RoundedRectangle(16 * scale, 16 * scale, 224 * scale, 224 * scale, 56 * scale))
                using (var surface = new LinearGradientBrush(
                    new PointF(38 * scale, 26 * scale),
                    new PointF(218 * scale, 232 * scale),
                    ColorTranslator.FromHtml("#123F67"),
                    ColorTranslator.FromHtml("#0B263F")))
                {
                    graphics.FillPath(surface, surfacePath);
                }

                using (var shadow = new SolidBrush(Color.FromArgb(86, 6, 21, 37)))
                using (var shadowPath = RoundedRectangle(60 * scale, 53 * scale, 78 * scale, 119 * scale, 39 * scale))
                {
                    graphics.TranslateTransform(0, 5 * scale);
                    graphics.FillPath(shadow, shadowPath);
                    graphics.ResetTransform();
                }

                using (var micPath = RoundedRectangle(60 * scale, 48 * scale, 78 * scale, 119 * scale, 39 * scale))
                using (var mic = new SolidBrush(ColorTranslator.FromHtml("#F6FCFF")))
                {
                    graphics.FillPath(mic, micPath);
                }

                using (var accent = new LinearGradientBrush(
                    new PointF(46 * scale, 58 * scale),
                    new PointF(196 * scale, 208 * scale),
                    ColorTranslator.FromHtml("#B9F5FF"),
                    ColorTranslator.FromHtml("#5DD6F2")))
                using (var accentPen = new Pen(accent, 14 * scale))
                using (var listening = new GraphicsPath())
                {
                    accentPen.StartCap = LineCap.Round;
                    accentPen.EndCap = LineCap.Round;
                    accentPen.LineJoin = LineJoin.Round;
                    listening.AddBezier(39 * scale, 108 * scale, 39 * scale, 165 * scale, 69 * scale, 193 * scale, 109 * scale, 193 * scale);
                    listening.AddBezier(109 * scale, 193 * scale, 149 * scale, 193 * scale, 179 * scale, 165 * scale, 179 * scale, 117 * scale);
                    graphics.DrawPath(accentPen, listening);
                }

                using (var whitePen = new Pen(ColorTranslator.FromHtml("#F6FCFF"), 13 * scale))
                {
                    whitePen.StartCap = LineCap.Round;
                    whitePen.EndCap = LineCap.Round;
                    graphics.DrawLine(whitePen, 109 * scale, 193 * scale, 109 * scale, 218 * scale);
                    graphics.DrawLine(whitePen, 83 * scale, 218 * scale, 135 * scale, 218 * scale);
                }

                using (var accent = new LinearGradientBrush(
                    new PointF(151 * scale, 124 * scale),
                    new PointF(203 * scale, 124 * scale),
                    ColorTranslator.FromHtml("#B9F5FF"),
                    ColorTranslator.FromHtml("#5DD6F2")))
                using (var arrowPen = new Pen(accent, 13 * scale))
                {
                    arrowPen.StartCap = LineCap.Round;
                    arrowPen.EndCap = LineCap.Round;
                    arrowPen.LineJoin = LineJoin.Round;
                    graphics.DrawLine(arrowPen, 151 * scale, 124 * scale, 198 * scale, 124 * scale);
                    graphics.DrawLine(arrowPen, 181 * scale, 103 * scale, 203 * scale, 124 * scale);
                    graphics.DrawLine(arrowPen, 203 * scale, 124 * scale, 181 * scale, 145 * scale);
                }

                using (var caretPen = new Pen(ColorTranslator.FromHtml("#F6FCFF"), 10 * scale))
                {
                    caretPen.StartCap = LineCap.Round;
                    caretPen.EndCap = LineCap.Round;
                    graphics.DrawLine(caretPen, 218 * scale, 86 * scale, 218 * scale, 162 * scale);
                    graphics.DrawLine(caretPen, 207 * scale, 86 * scale, 229 * scale, 86 * scale);
                    graphics.DrawLine(caretPen, 207 * scale, 162 * scale, 229 * scale, 162 * scale);
                }
            }

            using (var target = new Bitmap(iconSize, iconSize, PixelFormat.Format32bppArgb))
            {
                using (var graphics = Graphics.FromImage(target))
                {
                    graphics.SmoothingMode = SmoothingMode.HighQuality;
                    graphics.CompositingQuality = CompositingQuality.HighQuality;
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    graphics.DrawImage(source, new Rectangle(0, 0, iconSize, iconSize));
                }

                using (var stream = new MemoryStream())
                {
                    target.Save(stream, ImageFormat.Png);
                    return stream.ToArray();
                }
            }
        }
    }

    private static GraphicsPath RoundedRectangle(float x, float y, float width, float height, float radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(x, y, diameter, diameter, 180, 90);
        path.AddArc(x + width - diameter, y, diameter, diameter, 270, 90);
        path.AddArc(x + width - diameter, y + height - diameter, diameter, diameter, 0, 90);
        path.AddArc(x, y + height - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
"@
Add-Type -TypeDefinition $rendererSource -ReferencedAssemblies $systemDrawingAssembly

$assetDirectory = Join-Path $PSScriptRoot '..\src\VoicePaste.App\Assets'
$iconPath = Join-Path $assetDirectory 'VoicePaste.ico'
$sizes = @(16, 24, 32, 48, 64, 256)
$images = foreach ($size in $sizes) {
    [pscustomobject]@{
        Size = $size
        Bytes = [VoicePasteIconRenderer]::Render($size)
    }
}

$directorySize = 6 + (16 * $images.Count)
$offset = $directorySize
$stream = [System.IO.MemoryStream]::new()
$writer = [System.IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$images.Count)

    foreach ($image in $images) {
        $dimension = if ($image.Size -eq 256) { [byte]0 } else { [byte]$image.Size }
        $writer.Write($dimension)
        $writer.Write($dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$image.Bytes.Length)
        $writer.Write([uint32]$offset)
        $offset += $image.Bytes.Length
    }

    foreach ($image in $images) {
        $writer.Write($image.Bytes)
    }

    [System.IO.Directory]::CreateDirectory($assetDirectory) | Out-Null
    [System.IO.File]::WriteAllBytes($iconPath, $stream.ToArray())
}
finally {
    $writer.Dispose()
    $stream.Dispose()
}

Write-Output "Generated $iconPath"

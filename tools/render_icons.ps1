# Renders Icons.xaml to a PNG contact sheet so the geometry can actually be
# eyeballed. Uses WPF's own parser, so what you see is what the app will draw.
#   powershell -STA -File tools\render_icons.ps1
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$xamlPath = Join-Path $root 'Icons.xaml'
$outPath = Join-Path $root 'tools\icons-preview.png'

$reader = New-Object System.Xml.XmlTextReader $xamlPath
$dict = [Windows.Markup.XamlReader]::Load($reader)
$reader.Close()

# filter by value type, not key name — IconPath/IconPathFilled are styles
$keys = @($dict.Keys | Where-Object { $dict[$_] -is [Windows.Media.Geometry] } | Sort-Object)
$cols = 6
$rows = [math]::Ceiling($keys.Count / $cols)
$cell = 120.0
$iconBox = 54.0
$W = $cols * $cell
$H = $rows * $cell

$visual = New-Object Windows.Media.DrawingVisual
$dc = $visual.RenderOpen()

$bg = New-Object Windows.Media.SolidColorBrush ([Windows.Media.Color]::FromRgb(5, 5, 5))
$dc.DrawRectangle($bg, $null, (New-Object Windows.Rect 0, 0, $W, $H))

$stroke = New-Object Windows.Media.SolidColorBrush ([Windows.Media.Color]::FromRgb(242, 242, 242))
$pen = New-Object Windows.Media.Pen $stroke, 1.5
$pen.StartLineCap = 'Round'
$pen.EndLineCap = 'Round'
$pen.LineJoin = 'Round'
$labelBrush = New-Object Windows.Media.SolidColorBrush ([Windows.Media.Color]::FromRgb(140, 140, 140))
$typeface = New-Object Windows.Media.Typeface 'Segoe UI'

for ($i = 0; $i -lt $keys.Count; $i++) {
    $key = $keys[$i]
    $geo = $dict[$key]
    $col = $i % $cols
    $row = [math]::Floor($i / $cols)
    $ox = $col * $cell
    $oy = $row * $cell

    # scale the 24x24 authoring grid into the icon box, centred in the cell
    $scale = $iconBox / 24.0
    $tx = $ox + ($cell - $iconBox) / 2.0
    $ty = $oy + 18.0

    $group = New-Object Windows.Media.TransformGroup
    $group.Children.Add((New-Object Windows.Media.ScaleTransform $scale, $scale))
    $group.Children.Add((New-Object Windows.Media.TranslateTransform $tx, $ty))

    $dc.PushTransform($group)
    # scale the pen down so stroke weight matches the on-screen 24px rendering
    $p2 = $pen.Clone()
    $p2.Thickness = 1.5
    $dc.DrawGeometry($null, $p2, $geo)
    $dc.Pop()

    $text = New-Object Windows.Media.FormattedText(
        $key.Substring(4),
        [Globalization.CultureInfo]::InvariantCulture,
        [Windows.FlowDirection]::LeftToRight,
        $typeface, 11.0, $labelBrush, 96.0)
    $dc.DrawText($text, (New-Object Windows.Point ($ox + ($cell - $text.Width) / 2.0), ($oy + $iconBox + 26.0)))
}

$dc.Close()

$rtb = New-Object Windows.Media.Imaging.RenderTargetBitmap ([int]$W), ([int]$H), 96, 96, ([Windows.Media.PixelFormats]::Pbgra32)
$rtb.Render($visual)
$enc = New-Object Windows.Media.Imaging.PngBitmapEncoder
$enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($rtb))
$fs = [IO.File]::Create($outPath)
$enc.Save($fs)
$fs.Close()

Write-Output "rendered $($keys.Count) icons -> $outPath ($([int]$W)x$([int]$H))"

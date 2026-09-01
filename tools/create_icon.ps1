# Generates the mod's toolbar icon: a bulldozer seen in profile, white on transparent.
#
# Style follows the sibling mods (VesselBookmark's bookmark, ControlFromHere's joystick): a single
# flat white silhouette, no disc, no second colour, no outline. KSP tints the toolbar button itself,
# so any colour of ours would fight it.
#
# Why the WHOLE MACHINE and not just the blade: a blade alone in profile is a bare stroke, and a
# stroke of that weight reads as a bent limb whatever its thickness - tried twice, as a ribbon
# between two offset Beziers and as one thick curve, and both came out looking like a leg. That is
# what had pushed the previous icon to a FRONT view of the blade. Drawing the tractor as well settles
# the ambiguity the other way round: the track carries the meaning, and the upright beside it can
# then only be a blade.
#
# The track is drawn HOLLOW - outer outline minus inner outline, in one even-odd path so the hole
# antialiases in the same pass. At this size a solid track is a brick; the ~3 px ring is what says
# "chenille".
#
# Nothing here is a scaled-down version of a bigger drawing: every feature is sized so that it still
# measures at least 2 px after the reduction. Multiply any fraction below by 38 x $fillFraction to
# check - the exhaust stack is the one that is right on the limit.
#
# Why it is drawn 8x and reduced: GDI+ has no analytic antialiasing worth the name on filled paths.
# The reduction also blends colour with alpha, which fringes a white-on-transparent shape grey, so
# the RGB is forced back to pure white afterwards and only the alpha is kept from the resampling.
#
# Run from anywhere: powershell -ExecutionPolicy Bypass -File tools\create_icon.ps1

Add-Type -AssemblyName System.Drawing

# Output size (px). 38 is the ApplicationLauncher's native button size; the siblings ship 32 and KSP
# rescales them, but there is no reason to hand it a resample it does not need.
$size = 38

# Supersampling factor used before the reduction.
$scale = 8

# Geometry, in fractions of the canvas: x to the right, y DOWNWARDS (GDI+ convention). The machine
# faces LEFT, so the blade is the leftmost thing and the cab the rightmost.
#
# Every number here is a design choice, not a measurement of the .mu models: this is a pictogram, and
# what matters is that it stays readable once reduced to 38 px.

# --- The blade: a quadratic Bezier stroked at constant thickness.
#
# The tip is the LOWEST and most FORWARD point and the top leans back, which is what makes the curve
# read as a blade rather than as a hook: the control point sits to the RIGHT of the chord, so the
# hollow - the working face - opens down-left, towards the ground the machine is about to take.
#
# It is deliberately the heaviest stroke of the drawing. It is what the mod is about, and it is also
# the part that loses most from the reduction: everything else here is straight edges, which survive
# it, while a curve turns to porridge as soon as it is thin.
#
# It is also kept close to VERTICAL. Leaning it back the way a real moldboard leans turned it into a
# diagonal stick - a prop under the hood, not a blade - because at this size the eye reads the slant
# long before it reads the curvature.
$bladeTop       = @(0.130, 0.430)
$bladeControl   = @(0.168, 0.645)
$bladeTip       = @(0.115, 0.850)
$bladeThickness = 0.090

# The cutting edge, jutting FORWARD from the foot of the blade. This is what stops the blade reading
# as a bare bar: a vertical stroke is a post, a vertical stroke with a lip at its foot is a tool.
$edge = @(0.020, 0.792, 0.168, 0.870)          # left, top, right, bottom

# The push arm, the one link back to the tractor. There used to be a tilt brace above it as well;
# it closed a triangle with the upper half of the blade, and at 38 px that triangle filled in and
# turned the blade into an "A". One bar, low, where a real dozer carries it.
$armFrom   = @(0.175, 0.690)
$armTo     = @(0.340, 0.672)
$linkThickness = 0.062

# --- The track, as an outer outline and the hole inside it. Front (left) idler raised on a slope,
# rear sprocket taller and rounder: that asymmetry is most of what separates a dozer from a wagon.
$trackOuter = @(
    @(0.345, 0.565), @(0.825, 0.565), @(0.940, 0.685), @(0.940, 0.790),
    @(0.865, 0.870), @(0.345, 0.870), @(0.255, 0.775), @(0.255, 0.685)
)
# The hole is inset by ~0.075 - a shade under 3 px of ring. Any thinner and the track stops looking
# like a track and starts looking like wire.
$trackInner = @(
    @(0.375, 0.650), @(0.790, 0.650), @(0.862, 0.722), @(0.862, 0.775),
    @(0.828, 0.795), @(0.375, 0.795), @(0.335, 0.750), @(0.335, 0.708)
)

# --- The tractor itself: hood in front, cab behind, both overlapping the top of the track so that
# the whole machine is one connected silhouette.
$hood = @(0.400, 0.435, 0.630, 0.585)          # left, top, right, bottom
$cab  = @(@(0.660, 0.300), @(0.885, 0.300), @(0.885, 0.585), @(0.620, 0.585))

# The cab roof, overhanging on both sides - the one detail that keeps the cab from reading as a
# plain box.
$roof = @(0.600, 0.258, 0.910, 0.305)

# The exhaust stack. The smallest feature of the drawing, and the first thing to drop if the icon
# ever has to be simplified further.
$stack = @(0.470, 0.305, 0.525, 0.445)

# --- Framing. Applied as a transform rather than folded into the numbers above, so that every
# coordinate stays a position on the MACHINE - the ground line stays 0.870, the cab roof 0.258 - and
# the framing stays two numbers to turn.
#
# A machine in profile is wide and flat, so it is the WIDTH that fills the square: drawn as written
# the silhouette sits on the bottom edge with a band of nothing above it, hence the lift. The scale
# then pulls it back off the left and right edges - it was reaching them within a pixel, where the
# sibling icons keep a clear margin all round.
$verticalShift = -0.062
$fillFraction  = 0.93

# Resolve the output relative to this script, independent of the CWD.
$iconPath = Join-Path $PSScriptRoot '..\GameData\BulldozerMod\icon.png'
$iconPath = [System.IO.Path]::GetFullPath($iconPath)

$work = $size * $scale

# Turns a fraction-of-canvas pair into a PointF of the supersampled bitmap.
function New-Point($xy) {
    return New-Object System.Drawing.PointF(([float]($xy[0] * $work)), ([float]($xy[1] * $work)))
}

# Turns a list of fraction pairs into the PointF[] the GDI+ polygon calls want.
function New-Polygon($points) {
    return [System.Drawing.PointF[]]@($points | ForEach-Object { New-Point $_ })
}

# Fills a @(left, top, right, bottom) box given in fractions of the canvas.
function Add-Box($graphics, $brush, $box) {
    $graphics.FillRectangle(
        $brush,
        [float]($box[0] * $work),
        [float]($box[1] * $work),
        [float](($box[2] - $box[0]) * $work),
        [float](($box[3] - $box[1]) * $work))
}

$bitmap = New-Object System.Drawing.Bitmap($work, $work, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.Clear([System.Drawing.Color]::Transparent)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
# Scale about the centre of the canvas, then lift. Prepend being the default order, the last call
# made is the first one applied to a point.
$graphics.TranslateTransform([float](0.5 * $work), [float]((0.5 + $verticalShift) * $work))
$graphics.ScaleTransform([float]$fillFraction, [float]$fillFraction)
$graphics.TranslateTransform([float](-0.5 * $work), [float](-0.5 * $work))
$brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)

# --- The track: outer outline and hole in ONE even-odd path.
#
# Drawing the hole as a second pass would need SourceCopy, which does not antialias against what is
# already there and leaves the ring edges ragged. Alternate fill gets it right in a single fill.
$trackPath = New-Object System.Drawing.Drawing2D.GraphicsPath
$trackPath.FillMode = [System.Drawing.Drawing2D.FillMode]::Alternate
$trackPath.AddPolygon((New-Polygon $trackOuter))
$trackPath.AddPolygon((New-Polygon $trackInner))
$graphics.FillPath($brush, $trackPath)
$trackPath.Dispose()

# --- Hood, cab, roof, stack, cutting edge.
Add-Box $graphics $brush $hood
$graphics.FillPolygon($brush, (New-Polygon $cab))
Add-Box $graphics $brush $roof
Add-Box $graphics $brush $stack
Add-Box $graphics $brush $edge

# --- The blade and the push arm, stroked rather than filled: a pen gets the constant thickness and
# the joins for free, where offsetting a curve by hand does not.
$pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([float]($bladeThickness * $work))
$pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Flat
$pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Flat

# The cubic Bezier that has the same shape as the quadratic described above: both controls sit two
# thirds of the way from their end towards the quadratic's single control point.
$c1 = @(
    ($bladeTop[0] + 2.0 / 3.0 * ($bladeControl[0] - $bladeTop[0])),
    ($bladeTop[1] + 2.0 / 3.0 * ($bladeControl[1] - $bladeTop[1])))
$c2 = @(
    ($bladeTip[0] + 2.0 / 3.0 * ($bladeControl[0] - $bladeTip[0])),
    ($bladeTip[1] + 2.0 / 3.0 * ($bladeControl[1] - $bladeTip[1])))
$graphics.DrawBezier($pen, (New-Point $bladeTop), (New-Point $c1), (New-Point $c2), (New-Point $bladeTip))
$pen.Dispose()

$linkPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([float]($linkThickness * $work))
$linkPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Flat
$linkPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Flat
$graphics.DrawLine($linkPen, (New-Point $armFrom), (New-Point $armTo))
$linkPen.Dispose()

$brush.Dispose()
$graphics.Dispose()

# --- Reduce to the output size.
$icon = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$iconGraphics = [System.Drawing.Graphics]::FromImage($icon)
$iconGraphics.Clear([System.Drawing.Color]::Transparent)
$iconGraphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$iconGraphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$iconGraphics.DrawImage($bitmap, (New-Object System.Drawing.Rectangle(0, 0, $size, $size)))
$iconGraphics.Dispose()
$bitmap.Dispose()

# --- Force every pixel to pure white and keep only the resampled alpha.
#
# The reduction interpolates the colour channels along with alpha, so a pixel on the edge comes out
# as white-blended-with-transparent-black, i.e. grey. Unnoticeable over a dark toolbar, visible as a
# dirty fringe anywhere else - and it also makes the icon unusable as a tint source.
$rect = New-Object System.Drawing.Rectangle(0, 0, $size, $size)
$data = $icon.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadWrite, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
try {
    $bytes = New-Object byte[] ($data.Stride * $size)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
    for ($y = 0; $y -lt $size; $y++) {
        $row = $y * $data.Stride
        for ($x = 0; $x -lt $size; $x++) {
            # BGRA: leave the 4th byte (alpha) alone.
            $i = $row + $x * 4
            $bytes[$i] = 255
            $bytes[$i + 1] = 255
            $bytes[$i + 2] = 255
        }
    }
    [System.Runtime.InteropServices.Marshal]::Copy($bytes, 0, $data.Scan0, $bytes.Length)
} finally {
    $icon.UnlockBits($data)
}

New-Item -ItemType Directory -Force -Path (Split-Path $iconPath) | Out-Null
$icon.Save($iconPath, [System.Drawing.Imaging.ImageFormat]::Png)
$icon.Dispose()

Write-Host "create_icon: wrote $iconPath ($size x $size)"

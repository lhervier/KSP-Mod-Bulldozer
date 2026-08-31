# Generates the mod's toolbar icon: a bulldozer blade in profile, white on transparent.
#
# Style follows the sibling mods (VesselBookmark's bookmark, ControlFromHere's joystick): a single
# flat white silhouette, no disc, no second colour, no outline. KSP tints the toolbar button itself,
# so any colour of ours would fight it.
#
# Why the shape is a FRONT view: at the ~38 px the app launcher shows, a blade in PROFILE is a bare
# curve, and a curve of that weight reads as a bent limb whatever the thickness - tried twice, as a
# ribbon between two offset Beziers and as one thick stroke, and both came out looking like a leg.
# Seen from the front it is straight panels: the ribs and the cutting edge carry the meaning, and
# straight edges also survive the reduction cleanly.
#
# The blade floats just above the ground rather than resting on it, which is how the mod wants it
# carried - on a robotic hinge, lowered to work.
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

# Geometry, in fractions of the canvas: x to the right, y DOWNWARDS (GDI+ convention).
#
# Every number here is a design choice, not a measurement of the .mu models: this is a pictogram, and
# what matters is that it stays readable once reduced to 38 px.

# The blade face, a trapezoid NARROWER AT THE TOP: that is what says the plate is tilted back, its
# top edge being the far one. The other way round the panels splay outwards and the whole thing reads
# as a crown.
$faceTop      = 0.17
$faceBottom   = 0.57
$faceTopLeft  = 0.17
$faceTopRight = 0.83
$faceBotLeft  = 0.07
$faceBotRight = 0.93

# The face is cut into panels by vertical ribs, but only BELOW the top rail: ribs running the full
# height cut the face into separate teeth, and the object stops being one blade.
$railBottom = 0.28
$panelCount = 3
$panelGap   = 0.014

# The cutting edge, a bar under the face and slightly wider than it - that overhang is what makes it
# read as a lip rather than as one more panel.
$edgeTop    = 0.57
$edgeBottom = 0.68
$edgeLeft   = 0.05
$edgeRight  = 0.95

# The ground the blade works, kept clear of the cutting edge: the blade is carried above it, which is
# how the mod wants it used. Full width, so it reads as ground rather than as one more bar of the
# machine.
$groundLeft   = 0.00
$groundRight  = 1.00
$groundTop    = 0.78
$groundBottom = 0.91

# Resolve the output relative to this script, independent of the CWD.
$iconPath = Join-Path $PSScriptRoot '..\GameData\BulldozerMod\icon.png'
$iconPath = [System.IO.Path]::GetFullPath($iconPath)

$work = $size * $scale

# Turns a fraction-of-canvas pair into a PointF of the supersampled bitmap.
function New-Point($xy) {
    return New-Object System.Drawing.PointF(([float]($xy[0] * $work)), ([float]($xy[1] * $work)))
}

$bitmap = New-Object System.Drawing.Bitmap($work, $work, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.Clear([System.Drawing.Color]::Transparent)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)

# Where the fraction u across the face lands, at a given height of the trapezoid.
function Get-FaceX($u, $y) {
    $t = ($y - $faceTop) / ($faceBottom - $faceTop)
    $left = $faceTopLeft + ($faceBotLeft - $faceTopLeft) * $t
    $right = $faceTopRight + ($faceBotRight - $faceTopRight) * $t
    return $left + ($right - $left) * $u
}

# --- The top rail: the full width of the face, uncut.
$graphics.FillPolygon($brush, @(
    (New-Point @((Get-FaceX 0.0 $faceTop), $faceTop)),
    (New-Point @((Get-FaceX 1.0 $faceTop), $faceTop)),
    (New-Point @((Get-FaceX 1.0 $railBottom), $railBottom)),
    (New-Point @((Get-FaceX 0.0 $railBottom), $railBottom))
))

# --- The ribbed part of the face, one polygon per panel. Drawn as separate panels rather than as one
# trapezoid with the ribs punched out of it: punching holes needs a second pass in SourceCopy, which
# does not antialias against what is already there and leaves the rib edges ragged.
for ($i = 0; $i -lt $panelCount; $i++) {
    $u0 = $i / $panelCount
    $u1 = ($i + 1) / $panelCount
    if ($i -gt 0) { $u0 += $panelGap }
    if ($i -lt $panelCount - 1) { $u1 -= $panelGap }

    $graphics.FillPolygon($brush, @(
        (New-Point @((Get-FaceX $u0 $railBottom), $railBottom)),
        (New-Point @((Get-FaceX $u1 $railBottom), $railBottom)),
        (New-Point @((Get-FaceX $u1 $faceBottom), $faceBottom)),
        (New-Point @((Get-FaceX $u0 $faceBottom), $faceBottom))
    ))
}

# --- The cutting edge.
$graphics.FillRectangle(
    $brush,
    [float]($edgeLeft * $work),
    [float]($edgeTop * $work),
    [float](($edgeRight - $edgeLeft) * $work),
    [float](($edgeBottom - $edgeTop) * $work))

# --- The ground.
$graphics.FillRectangle(
    $brush,
    [float]($groundLeft * $work),
    [float]($groundTop * $work),
    [float](($groundRight - $groundLeft) * $work),
    [float](($groundBottom - $groundTop) * $work))

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

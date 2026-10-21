# vim:ts=4:et
"""
Generates the three bulldozer blade models (BD-2 / BD-3 / BD-5) and exports them to .mu.

    blender --background --python tools/make_blades.py -- --out GameData/KSPModBulldozer/Parts

Without --out the blades are only built in the current scene, side by side, so the shapes can be
looked at in the viewport before anything is written. Needs the io_object_mu addon enabled.

Everything below is derived from the blade WIDTH, the one number that separates the three models.


AXIS CONVENTION -- the one thing to get right
---------------------------------------------
io_object_mu maps Blender (x, y, z) onto Unity (x, z, y): it swaps Y and Z and reverses the
rotation direction (write_vector / write_quaternion in its mu.py). So this script authors in:

    Blender +X = blade width, to the right     -> Unity +X
    Blender +Y = the way the blade FACES       -> Unity +Z
    Blender +Z = up                            -> Unity +Y

Which is why 'bladeFace' below is an empty with NO rotation at all: an unrotated Blender object
exports with an identity Unity rotation, so its Unity forward (+Z) is Blender +Y -- the direction
the blade faces. That is exactly what BulldozerCleanModule.bladeTransform reads (face.forward).


WHY THE BLADE NEVER DIPS BELOW THE WHEELS
-----------------------------------------
Vessel.getLowestPoint() re-enables every collider for its measurement, so a blade hanging below the
wheel contact plane makes the whole vessel sit higher when the game reloads it.

That is settled here by construction rather than left to the player: the surface attach node sits at
(0, 0, 0), which is the CUTTING EDGE level, and no part of the model goes below z = 0. The node is
the point of the part that lands on whatever the player clicked. So the blade can never reach below
the click point -- and since the click point is on the rover's body, necessarily above the wheel
contact plane, the blade can never become the lowest point.

EDGE_DROP trades that guarantee away for looks: raise it and the cutting edge sinks that many metres
below the attach point, which then has to be checked against the actual rover.


WHY THERE ARE TWO STACK NODES
-----------------------------
A blade is carried on a robotic hinge, and the stack node is how high up the blade that hinge grabs
it -- which is what decides how far the blade DANGLES below the hinge arm.

The bracket across the push arms sits in the bottom third, so a blade hung from it dangles only
0.36 / 0.47 / 0.68 m. A hinge mounted high enough to clear the ground cannot reach down that far
with its arm level: the arm has to be angled down, which tilts the blade with it, which then has to
be rotated back by the same angle -- leaving a visible kink between the two parts, and two rotations
to set up instead of none. Measured in game at 30 degrees on a BD-5.

So the bracket grows upwards into a back plate carrying a SECOND node, which roughly doubles the
dangle. The low node stays: it costs nothing, it is the one already on built vessels, and a blade
slung under a low beam still wants it.

The top node is as high as it can go. Both nodes sit at the centre of the plate that carries them,
and that plate may not stick out above the blade -- the box collider is fitted to the mouldboard.
"""

import bpy
import bmesh
import math
import os
import sys

# --------------------------------------------------------------------------------------------
# The three models. Width is the only per-model input; everything else is a function of it.
# --------------------------------------------------------------------------------------------
BLADES = (
    ("bulldozerBladeMk1", 2.0),   # BD-2 'Scout'
    ("bulldozerBladeMk2", 3.0),   # BD-3 'Worksite'
    ("bulldozerBladeMk3", 5.0),   # BD-5 'Groundbreaker'
)

EDGE_DROP = 0.0     # metres the cutting edge sits BELOW the attach point. See the module docstring.
PROFILE_STEPS = 15  # samples along the mouldboard profile, bottom to top
EDGE_STEPS = 2      # how many of those belong to the cutting lip (its own object and material)
UV_TILE = 1.0       # metres per texture repeat


# How wide the back plate is, as a fraction of the bracket it grows out of. Full width reads as a
# sheet bolted over the whole back of the blade -- rendered, it hid the mouldboard entirely.
BACK_PLATE_WIDTH = 0.45

# ...but the plate also has to COVER the hinge that plugs into it, or the hinge overhangs it on
# every side. A hinge is the same size whatever the blade, so this is the one dimension that does
# NOT scale with the width: it is a floor, applied on top of the fraction above.
#
# Measured off the stock G-11 (hinge_01.mu), reading its meshes with the local transforms composed
# and taking the section across its attach plane -- NOT the naive bounding box, which the part's
# offset inside the .mu makes meaningless. The G-00 is 0.26 x 0.21 m and fits well inside this; the
# alligator hinges are far larger and were never the target.
HINGE_FOOTPRINT = (0.782, 0.622)    # width x height, in metres
HINGE_MARGIN = 0.03                 # how much plate shows around it on each side

TEXTURE_FILE = "bulldozerBlade.png"
FACE_EMPTY = "bladeFace"        # what the .cfg must put in bladeTransform


# --------------------------------------------------------------------------------------------
# Liveries
# --------------------------------------------------------------------------------------------
# A blade carries exactly two materials, and a livery gives the KSP/Specular triplet -- _Color,
# _SpecColor, _Shininess -- of each. Their suffixes are what the part .cfg targets by name, so
# renaming one means regenerating the .mu.
MAT_BODY = "Steel"   # mouldboard, cheeks, push arms and mounting bracket
MAT_EDGE = "Edge"    # the cutting lip alone

# The FIRST livery is the one baked into the .mu. Every other one exists only as a
# ModulePartVariants override in the part .cfg -- which main() prints, ready to paste, so that the
# two descriptions cannot drift apart. Nothing checks them against each other at load time: a
# stale override shows up nowhere but in the editor, on the part itself.
#
# The theme names are the STOCK ones (see Squad/Parts/VariantThemes.cfg). That is deliberate: a
# theme KSP does not know logs an error from PartLoader, and reusing stock names lets the editor's
# vessel-wide theme switch reach the blades along with everything else.
#
# TUNING THE SHEEN -- the two knobs do different jobs, and only one of them is "how shiny":
#   _SpecColor  HOW STRONG the highlight is. This is the one to move. Raise it towards white and
#               the surface turns into a mirror; drop it towards the base tint and the sheen fades.
#   _Shininess  HOW TIGHT it is (Blinn-Phong exponent, not an amount). High values concentrate the
#               highlight into a small hard glint -- which reads as chrome; middling values spread
#               it into the soft band that reads as brushed metal.
# A first pass at 0.92 / 0.82 came out a full mirror in game (2026-09-01). Both were cut back; the
# base tint was left alone, since that is the part that was actually wanted brighter.
#
# ⚠️ There is no gloss map to fall back on: the shared texture is written without an alpha channel,
# so the shader's gloss term sits at 1.0 everywhere and the whole strength of the highlight rides on
# _SpecColor. Adding an alpha would give a third knob -- but a shared one, since both liveries
# sample the same texture.
LIVERIES = (
    ("Steel", "Gray", "#LOC_KSPModBulldozer_bladeVariantSteel", {
        #            _Color              _SpecColor          _Shininess
        MAT_BODY: ((0.52, 0.54, 0.56), (0.35, 0.36, 0.38), 0.30),
        MAT_EDGE: ((0.20, 0.21, 0.23), (0.35, 0.36, 0.38), 0.55),
    }),
    # Polished stainless: a lighter base tint than raw steel, plus a sheen that stays a sheen. The
    # lip keeps a slightly tighter highlight than the body -- a cutting edge gets burnished by the
    # work -- and stays noticeably darker, as it does on the raw livery, so the blade keeps its
    # shape at a distance instead of turning into one flat sheet.
    ("Stainless", "Silver", "#LOC_KSPModBulldozer_bladeVariantStainless", {
        MAT_BODY: ((0.86, 0.88, 0.90), (0.50, 0.52, 0.55), 0.50),
        MAT_EDGE: ((0.48, 0.50, 0.53), (0.46, 0.48, 0.51), 0.60),
    }),
)


def to_hex(colour):
    """The #rrggbb form of a colour, as ColorUtility.TryParseHtmlString reads it in a .cfg."""
    return "#" + "".join("%02x" % max(0, min(255, int(round(c * 255.0)))) for c in colour)


def finishes_for(livery_name=None):
    """The two finishes of the named livery, defaulting to the one baked into the .mu."""
    if livery_name is None:
        return LIVERIES[0][3]
    for name, _theme, _label, finishes in LIVERIES:
        if name == livery_name:
            return finishes
    raise SystemExit("unknown livery %r, expected one of %s"
                     % (livery_name, [entry[0] for entry in LIVERIES]))


# --------------------------------------------------------------------------------------------
# Dimensions
# --------------------------------------------------------------------------------------------
def dimensions(width):
    """Every dimension of a blade of the given width, in metres, as a dict."""
    h = 0.30 * width + 0.30                 # mouldboard height: 0.90 / 1.20 / 1.80
    t = 0.030 + 0.008 * width               # mouldboard plate thickness
    return {
        "width": width,
        "h": h,
        "t": t,
        "t_edge": t * 0.35,                 # thickness at the very lip
        "tp": 0.030 + 0.010 * width,        # end plate / bracket thickness
        "side": 0.22 * h,                   # how far the end plates stand proud of the face
        "arm": 0.28 + 0.055 * width,        # attach plane -> back of the mouldboard
        "aw": 0.10 + 0.030 * width,         # push arm cross-section
        "arm_z": 0.30 * h,                  # push arm underside
    }


def profile(dim):
    """The mouldboard's side profile, bottom to top, as a list of (front, back) (y, z) point pairs.

    Front is the concave working face, back the outside of the plate.
    """
    h = dim["h"]

    # A quadratic Bezier from the cutting edge up to the top edge, with the control point pulled
    # back to y = 0 so the plate bellies backwards between them: that is what makes the working
    # face concave and lets spoil roll forward instead of piling against a flat slab.
    p0 = (0.42 * h, 0.0)        # cutting edge, the furthest forward point at the bottom
    p1 = (0.00, 0.45 * h)       # control
    p2 = (0.26 * h, h)          # top edge

    front = []
    for i in range(PROFILE_STEPS):
        t = i / (PROFILE_STEPS - 1)
        u = 1.0 - t
        front.append((u * u * p0[0] + 2 * u * t * p1[0] + t * t * p2[0],
                      u * u * p0[1] + 2 * u * t * p1[1] + t * t * p2[1]))

    pairs = []
    for i, (fy, fz) in enumerate(front):
        # Backward normal, taken from the tangent on the neighbours. Rotating the tangent by -90
        # degrees in the Y/Z plane is what points away from the concave side.
        a = front[max(i - 1, 0)]
        b = front[min(i + 1, PROFILE_STEPS - 1)]
        ty, tz = b[0] - a[0], b[1] - a[1]
        length = math.hypot(ty, tz) or 1.0
        ny, nz = -tz / length, ty / length

        # The lip is thin and thickens over the first few samples: a blade as thick at its edge as
        # in its middle reads as a slab, not as something meant to cut.
        ramp = min(1.0, i / max(EDGE_STEPS, 1))
        th = dim["t_edge"] + (dim["t"] - dim["t_edge"]) * ramp

        pairs.append(((fy, fz), (fy + ny * th, fz + nz * th)))
    return pairs


# --------------------------------------------------------------------------------------------
# Mesh helpers
# --------------------------------------------------------------------------------------------
def _finish(name, verts, faces, parent, material):
    """Create an object from raw geometry, with outward normals, box-projected UVs and a material."""
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata(verts, [], faces)
    mesh.validate()

    # Recalculated rather than wound by hand: the sweep below emits six families of faces, and
    # getting every one of them consistent by inspection is exactly the kind of thing that quietly
    # ships one inside-out face.
    bm = bmesh.new()
    bm.from_mesh(mesh)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    bm.to_mesh(mesh)
    bm.free()
    mesh.update()

    # Box projection: pick, per face, the two axes the face is most parallel to. Projecting on a
    # single fixed plane instead would give every perpendicular face degenerate UVs.
    uv = mesh.uv_layers.new(name="UVMap")
    axes = {0: (1, 2), 1: (0, 2), 2: (0, 1)}
    for poly in mesh.polygons:
        n = poly.normal
        a, b = axes[max(range(3), key=lambda i: abs(n[i]))]
        for li in poly.loop_indices:
            co = mesh.vertices[mesh.loops[li].vertex_index].co
            uv.data[li].uv = (co[a] / UV_TILE, co[b] / UV_TILE)
        poly.use_smooth = True

    # Smooth the swept skin but keep the edges crisp. use_auto_smooth is a 2.8x-3.x API, removed in
    # Blender 4.1, so there the model just comes out flat-shaded rather than failing.
    if hasattr(mesh, "use_auto_smooth"):
        mesh.use_auto_smooth = True
        mesh.auto_smooth_angle = math.radians(40.0)

    obj = bpy.data.objects.new(name, mesh)
    obj.data.materials.append(material)
    bpy.context.collection.objects.link(obj)
    obj.parent = parent
    return obj


def sweep(name, section, x0, x1, parent, material):
    """Build a closed solid by sweeping a (front, back) Y/Z section along X from x0 to x1."""
    n = len(section)
    verts = []
    for x in (x0, x1):
        for (fy, fz), (by, bz) in section:
            verts.append((x, fy, fz))
            verts.append((x, by, bz))

    def idx(side, i, back):
        return side * 2 * n + i * 2 + (1 if back else 0)

    faces = []
    for i in range(n - 1):
        faces.append((idx(0, i, False), idx(0, i + 1, False), idx(1, i + 1, False), idx(1, i, False)))
        faces.append((idx(0, i, True), idx(0, i + 1, True), idx(1, i + 1, True), idx(1, i, True)))
        faces.append((idx(0, i, False), idx(0, i, True), idx(0, i + 1, True), idx(0, i + 1, False)))
        faces.append((idx(1, i, False), idx(1, i, True), idx(1, i + 1, True), idx(1, i + 1, False)))
    faces.append((idx(0, 0, False), idx(0, 0, True), idx(1, 0, True), idx(1, 0, False)))
    faces.append((idx(0, n - 1, False), idx(0, n - 1, True), idx(1, n - 1, True), idx(1, n - 1, False)))
    return _finish(name, verts, faces, parent, material)


def box(name, x0, x1, y0, y1, z0, z1, parent, material):
    """Build an axis-aligned box spanning the given ranges."""
    verts = [(x, y, z) for x in (x0, x1) for y in (y0, y1) for z in (z0, z1)]
    faces = [(0, 1, 3, 2), (4, 5, 7, 6), (0, 1, 5, 4),
             (2, 3, 7, 6), (0, 2, 6, 4), (1, 3, 7, 5)]
    return _finish(name, verts, faces, parent, material)


def wedge(name, x0, x1, y_back, y_front, z0, z1, parent, material):
    """Build a box whose front face is tilted, given its y at z0 and at z1."""
    verts = []
    for x in (x0, x1):
        for z, y in ((z0, y_front[0]), (z1, y_front[1])):
            verts.append((x, y_back, z))
            verts.append((x, y, z))
    faces = [(0, 1, 3, 2), (4, 5, 7, 6), (0, 1, 5, 4),
             (2, 3, 7, 6), (0, 2, 6, 4), (1, 3, 7, 5)]
    return _finish(name, verts, faces, parent, material)


def height_of(polyline, z):
    """The y of a (y, z) polyline at height z, or None when z falls outside it."""
    for i in range(len(polyline) - 1):
        (y0, za), (y1, zb) = polyline[i], polyline[i + 1]
        if min(za, zb) - 1e-9 <= z <= max(za, zb) + 1e-9:
            return y0 if za == zb else y0 + (z - za) / (zb - za) * (y1 - y0)
    return None


def arm_front(section, z0, z1, thickness):
    """Where an arm spanning z0..z1 should end, as its front y at z0 and at z1.

    Buried in the mouldboard everywhere along its height, and through it nowhere.
    """
    back = [b for _, b in section]
    front = [f for f, _ in section]

    # A flat-ended arm cannot do this: over the height of one arm the mouldboard's back recedes by
    # more than the plate is thick, so any single y is either proud of the skin at one end or short
    # of it at the other -- the two rectangles that showed through the working face in game.
    # Following the back surface fixes both ends at once.
    ends = [height_of(back, z0) + 0.45 * thickness, height_of(back, z1) + 0.45 * thickness]

    # The chord runs ahead of the curve between its ends, so the deepest bite is in the middle, not
    # at the ends. Measured against the front skin and backed off if it would break out -- the
    # margin depends on the curvature and the arm height, both of which move with the width.
    worst = 0.0
    steps = 16
    for i in range(steps + 1):
        z = z0 + (z1 - z0) * i / steps
        skin = height_of(front, z)
        if skin is not None:
            worst = max(worst, ends[0] + (ends[1] - ends[0]) * i / steps - skin)
    if worst > 0.0:
        clearance = worst + 0.15 * thickness
        ends = [ends[0] - clearance, ends[1] - clearance]
    return ends


# --------------------------------------------------------------------------------------------
# Material and texture
# --------------------------------------------------------------------------------------------
def srgb_to_linear(c):
    """Convert an sRGB channel value to the linear value Blender stores in image pixels."""
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def ksp_material(name, finish):
    """Create a KSP/Specular material carrying the given (colour, spec, shininess) finish."""
    colour, spec, shininess = finish
    mat = bpy.data.materials.new(name)

    # Blender-side appearance only -- none of this is exported, the .mu carries mumatprop below.
    # Worth setting anyway so what the viewport shows matches what KSP will draw: without it every
    # material renders as the same default grey and the cutting edge is invisible in Blender.
    mat.diffuse_color = colour + (1.0,)
    mat.use_nodes = True
    principled = mat.node_tree.nodes.get("Principled BSDF")
    if principled is not None:
        principled.inputs["Base Color"].default_value = colour + (1.0,)
        principled.inputs["Roughness"].default_value = 1.0 - shininess
        # Wired so that turning the sheen down in LIVERIES visibly turns it down in a preview
        # render too. It shows the DIRECTION of a change, not the game's look: Blender's Specular
        # is a dielectric reflectance, KSP's _SpecColor a Blinn-Phong highlight colour.
        principled.inputs["Specular"].default_value = min(1.0, max(spec))

    mp = mat.mumatprop
    mp.shaderName = "KSP/Specular"
    for group in (mp.texture, mp.color, mp.vector, mp.float2, mp.float3):
        group.properties.clear()

    item = mp.texture.properties.add()
    item.name = "_MainTex"
    item.tex = TEXTURE_FILE
    item = mp.color.properties.add()
    item.name = "_Color"
    item.value = colour + (1.0,)
    item = mp.color.properties.add()
    item.name = "_SpecColor"
    item.value = spec + (1.0,)
    item = mp.float3.properties.add()
    item.name = "_Shininess"
    item.value = shininess
    return mat


def write_texture(path, size=64):
    """Write the flat steel texture the blades share, as a PNG at the given path."""
    img = bpy.data.images.new("bulldozerBlade", size, size, alpha=False)
    img.colorspace_settings.name = "sRGB"

    # Flat, with a faint grain: the tint that tells body from cutting edge lives in _Color, so the
    # texture only has to keep the shader from sampling nothing.
    base = srgb_to_linear(0.66)
    pixels = []
    for i in range(size * size):
        g = base * (0.96 + 0.08 * (((i * 1103515245 + 12345) >> 16) & 0xFF) / 255.0)
        pixels.extend((g, g, g, 1.0))
    img.pixels = pixels

    img.filepath_raw = path
    img.file_format = "PNG"
    img.save()
    return path


# --------------------------------------------------------------------------------------------
# Assembly
# --------------------------------------------------------------------------------------------
def build_blade(name, width, x_offset=0.0, livery_name=None):
    """Build one complete blade and return (root object, report dict).

    livery_name repaints the blade for a preview render; the export always leaves it None, since
    the .mu may only ever carry the base livery -- the others are .cfg overrides on top of it.
    """
    dim = dimensions(width)
    h, tp = dim["h"], dim["tp"]

    # The material names are what the .cfg overrides target by prefix, so they have to stay exactly
    # what LIVERIES targets: renaming one here means regenerating the .mu and editing three .cfg.
    finishes = finishes_for(livery_name)
    steel = ksp_material(name + MAT_BODY, finishes[MAT_BODY])
    edge = ksp_material(name + MAT_EDGE, finishes[MAT_EDGE])

    root = bpy.data.objects.new(name, None)
    root.empty_display_type = "PLAIN_AXES"
    root.empty_display_size = 0.5
    root.location = (x_offset, 0.0, 0.0)
    bpy.context.collection.objects.link(root)
    if hasattr(root, "muproperties"):
        root.muproperties.modelType = "PART"

    section = profile(dim)

    # Slide the profile forward so the BACK of the mouldboard lands exactly at y = arm, which is
    # what the push arms are cut to reach. The belly of the curve is not at the cutting edge's y, so
    # this offset is measured off the built profile rather than assumed.
    dy = dim["arm"] - min(back[0] for _, back in section)

    # And up, so the lowest point of the whole section sits exactly on z = 0 -- the one line the
    # model may not cross (see the module docstring). It is NOT the cutting edge that reaches
    # lowest: the plate's thickness is laid off along the profile normal, which at the raked lip
    # tilts downward and puts the underside of the lip a centimetre or two below the front curve.
    # Measured, because how far depends on the rake and on the lip thickness, both of which move
    # with the width.
    dz = -EDGE_DROP - min(min(front[1], back[1]) for front, back in section)
    section = [((fy + dy, fz + dz), (by + dy, bz + dz)) for (fy, fz), (by, bz) in section]

    # End plates: the profile pushed straight forward, so they cup the working face at each end
    # instead of being slabs bolted to the sides. They eat into the nominal width rather than adding
    # to it, so the part really is 2 / 3 / 5 m across.
    plate = [((fy + dim["side"], fz), (by, bz)) for (fy, fz), (by, bz) in section]

    # The mouldboard stops where the cheeks start, rather than running the full width underneath
    # them. Overlapping them z-fights, and visibly: the cheek's outer face and the mouldboard's end
    # cap end up in the same plane with the SAME normal, as do their two back skins, so the depth
    # test has no way to pick a winner and the pair flickers as the camera moves.
    #
    # Abutting solids never do this. Their coplanar faces point AWAY from each other, so whichever
    # one the camera can see, the other is back-facing and culled before the depth test. The same
    # reasoning is why Edge and Body may share a section index, and why the arms are buried inside
    # the bracket rather than flush with its back.
    half = width / 2.0
    inner = half - tp
    sweep(name + "Edge", section[:EDGE_STEPS + 1], -inner, inner, root, edge)
    sweep(name + "Body", section[EDGE_STEPS:], -inner, inner, root, steel)
    sweep(name + "EndL", plate, -half, -inner, root, edge)
    sweep(name + "EndR", plate, inner, half, root, edge)

    # Push arms and the bracket that spans them: what carries the blade back to the rover.
    arm_x = 0.27 * width
    aw, arm_z = dim["aw"], dim["arm_z"] + dz
    top = max(max(front[1], back[1]) for front, back in section)   # highest point of the mouldboard
    flange = 0.14 * h           # how far the bracket stands proud of the arms, above and below

    # The arms end on the mouldboard's back, following its curve, and start half way INTO the
    # bracket rather than flush with its back face -- flush would put the arm's rear cap in the
    # bracket's plane with the same normal, the z-fight described above.
    ends = arm_front(section, arm_z, arm_z + aw, dim["t"])
    wedge(name + "ArmL", -arm_x - aw / 2, -arm_x + aw / 2, tp / 2, ends, arm_z, arm_z + aw, root, steel)
    wedge(name + "ArmR", arm_x - aw / 2, arm_x + aw / 2, tp / 2, ends, arm_z, arm_z + aw, root, steel)
    # The bracket is also what a stack-attached beam butts against, so it is sized to give one
    # something to land on rather than just to tie the two arms together.
    bracket_x = arm_x + aw / 2 + 0.04
    bracket_top = arm_z + aw + flange
    box(name + "Bracket", -bracket_x, bracket_x, 0.0, tp,
        arm_z - flange, bracket_top, root, steel)

    # The back plate that carries the stack node (see the module docstring). It cannot be an open
    # frame: the bracket already reaches within 0.15 m of it on a BD-5.
    #
    # Butted onto the bracket rather than sunk into it: the two share the y = 0 and y = tp planes,
    # so any overlap would put two coplanar faces with the same normal against each other -- the
    # flicker described above. Abutting leaves them edge to edge, which never z-fights.
    #
    # Sized as the LARGER of what the blade suggests and what the hinge needs, so a big blade keeps
    # a plate in proportion while a small one is still big enough to sit behind the whole hinge.
    # On a BD-2 and a BD-3 the plate ends up above the top edge of the mouldboard, which is why the
    # collider below is fitted to the plate as well.
    if bracket_top >= top:
        raise SystemExit("%s: bracket already reaches the top edge, no room for a back plate" % name)
    node_z = (bracket_top + top) / 2.0
    plate_x = max(BACK_PLATE_WIDTH * bracket_x, HINGE_FOOTPRINT[0] / 2.0 + HINGE_MARGIN)
    plate_top = max(top, node_z + HINGE_FOOTPRINT[1] / 2.0 + HINGE_MARGIN)
    box(name + "BackPlate", -plate_x, plate_x, 0.0, tp, bracket_top, plate_top, root, steel)

    # The hinge is centred on the node, so its lower half reaches BELOW the plate on the smaller
    # blades -- where the bracket, which is wider still, is what stands behind it. Checked rather
    # than eyeballed: a hinge overhanging into thin air is exactly the defect this plate fixes.
    hinge_bottom = node_z - HINGE_FOOTPRINT[1] / 2.0 - HINGE_MARGIN
    if hinge_bottom < arm_z - flange or bracket_x < HINGE_FOOTPRINT[0] / 2.0 + HINGE_MARGIN:
        raise SystemExit("%s: the bracket does not cover the part of the hinge below the plate"
                         % name)

    # What the blade faces. No rotation: an unrotated object's exported Unity forward is Blender +Y.
    face = bpy.data.objects.new(FACE_EMPTY, None)
    face.empty_display_type = "ARROWS"
    face.empty_display_size = 0.4 * h
    face.location = (0.0, dim["arm"] + 0.5 * h, 0.5 * h)
    bpy.context.collection.objects.link(face)
    face.parent = root

    # One box collider over the lot. It stays at the origin with an identity matrix so io_object_mu
    # folds it onto the root object instead of adding a game object for it; the offset travels in
    # muproperties.center. It collides for real in flight: a blade left down ploughs the terrain and
    # pins the rover, which is what forces the player to carry it on a robotic hinge.
    y_max = max(max(f[0] for f, _ in plate), max(ends))
    z_min = -EDGE_DROP
    z_max = max(top, plate_top)     # the back plate can stand above the mouldboard
    collider = bpy.data.objects.new(name + "Collider", None)
    collider.empty_display_type = "CUBE"
    bpy.context.collection.objects.link(collider)
    collider.parent = root
    mp = collider.muproperties
    mp.collider = "MU_COL_BOX"
    mp.size = (width, y_max, z_max - z_min)
    mp.center = (0.0, y_max / 2.0, (z_min + z_max) / 2.0)

    return root, {
        "name": name,
        "width": width,
        "height": h,
        "depth": y_max,
        "plate": (2.0 * plate_x, plate_top - bracket_top),
        "top_stack_node_y": node_z,     # in Unity's up axis
        "unity_bounds": ((-half, half), (z_min, z_max), (0.0, y_max)),
    }


# --------------------------------------------------------------------------------------------
# Driver
# --------------------------------------------------------------------------------------------
def clear_scene():
    """Empty the current blend of objects, meshes, materials and images."""
    for collection in (bpy.data.objects, bpy.data.meshes, bpy.data.materials, bpy.data.images):
        for item in list(collection):
            collection.remove(item, do_unlink=True)


def cli_args():
    """Everything given after '--' on the Blender command line."""
    return sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []


def out_dir():
    """The directory given after '--out', or None when the script was run without one."""
    args = cli_args()
    return os.path.abspath(args[args.index("--out") + 1]) if "--out" in args else None


def variants_cfg(name, indent=""):
    """The lines of the ModulePartVariants node offering every livery but the baked-in one."""
    t = "\t"        # the part .cfg files are tab-indented; this block is meant to paste into one
    base_name, base_theme, base_label, base = LIVERIES[0]
    out = ["MODULE", "{",
           t + "name = ModulePartVariants",
           # Colour-only variants: same geometry, so one drag cube covers all of them.
           t + "useMultipleDragCubes = false",
           t + "baseName = %s" % base_name,
           t + "baseDisplayName = %s" % base_label,
           t + "baseThemeName = %s" % base_theme,
           t + "primaryColor = %s" % to_hex(base[MAT_BODY][0]),
           t + "secondaryColor = %s" % to_hex(base[MAT_EDGE][0])]

    for variant_name, theme, label, finishes in LIVERIES[1:]:
        out += ["", t + "VARIANT", t + "{",
                t * 2 + "name = %s" % variant_name,
                t * 2 + "displayName = %s" % label,
                t * 2 + "themeName = %s" % theme,
                t * 2 + "primaryColor = %s" % to_hex(finishes[MAT_BODY][0]),
                t * 2 + "secondaryColor = %s" % to_hex(finishes[MAT_EDGE][0])]
        # One TEXTURE node per material: without materialName the override would hit both, and the
        # cutting lip would lose the darker tint that tells it apart from the mouldboard.
        for suffix in (MAT_BODY, MAT_EDGE):
            colour, spec, shininess = finishes[suffix]
            out += ["", t * 2 + "TEXTURE", t * 2 + "{",
                    t * 3 + "materialName = %s%s" % (name, suffix),
                    t * 3 + "_Color = %s" % to_hex(colour),
                    t * 3 + "_SpecColor = %s" % to_hex(spec),
                    t * 3 + "_Shininess = %.2f" % shininess,
                    t * 2 + "}"]
        out += [t + "}"]
    out += ["}"]
    return [indent + line if line else line for line in out]


def main():
    if not hasattr(bpy.ops.export_object, "ksp_mu"):
        raise SystemExit("io_object_mu is not enabled in this Blender")

    target = out_dir()
    clear_scene()

    if target:
        os.makedirs(target, exist_ok=True)
        write_texture(os.path.join(target, TEXTURE_FILE))

    reports = []
    x = 0.0
    for name, width in BLADES:
        # Only spread the blades out when nothing is being exported: a non-zero root location would
        # travel into the .mu and offset the part.
        root, report = build_blade(name, width, 0.0 if target else x)
        x += width + 1.0
        reports.append(report)

        if target:
            bpy.context.view_layer.objects.active = root
            root.select_set(True)
            path = os.path.join(target, name + ".mu")
            bpy.ops.export_object.ksp_mu(filepath=path)
            root.select_set(False)
            report["path"] = path

    print("\n" + "=" * 78)
    for r in reports:
        xs, ys, zs = r["unity_bounds"]
        print("%s  %.1f m wide" % (r["name"], r["width"]))
        print("    Unity bounds  x %.3f..%.3f   y %.3f..%.3f (up)   z %.3f..%.3f (faces)"
              % (xs[0], xs[1], ys[0], ys[1], zs[0], zs[1]))
        print("    cfg lines:")
        print("        MODEL { model = KSPModBulldozer/Parts/%s }" % r["name"])
        print("        node_attach = 0.0, 0.0, 0.0, 0.0, 0.0, 1.0, 1")
        print("        node_stack_backTop = 0.0, %.4f, 0.0, 0.0, 0.0, -1.0, 1"
              % r["top_stack_node_y"])
        print("        attachRules = 1,1,0,0,1")
        print("        bladeTransform = %s   (inside the MODULE node)" % FACE_EMPTY)
        print("        (dangles %.2f m below the node; back plate %.2f x %.2f m, backing a"
              " %.2f x %.2f hinge together with the bracket below it)"
              % (r["top_stack_node_y"], r["plate"][0], r["plate"][1],
                 HINGE_FOOTPRINT[0], HINGE_FOOTPRINT[1]))
        for line in variants_cfg(r["name"], indent="        "):
            print(line)
        if "path" in r:
            print("    wrote %s" % r["path"])
    print("=" * 78)


# Guarded so render_blades.py can import this module for its geometry without triggering an export.
if __name__ == "__main__":
    main()

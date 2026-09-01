# vim:ts=4:et
"""
Renders the three bulldozer blades to PNG, for looking at the shapes without opening Blender.

    blender --background --python tools/render_blades.py
    blender --background --python tools/render_blades.py -- --out somewhere --width 1400
    blender --background --python tools/render_blades.py -- --livery Stainless
    blender --background --python tools/render_blades.py -- --blade Mk3

Writes one image per view and per livery into tools/renders by default. It builds the geometry AND
takes the liveries by importing make_blades.py, so what it shows is always what the generator
currently produces -- there is no second copy of the shape or of the paint here to fall out of step
with the first.

Nothing about these images is exported to KSP. They are documentation, and a way to close the loop
when tuning the shape constants in make_blades.py: change a number, re-run this, look.
"""

import bpy
import importlib.util
import math
import os
import sys

from mathutils import Vector

HERE = os.path.dirname(os.path.abspath(__file__))
GAP = 0.8           # metres of clear ground between two blades in the line-up
MARGIN = 1.10       # how much wider than the geometry the frame is
SENSOR = 36.0       # Blender's default sensor width, in mm

# A stand-in for the G-11 hinge, at its real size, plus the level arm behind it and the ground the
# blade is standing on. Together they answer the two questions the back plate exists for: with the
# arm horizontal, does the cutting edge reach the ground, and is the hinge fully backed by plate
# and bracket rather than overhanging into air? None of it is exported -- it is drawn straight into
# the preview scene, and it takes its section from the generator so the two cannot drift apart.
HINGE_DEPTH = 0.625     # how far the G-11 stands off what it is bolted to, in metres
ARM_LENGTH = 1.0        # the level arm drawn behind it
ARM_SECTION = 0.22
HINGE_TINT = ((0.42, 0.22, 0.06), (0.20, 0.20, 0.20), 0.20)
ARM_TINT = ((0.28, 0.15, 0.04), (0.20, 0.20, 0.20), 0.20)
GROUND_TINT = ((0.16, 0.15, 0.14), (0.05, 0.05, 0.05), 0.05)

# Where each camera stands, in the same metres as the models. Only the position is given: where it
# points and how long a lens it takes are FITTED to the geometry (see aim_and_fit), so these views
# survive a change to the shape constants in make_blades.py instead of quietly going off-frame --
# which is the whole reason this script exists.
VIEWS = {
    "front": (4.0, 15.5, 6.0),      # the concave working face
    # Far back rather than close: the line-up recedes almost straight away from here, and from close
    # up the near blade comes out three times the size of the far one, with its profile bent by the
    # wide angle -- which is the one thing this view exists to show. The fit picks the long lens.
    "side": (30.0, 12.0, 6.5),      # the profile: curvature of the mouldboard and the cheek
    "back": (7.5, -15.0, 6.5),      # push arms and mounting bracket
}

# Straight-on profile, on the X axis with no elevation: the view that shows how high up the blade
# the stack node sits and whether the mock hinge arm leaves it square. Only usable one blade at a
# time -- the line-up runs along X, so from here the three would sit on top of each other.
SOLO_VIEW = {
    "profile": (16.0, 0.0, 0.9),
    # Low and close behind, where the hinge would be: the arms, the bracket and the beam together.
    "rear34": (6.0, -9.0, 2.2),
}


def load_generator():
    """Import make_blades.py as a module, without running its export."""
    spec = importlib.util.spec_from_file_location("make_blades", os.path.join(HERE, "make_blades.py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def mock_hinge(gen, root, width, report):
    """Draw a G-11-sized hinge, and a level arm behind it, plugged into the blade's stack node."""
    node_z = report["top_stack_node_y"]
    w, h = gen.HINGE_FOOTPRINT
    name = report["name"]
    # Starting at y = 0, the plate's back face, which is where a stack-attached part lands.
    gen.box(name + "Hinge", -w / 2.0, w / 2.0, -HINGE_DEPTH, 0.0, node_z - h / 2.0, node_z + h / 2.0,
            root, gen.ksp_material(name + "Hinge", HINGE_TINT))
    half = ARM_SECTION / 2.0
    gen.box(name + "Arm", -half, half, -HINGE_DEPTH - ARM_LENGTH, -HINGE_DEPTH,
            node_z - half, node_z + half, root, gen.ksp_material(name + "Arm", ARM_TINT))


def ground(gen, extent):
    """Lay a plane at z = 0 -- where the model's lowest point sits -- so the blades read as posed."""
    material = gen.ksp_material("Ground", GROUND_TINT)
    gen.box("Ground", -extent, extent, -extent, extent, -0.02, 0.0, None, material)


def build_lineup(gen, livery_name=None, blades=None):
    """Build the blades side by side and return every mesh corner, in world space."""
    gen.clear_scene()

    # Laid out edge to edge rather than on a fixed pitch: the widths differ by more than a factor of
    # two, so a fixed pitch either overlaps the big one or strands the small one.
    x = 0.0
    for name, width in (blades or gen.BLADES):
        x += width / 2.0
        root, report = gen.build_blade(name, width, x, livery_name)
        mock_hinge(gen, root, width, report)
        x += width / 2.0 + GAP

    bpy.context.view_layer.update()     # parenting only lands in matrix_world after this
    points = []
    for obj in bpy.data.objects:
        if obj.type != "MESH":
            continue
        for corner in obj.bound_box:
            points.append(obj.matrix_world @ Vector(corner))

    # After the corners are collected, so the frame stays fitted to the blades rather than to a
    # plane that has to be far bigger than them to read as ground.
    ground(gen, 4.0 * x)
    return points


def aim_and_fit(cam, points, aspect):
    """Point the camera at the geometry and set a lens that frames all of it, with a margin.

    Returns the focal length in mm. Fitted rather than hand-set so the views stay correct when the
    blade dimensions change.
    """
    position = Vector(cam.location)

    def spans_for(direction):
        """Where the geometry lands in the frame, as (min, max) tangents horizontally and vertically."""
        basis = direction.to_track_quat("-Z", "Y").to_matrix()
        right, up, back = basis.col[0], basis.col[1], basis.col[2]
        out = []
        for axis in (right, up):
            ratios = []
            for p in points:
                depth = (p - position).dot(-back)
                if depth > 1e-6:
                    ratios.append((p - position).dot(axis) / depth)
            out.append((min(ratios), max(ratios)))
        return out, right, up, -back

    # Aiming at the centroid does not centre the PICTURE: under perspective the near geometry
    # subtends more angle than the far. So measure where the geometry actually lands, re-aim at the
    # middle of that, and repeat. Three passes is well past converged at these angles.
    direction = (sum(points, Vector((0.0, 0.0, 0.0))) / len(points)) - position
    for _ in range(3):
        spans, right, up, forward = spans_for(direction)
        direction = (forward
                     + right * ((spans[0][0] + spans[0][1]) / 2.0)
                     + up * ((spans[1][0] + spans[1][1]) / 2.0)).normalized()

    cam.rotation_euler = direction.to_track_quat("-Z", "Y").to_euler()
    spans, _, _, _ = spans_for(direction)     # measured with the orientation actually used

    # Blender fits its 36 mm sensor to the LONGER side of the render, which here is the width.
    # The shorter of the two focal lengths is the one that fits: the longer satisfies its own axis
    # and lets the geometry run off the other one.
    tan_h = max(abs(spans[0][0]), abs(spans[0][1])) * MARGIN
    tan_v = max(abs(spans[1][0]), abs(spans[1][1])) * MARGIN
    return min(SENSOR / 2.0 / tan_h, SENSOR / aspect / 2.0 / tan_v)


def setup_scene(width_px):
    """Set up renderer, world and lights, and return the scene."""
    scene = bpy.context.scene
    scene.render.engine = "BLENDER_EEVEE"
    scene.render.resolution_x = width_px
    scene.render.resolution_y = width_px // 2
    scene.render.image_settings.file_format = "PNG"
    scene.render.image_settings.compression = 100

    # Dithering costs nothing on screen but wrecks PNG compression on the large smooth areas a steel
    # plate is mostly made of -- it roughly trebled the file size here.
    scene.render.dither_intensity = 0.0

    scene.world = bpy.data.worlds.new("W")
    scene.world.use_nodes = True
    scene.world.node_tree.nodes["Background"].inputs[0].default_value = (0.05, 0.06, 0.08, 1.0)

    key = bpy.data.objects.new("Key", bpy.data.lights.new("Key", "SUN"))
    key.data.energy = 4.0
    key.rotation_euler = (math.radians(50), 0.0, math.radians(-40))
    scene.collection.objects.link(key)

    fill = bpy.data.objects.new("Fill", bpy.data.lights.new("Fill", "SUN"))
    fill.data.energy = 1.5
    fill.rotation_euler = (math.radians(70), 0.0, math.radians(150))
    scene.collection.objects.link(fill)
    return scene


def render(scene, points, view, path, views=VIEWS):
    """Render one named view of the current scene to the given path."""
    cam_data = bpy.data.cameras.new("Cam")
    cam = bpy.data.objects.new("Cam", cam_data)
    scene.collection.objects.link(cam)
    scene.camera = cam
    cam.location = views[view]

    aspect = scene.render.resolution_x / scene.render.resolution_y
    cam_data.lens = aim_and_fit(cam, points, aspect)

    scene.render.filepath = path
    bpy.ops.render.render(write_still=True)
    lens = cam_data.lens

    bpy.data.objects.remove(cam, do_unlink=True)
    bpy.data.cameras.remove(cam_data, do_unlink=True)
    return lens


def parse_args():
    """Read the options given after '--', falling back to every livery in tools/renders, 1400 px."""
    args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    out = args[args.index("--out") + 1] if "--out" in args else os.path.join(HERE, "renders")
    width = int(args[args.index("--width") + 1]) if "--width" in args else 1400
    livery = args[args.index("--livery") + 1] if "--livery" in args else None
    blade = args[args.index("--blade") + 1] if "--blade" in args else None
    return os.path.abspath(out), width, livery, blade


def main():
    out, width, livery, blade = parse_args()
    os.makedirs(out, exist_ok=True)

    gen = load_generator()

    # One named blade, or all three. The straight-on profile only makes sense for a single one.
    blades = [entry for entry in gen.BLADES if blade is None or entry[0].endswith(blade)]
    if not blades:
        raise SystemExit("no blade matching %r in %s" % (blade, [e[0] for e in gen.BLADES]))
    views = dict(VIEWS)
    if len(blades) == 1:
        views.update(SOLO_VIEW)
    suffix = "" if blade is None else "_" + blades[0][0][-3:].lower()

    # Every livery unless one is named. They differ only in paint, so this is the only way to see a
    # .cfg-only livery at all -- it exists nowhere in the .mu, and otherwise only shows up in game.
    # ⚠️ These are Blender previews of the tint, not of the KSP shader: the base colour is shown
    # neat, without the grey texture KSP multiplies it by, so both come out lighter than in game.
    liveries = [livery] if livery else [entry[0] for entry in gen.LIVERIES]
    for livery_name in liveries:
        # Lights and world go up AFTER the blades: build_lineup starts by emptying the scene, which
        # would take them with it.
        points = build_lineup(gen, livery_name, blades)
        scene = setup_scene(width)
        for view in views:
            path = os.path.join(out, "blades_%s%s_%s.png"
                                % (view, suffix, livery_name.lower()))
            lens = render(scene, points, view, path, views)
            print("rendered %s  (fitted lens %.0f mm)" % (path, lens))


if __name__ == "__main__":
    main()

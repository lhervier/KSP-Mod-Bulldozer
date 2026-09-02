using System;
using System.Collections.Generic;
using System.Text;
using Unity.Collections;
using UnityEngine;
using Parallax;
using Parallax.PQS_Mods;
using com.github.lhervier.ksp.shared;
using com.github.lhervier.ksp.bulldozermod.parallax;
using com.github.lhervier.ksp.bulldozermod.terrain;
using com.github.lhervier.ksp.bulldozermod.utils;

namespace com.github.lhervier.ksp.bulldozermod.diagnostics
{
    /// <summary>
    /// Read-only dump of everything a pass would decide, written to KSP.log. Changes nothing.
    ///
    /// It exists to separate three failure modes that look identical on screen — "rocks stay standing":
    ///
    /// 1. a wrong projection (the mask lands somewhere else than the strip) — caught by the per-quad sanity
    ///    line, where the quad center MUST project to the middle of its own mask, and by the instance table,
    ///    where the verdict must follow the measured distance to the strip axis;
    /// 2. rocks that no overlay can ever show (families Parallax does not track per instance) — caught by
    ///    the family list at the end;
    /// 3. neither: the blade is 5 m wide and the big scatters are clusters anchored up to 25 m away, so they
    ///    legitimately survive — which the instance table shows as "far from the axis, not blocked".
    ///
    /// The two coordinate systems it prints are deliberately independent: (along, perp) comes from plain
    /// tangent-plane geometry, the verdict comes from the mask. Agreement between them is the proof.
    /// </summary>
    public static class BandDiagnostic
    {
        private static readonly ModLogger LOGGER = new ModLogger("BandDump");

        // How far past the strip to list instances: enough to show the ones that just missed, and to expose
        // a projection error that puts the cleared area somewhere else entirely.
        private const double ListMargin = 60.0;

        // Instances are listed, not summarised, so a table has to stay readable in the log.
        private const int MaxListed = 60;

        /// <summary>
        /// Whether the blades show their "dump this pass" entry in the PAW. Off until the debug window says
        /// otherwise, so a normal game never sees it.
        ///
        /// Static, and for the same reason the overlay's switch is: the setting outlives a scene while the
        /// blades reading it are rebuilt at every load, so the flag cannot live on either side. The debug
        /// window's view model owns the pairing with the stored preference.
        /// </summary>
        public static bool ButtonVisible;

        /// <summary>Dumps the pass <paramref name="blade"/> would apply right now. Never modifies
        /// anything.</summary>
        public static void Dump(BulldozerCleanModule blade, double length, double width)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== BULLDOZER PASS DUMP ===");

            Vessel vessel = blade == null ? null : blade.vessel;
            if (vessel == null || vessel.mainBody == null || vessel.mainBody.pqsController == null)
            {
                LOGGER.LogWarning("No vessel / body / PQS — nothing to dump");
                return;
            }
            CelestialBody body = vessel.mainBody;
            PQS sphere = body.pqsController;

            SurfaceBand band;
            if (!BulldozerCleanModule.TryBuildBand(blade, length, width, out band))
            {
                sb.AppendLine("TryBuildBand REFUSED: the blade aims nowhere usable.");
                sb.AppendLine("  facing axis (world) = " + blade.FacingAxis);
                LOGGER.LogInfo(sb.ToString());
                return;
            }

            // Spelt out because the dump reports the strip, not the sliders, and the two differ on purpose:
            // a 50 x 10 m setting draws 49 x 9 so that a well-placed pass can land on 10 x 2 cells.
            sb.AppendLine($"sliders length={length:F0} width={width:F0} — the strip below is " +
                          $"{SurfaceBand.GridSlack:F0} m shorter and narrower (SurfaceBand.GridSlack)");

            DumpBand(sb, blade, vessel, body, sphere, band);
            DumpHeadings(sb, blade, vessel, body, band);
            DumpQuads(sb, body, band);
            DumpInstances(sb, body, band);
            DumpFamilies(sb, body);

            LOGGER.LogInfo(sb.ToString());
        }

        private static void DumpBand(StringBuilder sb, BulldozerCleanModule blade, Vessel vessel,
                                     CelestialBody body, PQS sphere, SurfaceBand band)
        {
            sb.AppendLine($"body={body.name} R={body.Radius:F0} maxLevel={sphere.maxLevel} " +
                          $"cell={ScatterMask.MetersPerPixel:F1} m");
            sb.AppendLine($"vessel lat={vessel.latitude:F6} lon={vessel.longitude:F6}");
            sb.AppendLine($"band origin={band.origin} forward={band.forward} " +
                          $"length={band.length:F0} width={band.width:F0}");

            // The origin is rebuilt from lat/lon while everything else comes from world positions. If those
            // two roads ever diverged, the whole pass would sit somewhere else — so they are compared here.
            // Against the BLADE'S EDGE, which is where a pass starts: measured against the vessel centre,
            // this would now report the mounting offset as an error.
            Vector3d fromWorld = body.GetRelSurfacePosition(blade.OriginPoint).normalized;
            double originError = MathUtils.AngleBetween(band.origin, fromWorld) * body.Radius;
            sb.AppendLine($"origin check: lat/lon vs blade edge differ by {originError:F2} m " +
                          "(must be ~0; anything large means a frame mismatch)");

            // How far from the middle of the rover that edge sits. Not an error — it is the whole point of
            // aiming from the blade — but it is what tells two blades' passes apart.
            Vector3d centre = body.GetRelSurfacePosition(vessel.GetWorldPos3D()).normalized;
            double offset = MathUtils.AngleBetween(fromWorld, centre) * body.Radius;
            sb.AppendLine($"blade edge is {offset:F1} m from the vessel centre (expected: a mounting offset)");

            sb.AppendLine($"blade facing (world)={blade.FacingAxis} -> forward (body frame)={band.forward}");
        }

        /// <summary>
        /// Bearings of every axis that could plausibly mean "forward", so the one that matches reality can be
        /// identified rather than assumed.
        ///
        /// Two independent references to compare them against: the NAVBALL on screen (which reads the
        /// control point, so a disagreement with the blade's own axis means the blade is not mounted square
        /// with the chassis), and the SURFACE VELOCITY while driving — the only unarguable answer to "where
        /// does this rover actually go".
        /// </summary>
        private static void DumpHeadings(StringBuilder sb, BulldozerCleanModule blade, Vessel vessel,
                                         CelestialBody body, SurfaceBand band)
        {
            // North and east in the body frame, built analytically from lat/lon. This frame is
            // SphericalVector(lat, lon).xzy, so axis 1 is the pole: north is d/dlat, east is d/dlon.
            double lat = vessel.latitude * Math.PI / 180.0;
            double lon = vessel.longitude * Math.PI / 180.0;
            Vector3d north = new Vector3d(-Math.Sin(lat) * Math.Cos(lon), Math.Cos(lat),
                                          -Math.Sin(lat) * Math.Sin(lon));
            Vector3d east = new Vector3d(-Math.Sin(lon), 0.0, Math.Cos(lon));

            sb.AppendLine("headings (deg, 0=north, 90=east — compare the first line with the NAVBALL):");
            sb.AppendLine("    " + Bearing("band forward", band.forward, band.origin, north, east));
            sb.AppendLine("    " + Bearing("blade facing (what the band uses)",
                                           body.GetRelSurfaceDirection(blade.FacingAxis), band.origin,
                                           north, east));

            Transform reference = vessel.ReferenceTransform;
            if (reference != null)
            {
                sb.AppendLine("    " + Bearing("control point .up (the band's fallback axis)",
                                               body.GetRelSurfaceDirection(reference.up), band.origin, north, east));
                sb.AppendLine("    " + Bearing("control point .forward",
                                               body.GetRelSurfaceDirection(reference.forward), band.origin, north, east));
                sb.AppendLine("    " + Bearing("control point .right",
                                               body.GetRelSurfaceDirection(reference.right), band.origin, north, east));
            }
            Transform root = vessel.transform;
            if (root != null)
            {
                sb.AppendLine("    " + Bearing("vessel root .up",
                                               body.GetRelSurfaceDirection(root.up), band.origin, north, east));
                sb.AppendLine("    " + Bearing("vessel root .forward",
                                               body.GetRelSurfaceDirection(root.forward), band.origin, north, east));
            }

            // Ground truth, but only while actually rolling: below walking pace the direction is noise.
            Vector3d velocity = vessel.srf_velocity;
            double speed = velocity.magnitude;
            if (speed > 0.5)
            {
                sb.AppendLine("    " + Bearing($"SURFACE VELOCITY ({speed:F1} m/s) = where the rover really goes",
                                               body.GetRelSurfaceDirection(velocity), band.origin, north, east));
            }
            else
            {
                sb.AppendLine($"    surface velocity: {speed:F2} m/s — too slow to tell where the rover goes; " +
                              "drive forward and dump again");
            }
        }

        // One bearing line: where that axis points once flattened onto the ground, and how much of it pointed
        // at the sky in the first place (a nearly vertical axis has no meaningful bearing).
        private static string Bearing(string label, Vector3d axis, Vector3d up, Vector3d north, Vector3d east)
        {
            Vector3d unit = axis.normalized;
            double vertical = Vector3d.Dot(unit, up);
            Vector3d tangent = unit - up * vertical;
            if (tangent.magnitude <= 1e-9)
            {
                return $"{label,-52} vertical, no bearing (vertical fraction {vertical:F3})";
            }
            tangent = tangent.normalized;
            double bearing = Math.Atan2(Vector3d.Dot(tangent, east), Vector3d.Dot(tangent, north))
                             * 180.0 / Math.PI;
            if (bearing < 0.0)
            {
                bearing += 360.0;
            }
            return $"{label,-52} {bearing,6:F1} deg   (vertical fraction {vertical,6:F3})";
        }

        private static void DumpQuads(StringBuilder sb, CelestialBody body, SurfaceBand band)
        {
            List<QuadRef> quads = QuadLocator.FindImpactedQuads(body, band);
            sb.AppendLine($"impacted quads: {quads.Count}");

            foreach (QuadRef quad in quads)
            {
                double footprint = quad.halfDiagonal * ScatterCleaner.FootprintMargin;
                int size = ScatterMask.SizeFor(footprint);
                BandRaster raster = ScatterCleaner.RasterFor(body, quad.direction, quad.halfDiagonal, band);

                sb.AppendLine($"  quad dir={quad.direction} halfDiag={quad.halfDiagonal:F0} m " +
                              $"footprint={footprint:F0} m mask={size}px " +
                              $"({2.0 * footprint / size:F2} m/px) " +
                              $"owner={(quad.owner == null ? "free" : quad.owner.gameObject.name)}");

                // THE sanity check on the projection: a quad sits at the center of its own mask, so its
                // center direction must land on (size/2, size/2). Any other value means the mask frame and
                // the strip do not live in the same space, and everything downstream is fiction.
                Vector2 centerPx = raster.PixelOf(quad.direction.normalized);
                sb.AppendLine($"    quad center -> px=({centerPx.x:F1},{centerPx.y:F1}) " +
                              $"expected ({size * 0.5f:F1},{size * 0.5f:F1})");

                Vector2 startPx = raster.PixelOf(band.origin);
                Vector2 endPx = raster.PixelOf(band.End);
                sb.AppendLine($"    band px start=({startPx.x:F1},{startPx.y:F1}) " +
                              $"end=({endPx.x:F1},{endPx.y:F1})");

                int minX, minY, maxX, maxY;
                if (!raster.TryGetCellBounds(out minX, out minY, out maxX, out maxY))
                {
                    sb.AppendLine("    NO cell bounds: the strip falls outside this mask");
                    continue;
                }
                int cleared = 0;
                for (int y = minY; y <= maxY; y++)
                {
                    for (int x = minX; x <= maxX; x++)
                    {
                        if (raster.CellCleared(x, y))
                        {
                            cleared++;
                        }
                    }
                }
                sb.AppendLine($"    cells cleared={cleared} in box [{minX}..{maxX}]x[{minY}..{maxY}]");
            }
        }

        // Lists every collideable instance around the vessel with, side by side, where it sits relative to
        // the strip (plain geometry) and what the mask says about it (the shader's own verdict).
        private static void DumpInstances(StringBuilder sb, CelestialBody body, SurfaceBand band)
        {
            FastList<ScatterColliderData> collision = Parallax.CollisionManager.collisionData;
            if (collision == null)
            {
                sb.AppendLine("no collideable instance data on this body");
                return;
            }

            // Tangent frame at the start of the strip: `side` completes it, so a rock's position reads as
            // (how far along, how far off to the side) in meters.
            Vector3d side = Vector3d.Cross(band.origin, band.forward).normalized;

            Vector3d[] corners = new Vector3d[4];
            List<string> rows = new List<string>();
            int blocked = 0;
            int considered = 0;

            for (int c = 0; c < collision.Length; c++)
            {
                ScatterColliderData data = collision[c];
                if (data == null || data.disposed || !data.quadLocalData.IsCreated)
                {
                    continue;
                }
                ScatterSystemQuadData quadData = data.scatterSystemQuad;
                if (quadData == null || quadData.quad == null || quadData.quad.meshRenderer == null)
                {
                    continue;
                }

                double halfDiagonal;
                if (!QuadLocator.TryGetHalfDiagonal(body, quadData.quad, corners, out halfDiagonal))
                {
                    continue;
                }

                PQSMod_MapDecalVertexRemoveScatter owner;
                PQSMod_MapDecalVertexRemoveScatter.maskedScattersPerQuad.TryGetValue(quadData.quad, out owner);
                bool foreign = owner != null && !(owner is BulldozerScatterDecal);

                BandRaster raster = ScatterCleaner.RasterFor(
                    body, quadData.quad.positionPlanetRelative, halfDiagonal, band);

                Scatter scatter = ScatterAt(data.collideableScattersIndex);
                string family = scatter != null ? scatter.scatterName : "slot " + data.collideableScattersIndex;

                Matrix4x4 quadToWorld = quadData.quad.meshRenderer.localToWorldMatrix;
                NativeArray<PositionData> positions = data.quadLocalData;
                int count = Math.Min(data.dataCount, positions.Length);

                for (int i = 0; i < count; i++)
                {
                    Vector3 world = quadToWorld.MultiplyPoint3x4(positions[i].localPos);
                    Vector3d dir = body.GetRelSurfacePosition(world).normalized;

                    // Tangent-plane coordinates, computed WITHOUT the mask: this is the reference the mask
                    // verdict is checked against.
                    Vector3d tangent = dir - band.origin * Vector3d.Dot(dir, band.origin);
                    double along = Vector3d.Dot(tangent, band.forward) * body.Radius;
                    double perp = Vector3d.Dot(tangent, side) * body.Radius;

                    if (along < -ListMargin || along > band.length + ListMargin ||
                        Math.Abs(perp) > ListMargin)
                    {
                        continue;
                    }
                    considered++;

                    float value = raster.Sample(dir);
                    bool blocks = value < 0.5f && !foreign;
                    if (blocks)
                    {
                        blocked++;
                    }
                    if (rows.Count < MaxListed)
                    {
                        Vector2 px = raster.PixelOf(dir);
                        rows.Add($"    {family,-22} along={along,7:F1} perp={perp,7:F1} " +
                                 $"px=({px.x,6:F1},{px.y,6:F1}) mask={value:F2} " +
                                 $"{(blocks ? "BLOCKED" : "kept")}{(foreign ? " (foreign quad)" : "")}");
                    }
                }
            }

            sb.AppendLine($"collideable instances within {ListMargin:F0} m of the strip: {considered}, " +
                          $"of which {blocked} would go");
            sb.AppendLine("    (along = meters down the strip, perp = meters off its axis; the blade spans " +
                          $"perp -{band.width * 0.5:F1} to +{band.width * 0.5:F1})");
            rows.Sort(StringComparer.Ordinal);
            foreach (string row in rows)
            {
                sb.AppendLine(row);
            }
            if (considered > MaxListed)
            {
                sb.AppendLine($"    ... {considered - MaxListed} more not listed");
            }
        }

        // Which families exist on this body, and which of them an overlay can show at all. A rock that is
        // removed but never highlighted is almost always a family missing from this collideable list.
        private static void DumpFamilies(StringBuilder sb, CelestialBody body)
        {
            Scatter[] collideable = Parallax.CollisionManager.collideableScatters;
            sb.AppendLine("scatter families blocked by our decal on this body:");
            foreach (string name in ParallaxInterop.GetScatterNames(body.name))
            {
                bool found = false;
                if (collideable != null)
                {
                    for (int i = 0; i < collideable.Length && !found; i++)
                    {
                        found = collideable[i] != null && collideable[i].scatterName == name;
                    }
                }
                sb.AppendLine($"    {name,-30} {(found ? "collideable (can be highlighted)" : "NOT collideable — removed, never highlighted")}");
            }
        }

        private static Scatter ScatterAt(int index)
        {
            Scatter[] scatters = Parallax.CollisionManager.collideableScatters;
            return scatters != null && index >= 0 && index < scatters.Length ? scatters[index] : null;
        }
    }
}

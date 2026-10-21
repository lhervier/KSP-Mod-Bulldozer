using System.Collections.Generic;
using System.Text;
using UnityEngine;
using Parallax;
using Parallax.PQS_Mods;
using com.github.lhervier.ksp.mod.bulldozer.parallax;
using com.github.lhervier.ksp.mod.bulldozer.terrain;
using com.github.lhervier.ksp.mod.bulldozer.utils;

namespace com.github.lhervier.ksp.mod.bulldozer.diagnostics
{
    /// <summary>
    /// The leaf quads around the vessel, as a block of text — what the debug window displays to let the
    /// player park on a quad edge or corner.
    ///
    /// Reading it: the distances are vessel-to-quad-CENTER. Two equal distances mean the vessel is on the
    /// edge between those two quads; four equal distances mean it is on the corner where four quads meet.
    /// The "impacted" count is how many decals a clean would create right now — the number to watch when
    /// testing a clean that straddles quads.
    ///
    /// Kept in English, like the log dumps of <see cref="BandDiagnostic"/> and unlike the window around it:
    /// this is a developer readout, and half of its words (half-diag, spread) name things this project only
    /// ever names in English.
    /// </summary>
    public class QuadReadout
    {
        // Reused across refreshes rather than rebuilt: the window rebuilds this text twice a second for as
        // long as it stays open.
        private readonly List<KeyValuePair<PQ, double>> _ranked = new List<KeyValuePair<PQ, double>>();
        private readonly StringBuilder _sb = new StringBuilder();

        /// <summary>Builds the readout for the active vessel. Never null; says so when there is nothing to report.</summary>
        public string Build()
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == null || vessel.mainBody?.pqsController == null)
            {
                return "no vessel / PQS";
            }

            CelestialBody body = vessel.mainBody;
            PQS sphere = body.pqsController;
            Vector3d vesselDir = body.GetRelSurfaceNVector(vessel.latitude, vessel.longitude);

            _ranked.Clear();
            foreach (KeyValuePair<PQ, ScatterSystemQuadData> entry in ScatterComponent.scatterQuadData)
            {
                PQ quad = entry.Key;
                if (quad == null || quad.gameObject == null || quad.subdivision != sphere.maxLevel)
                {
                    continue;
                }
                double dist = MathUtils.AngleBetween(quad.positionPlanetRelative, vesselDir) * body.Radius;
                _ranked.Add(new KeyValuePair<PQ, double>(quad, dist));
            }
            _ranked.Sort((a, b) => a.Value.CompareTo(b.Value));

            _sb.Length = 0;
            _sb.AppendLine($"{body.name}  maxLevel={sphere.maxLevel}");

            if (_ranked.Count == 0)
            {
                _sb.AppendLine("no leaf quad tracked here");
                return _sb.ToString();
            }

            _sb.AppendLine("distance to quad centers:");
            int shown = Mathf.Min(4, _ranked.Count);
            for (int i = 0; i < shown; i++)
            {
                PQ quad = _ranked[i].Key;
                PQSMod_MapDecalVertexRemoveScatter owner;
                PQSMod_MapDecalVertexRemoveScatter.maskedScattersPerQuad.TryGetValue(quad, out owner);
                string ownerName = owner == null ? "free" : owner.gameObject.name;
                _sb.AppendLine($"  #{i}  {_ranked[i].Value,7:F1} m   {ownerName}");
            }

            // Equal distances are the signal: 2 for an edge, 4 for a corner.
            double spread = _ranked[shown - 1].Value - _ranked[0].Value;
            _sb.AppendLine("");
            _sb.AppendLine($"spread over {shown}: {spread:F1} m");

            // The pass the previewed blade currently offers, so this line answers "what would a click do
            // HERE". It needs a blade to read: with no preview running there is no strip to report on, and
            // guessing one from the vessel would answer a different question.
            BulldozerCleanModule blade = ScatterHighlighter.Source;
            // Initialised because the short-circuit below leaves it untouched when there is no blade.
            SurfaceBand band = new SurfaceBand();
            bool aimed = blade != null
                && BulldozerCleanModule.TryBuildBand(blade, blade.passLength, blade.passWidth, out band);
            List<QuadRef> impacted = aimed
                ? QuadLocator.FindImpactedQuads(body, band)
                : new List<QuadRef>();

            // The two sides are shown separately on purpose: a quad is not square, and the gap between them
            // grows towards the edges of a cube face (1.41x at 45 deg). The two half-diagonals expose the
            // skew on top of that: they are equal only where the quad is a true rectangle.
            QuadMetrics.QuadShape shape;
            if (QuadMetrics.TryMeasureShape(body, _ranked[0].Key, out shape))
            {
                _sb.AppendLine($"sides {shape.sideA:F0} x {shape.sideB:F0} m " +
                               $"(nominal {QuadMetrics.NominalQuadEdge(sphere):F0} m)");
                _sb.AppendLine($"half-diag {shape.halfDiagonalMin:F0} .. {shape.halfDiagonalMax:F0} m");
            }
            else
            {
                _sb.AppendLine($"shape not measurable (nominal {QuadMetrics.NominalQuadEdge(sphere):F0} m)");
            }

            _sb.AppendLine(blade == null
                ? "no blade previewing: switch a blade's preview on to see what a pass would touch"
                : aimed
                    ? $"a {band.length:F0} x {band.width:F0} m pass here would touch " +
                      $"{impacted.Count} quad(s)"
                    : "no usable heading: the blade must look along the ground");

            // What the decals would actually allocate, so a body with tiny or huge quads shows up here
            // rather than as a memory surprise. One byte per pixel and nothing else: the mask is written
            // straight into its R8 texture, there is no CPU-side copy beside it. Each quad is sized on its
            // own half-diagonal, so the total is reported alongside the biggest one rather than a single
            // figure "each".
            if (impacted.Count > 0)
            {
                double biggest = 0.0;
                long kilobytes = 0L;
                foreach (QuadRef quad in impacted)
                {
                    double quadFootprint = quad.halfDiagonal * ScatterCleaner.FootprintMargin;
                    if (quadFootprint > biggest)
                    {
                        biggest = quadFootprint;
                    }
                    int quadPx = ScatterMask.SizeFor(quadFootprint);
                    kilobytes += (long)quadPx * quadPx / 1024L;
                }

                int px = ScatterMask.SizeFor(biggest);
                _sb.AppendLine($"largest mask {px}px over {2.0 * biggest:F0} m " +
                               $"({2.0 * biggest / px:F2} m/px), {kilobytes} Ko total");
            }
            return _sb.ToString();
        }
    }
}

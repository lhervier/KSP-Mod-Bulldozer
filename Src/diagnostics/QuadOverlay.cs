using System.Collections.Generic;
using UnityEngine;
using Parallax;
using Parallax.PQS_Mods;
using com.github.lhervier.ksp.bulldozermod.terrain;
using com.github.lhervier.ksp.bulldozermod.utils;

namespace com.github.lhervier.ksp.bulldozermod.diagnostics
{
    /// <summary>
    /// On-screen readout of the leaf quads around the vessel, to position it on a quad edge or corner.
    ///
    /// Reading it: the distances are vessel-to-quad-CENTER. Two equal distances mean the vessel is on the
    /// edge between those two quads; four equal distances mean it is on the corner where four quads meet.
    /// The "impacted" count is how many decals a clean would create right now — the number to watch when
    /// testing a clean that straddles quads.
    ///
    /// Toggle with MOD+B (right-shift on Windows). Temporary, to be removed once the mechanism is settled.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class QuadOverlay : MonoBehaviour
    {
        private const float RefreshInterval = 0.25f;

        private bool _visible;
        private float _nextRefresh;
        private string _text = "";
        private Rect _window = new Rect(60f, 120f, 340f, 0f);

        private void Update()
        {
            if (GameSettings.MODIFIER_KEY.GetKey() && Input.GetKeyDown(KeyCode.B))
            {
                _visible = !_visible;
            }
            if (!_visible || Time.time < _nextRefresh)
            {
                return;
            }
            _nextRefresh = Time.time + RefreshInterval;
            _text = BuildText();
        }

        private void OnGUI()
        {
            if (!_visible)
            {
                return;
            }
            GUI.skin = HighLogic.Skin;
            _window = GUILayout.Window(GetInstanceID(), _window, DrawWindow, "Bulldozer — quads",
                                       GUILayout.Width(340f));
        }

        private void DrawWindow(int id)
        {
            GUILayout.Label(_text);
            GUI.DragWindow();
        }

        private string BuildText()
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == null || vessel.mainBody?.pqsController == null)
            {
                return "no vessel / PQS";
            }

            CelestialBody body = vessel.mainBody;
            PQS sphere = body.pqsController;
            Vector3d vesselDir = body.GetRelSurfaceNVector(vessel.latitude, vessel.longitude);

            List<KeyValuePair<PQ, double>> ranked = new List<KeyValuePair<PQ, double>>();
            foreach (KeyValuePair<PQ, ScatterSystemQuadData> entry in ScatterComponent.scatterQuadData)
            {
                PQ quad = entry.Key;
                if (quad == null || quad.gameObject == null || quad.subdivision != sphere.maxLevel)
                {
                    continue;
                }
                double dist = MathUtils.AngleBetween(quad.positionPlanetRelative, vesselDir) * body.Radius;
                ranked.Add(new KeyValuePair<PQ, double>(quad, dist));
            }
            ranked.Sort((a, b) => a.Value.CompareTo(b.Value));

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine($"{body.name}  maxLevel={sphere.maxLevel}");

            if (ranked.Count == 0)
            {
                sb.AppendLine("no leaf quad tracked here");
                return sb.ToString();
            }

            sb.AppendLine("distance to quad centers:");
            int shown = Mathf.Min(4, ranked.Count);
            for (int i = 0; i < shown; i++)
            {
                PQ quad = ranked[i].Key;
                PQSMod_MapDecalVertexRemoveScatter owner;
                PQSMod_MapDecalVertexRemoveScatter.maskedScattersPerQuad.TryGetValue(quad, out owner);
                string ownerName = owner == null ? "free" : owner.gameObject.name;
                sb.AppendLine($"  #{i}  {ranked[i].Value,7:F1} m   {ownerName}");
            }

            // Equal distances are the signal: 2 for an edge, 4 for a corner.
            double spread = ranked[shown - 1].Value - ranked[0].Value;
            sb.AppendLine("");
            sb.AppendLine($"spread over {shown}: {spread:F1} m");

            List<QuadRef> impacted = QuadLocator.FindImpactedQuads(
                body, vessel.latitude, vessel.longitude, BulldozerCleanModule.ClearRadius);

            // The two sides are shown separately on purpose: a quad is not square, and the gap between them
            // grows towards the edges of a cube face (1.41x at 45 deg). The two half-diagonals expose the
            // skew on top of that: they are equal only where the quad is a true rectangle.
            QuadMetrics.QuadShape shape;
            if (QuadMetrics.TryMeasureShape(body, ranked[0].Key, out shape))
            {
                sb.AppendLine($"sides {shape.sideA:F0} x {shape.sideB:F0} m " +
                              $"(nominal {QuadMetrics.NominalQuadEdge(sphere):F0} m)");
                sb.AppendLine($"half-diag {shape.halfDiagonalMin:F0} .. {shape.halfDiagonalMax:F0} m");
            }
            else
            {
                sb.AppendLine($"shape not measurable (nominal {QuadMetrics.NominalQuadEdge(sphere):F0} m)");
            }

            sb.AppendLine($"a {BulldozerCleanModule.ClearRadius:F0} m clean here would touch {impacted.Count} quad(s)");

            // What the decals would actually allocate, so a body with tiny or huge quads shows up here
            // rather than as a memory surprise. Each quad is sized on its own half-diagonal, so the total is
            // reported alongside the biggest one rather than a single figure "each".
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
                    kilobytes += 2L * quadPx * quadPx / 1024L;
                }

                int px = ScatterMask.SizeFor(biggest);
                sb.AppendLine($"largest mask {px}px over {2.0 * biggest:F0} m " +
                              $"({2.0 * biggest / px:F2} m/px), {kilobytes} Ko total");
            }
            return sb.ToString();
        }
    }
}

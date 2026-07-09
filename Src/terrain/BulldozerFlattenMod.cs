using System;
using UnityEngine;

namespace com.github.lhervier.ksp.bulldozermod.terrain
{
    /// <summary>
    /// Our own additive flatten PQSMod — a stock PQSMod_FlattenArea WITHOUT the `quadActive` short-circuit.
    ///
    /// Why not the stock mod: stock FlattenArea bails out of OnVertexBuildHeight on its `quadActive` guard
    /// whenever it is evaluated outside an actual quad build — which is exactly what happens when the PQS
    /// positions a quad: `positionPlanet = dir * GetSurfaceHeight(dir)` uses the PUBLIC GetSurfaceHeight
    /// (overrideQuadBuildCheck = false), so the stock mod returns the NATURAL height there while the quad's
    /// vertices (built with quadActive set) get the flat height. The quad ends up positioned at the natural
    /// height but meshed flat -> the whole tile renders/collides ~(natural-flattenTo) metres too high (observed
    /// +13 m -> rover embedded -> explosion). Applying UNCONDITIONALLY makes GetSurfaceHeight consistent for
    /// both override values, so positionPlanet and the vertices agree. (Perf: this runs for every vertex of
    /// every quad; a spatial short-circuit is deferred — fine for the milestone.)
    ///
    /// Geometry mirrors stock FlattenArea: a flat disk of `radius` at absolute height `flattenTo`, surrounded
    /// by a CubicHermite blend ring out to `radius + ringWidth` back to the surrounding height.
    /// </summary>
    public class BulldozerFlattenMod : PQSMod
    {
        // Set by the injector before OnSetup.
        public double lat;
        public double lon;
        public double flattenTo;   // absolute altitude (ASL)
        public double radius;      // flat disk radius, metres
        public double ringWidth;   // blend ring width outside the disk, metres

        // Computed in OnSetup.
        private Vector3d _posNorm;
        private double _flattenToRadius;
        private double _angleInner;
        private double _angleOuter;
        private double _angleDelta;

        public override void OnSetup()
        {
            Planetarium.CelestialFrame cf = default(Planetarium.CelestialFrame);
            Planetarium.CelestialFrame.SetFrame(0.0, 0.0, 0.0, ref cf);
            _posNorm = (LatLon.GetSurfaceNVector(cf, lat, lon) * (sphere.radius + flattenTo)).normalized;

            _flattenToRadius = sphere.radius + flattenTo;
            _angleInner = Math.Atan(radius / sphere.radius);
            _angleOuter = Math.Atan((radius + ringWidth) / sphere.radius);
            _angleDelta = _angleOuter - _angleInner;
            if (_angleDelta <= 0.0)
            {
                _angleDelta = double.Epsilon; // avoid div-by-zero if ringWidth == 0
            }
        }

        public override void OnVertexBuildHeight(PQS.VertexBuildData data)
        {
            // NOTE: deliberately NO `quadActive` guard (that is the whole point — see class summary).
            double testAngle = Math.Acos(Vector3d.Dot(data.directionFromCenter, _posNorm));
            if (testAngle >= _angleOuter)
            {
                return; // outside the influence area -> leave the natural height
            }
            if (testAngle < _angleInner)
            {
                data.vertHeight = _flattenToRadius; // flat disk -> absolute target height
                return;
            }
            // Blend ring: smooth from flat (inner) back to the surrounding height (outer).
            double aDelta = (testAngle - _angleInner) / _angleDelta;
            data.vertHeight = CubicHermite(_flattenToRadius, data.vertHeight, 0.0, 0.0, aDelta);
        }

        // Stock FlattenArea's blend curve: cubic Hermite with the given end tangents.
        private static double CubicHermite(double start, double end, double startTangent, double endTangent, double t)
        {
            double t2 = t * t;
            double t3 = t2 * t;
            return start * (2.0 * t3 - 3.0 * t2 + 1.0)
                 + startTangent * (t3 - 2.0 * t2 + t)
                 + end * (-2.0 * t3 + 3.0 * t2)
                 + endTangent * (t3 - t2);
        }
    }
}

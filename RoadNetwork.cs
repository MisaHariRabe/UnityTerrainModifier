using System.Collections.Generic;
using UnityEngine;

namespace RoadTools
{
    /// <summary>
    /// Graphe routier construit depuis les Road et RoadIntersection de la scène.
    /// Nœuds : 2 par route (début / fin) + 1 par intersection.
    /// Arêtes : route entière, branche d'intersection (extrémité de route -> centre), jonction de bouts libres proches.
    /// </summary>
    public sealed class RoadNetwork
    {
        public struct Snap
        {
            public bool valid;
            public int road;        // -1 si sur une intersection
            public int hub;         // -1 si sur une route
            public float dist;      // mètres depuis le début de la route
            public Vector3 point;
            public float lateral;   // distance à la route / au centre
        }

        private struct Edge { public int to; public float cost; }

        private readonly List<Road> roads = new List<Road>();
        private readonly List<RoadIntersection> hubs = new List<RoadIntersection>();
        private readonly Dictionary<int, KeyValuePair<int, IntersectionArm>> endLink = new Dictionary<int, KeyValuePair<int, IntersectionArm>>();
        private List<Edge>[] adj;
        private int hubBase;

        public int RoadCount => roads.Count;
        public int HubCount => hubs.Count;
        private int NodeCount => hubBase + hubs.Count;

        // ------------------------------------------------------------------
        public static RoadNetwork Build(float joinDistance = 2f)
        {
            var n = new RoadNetwork();
            foreach (var r in Object.FindObjectsByType<Road>(FindObjectsSortMode.None))
                if (r != null && r.isActiveAndEnabled && r.Points.Count >= 2 && r.TotalLength > 0.5f) n.roads.Add(r);
            foreach (var h in Object.FindObjectsByType<RoadIntersection>(FindObjectsSortMode.None))
                if (h != null && h.isActiveAndEnabled) n.hubs.Add(h);
            n.Link(joinDistance);
            return n;
        }

        private void Link(float joinDistance)
        {
            int R = roads.Count;
            hubBase = 2 * R;
            adj = new List<Edge>[NodeCount];
            for (int i = 0; i < adj.Length; i++) adj[i] = new List<Edge>();

            for (int i = 0; i < R; i++) AddEdge(2 * i, 2 * i + 1, roads[i].TotalLength);

            for (int h = 0; h < hubs.Count; h++)
            {
                foreach (var arm in hubs[h].arms)
                {
                    if (arm == null || arm.road == null) continue;
                    int ri = roads.IndexOf(arm.road);
                    if (ri < 0) continue;
                    int node = 2 * ri + (arm.roadEnd == RoadEnd.Start ? 0 : 1);
                    if (endLink.ContainsKey(node)) continue;
                    endLink[node] = new KeyValuePair<int, IntersectionArm>(h, arm);
                    AddEdge(node, hubBase + h, hubs[h].EffectiveLength(arm));
                }
            }

            // Bouts libres de deux routes différentes qui se touchent.
            for (int a = 0; a < hubBase; a++)
            {
                if (endLink.ContainsKey(a)) continue;
                for (int b = a + 1; b < hubBase; b++)
                {
                    if ((a >> 1) == (b >> 1) || endLink.ContainsKey(b)) continue;
                    if (Flat(EndPos(a), EndPos(b)) <= joinDistance) AddEdge(a, b, 0f);
                }
            }
        }

        private void AddEdge(int a, int b, float c)
        {
            adj[a].Add(new Edge { to = b, cost = c });
            adj[b].Add(new Edge { to = a, cost = c });
        }

        private static float Flat(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        private Vector3 EndPos(int node)
        {
            var pts = roads[node >> 1].Points;
            return (node & 1) == 0 ? pts[0].pos : pts[pts.Count - 1].pos;
        }

        private float EndDist(int node) => (node & 1) == 0 ? 0f : roads[node >> 1].TotalLength;
        private bool IsHubNode(int n) => n >= hubBase && n < hubBase + hubs.Count;

        private static float HubRadius(RoadIntersection h) =>
            h.shape != IntersectionShape.Junction ? h.outerRadius : 8f;

        // ------------------------------------------------------------------
        //  Accrochage d'un point monde au réseau
        // ------------------------------------------------------------------
        public Snap Nearest(Vector3 p, float maxDistance)
        {
            var s = new Snap { road = -1, hub = -1 };

            float bestHub = float.MaxValue;
            for (int h = 0; h < hubs.Count; h++)
            {
                Vector3 c = hubs[h].transform.position;
                float d = Flat(p, c);
                if (d <= HubRadius(hubs[h]) && d < bestHub)
                {
                    bestHub = d;
                    s.valid = true; s.hub = h; s.point = c; s.lateral = d;
                }
            }
            if (s.valid) return s;

            float best = maxDistance;
            for (int ri = 0; ri < roads.Count; ri++)
            {
                var pts = roads[ri].Points;
                for (int i = 0; i < pts.Count - 1; i++)
                {
                    Vector3 a = pts[i].pos, b = pts[i + 1].pos;
                    float abx = b.x - a.x, abz = b.z - a.z;
                    float len2 = abx * abx + abz * abz;
                    float u = len2 > 1e-6f ? Mathf.Clamp01(((p.x - a.x) * abx + (p.z - a.z) * abz) / len2) : 0f;
                    Vector3 q = Vector3.Lerp(a, b, u);
                    float d = Flat(p, q);
                    if (d < best)
                    {
                        best = d;
                        s.valid = true; s.road = ri; s.hub = -1;
                        s.dist = Mathf.Lerp(pts[i].dist, pts[i + 1].dist, u);
                        s.point = q; s.lateral = d;
                    }
                }
            }
            return s;
        }

        // ------------------------------------------------------------------
        //  Plus court chemin (Dijkstra) + géométrie le long des splines
        // ------------------------------------------------------------------
        public bool TryFindPath(Vector3 from, Vector3 to, List<Vector3> result, out float length, float maxSnap = 80f)
        {
            result.Clear();
            length = 0f;
            if (roads.Count == 0) return false;

            Snap a = Nearest(from, maxSnap), b = Nearest(to, maxSnap);
            if (!a.valid || !b.valid) return false;

            int total = NodeCount, S = total, G = total + 1;
            var dist = new float[total + 2];
            var prev = new int[total + 2];
            var done = new bool[total + 2];
            for (int i = 0; i < dist.Length; i++) { dist[i] = float.MaxValue; prev[i] = -1; }

            var toGoal = new Dictionary<int, float>();
            if (b.hub >= 0) toGoal[hubBase + b.hub] = 0f;
            else
            {
                float L = roads[b.road].TotalLength;
                toGoal[2 * b.road] = b.dist;
                toGoal[2 * b.road + 1] = L - b.dist;
            }

            void Relax(int u, int v, float c)
            {
                float nd = dist[u] + c;
                if (nd < dist[v]) { dist[v] = nd; prev[v] = u; }
            }

            dist[S] = 0f;
            if (a.hub >= 0) Relax(S, hubBase + a.hub, 0f);
            else
            {
                float L = roads[a.road].TotalLength;
                Relax(S, 2 * a.road, a.dist);
                Relax(S, 2 * a.road + 1, L - a.dist);
                if (b.hub < 0 && b.road == a.road) Relax(S, G, Mathf.Abs(a.dist - b.dist));
            }
            done[S] = true;

            while (true)
            {
                int u = -1; float bd = float.MaxValue;
                for (int i = 0; i < dist.Length; i++)
                    if (!done[i] && dist[i] < bd) { bd = dist[i]; u = i; }
                if (u < 0 || u == G) break;
                done[u] = true;
                if (u < total)
                {
                    foreach (var e in adj[u]) Relax(u, e.to, e.cost);
                    if (toGoal.TryGetValue(u, out float cg)) Relax(u, G, cg);
                }
            }
            if (dist[G] == float.MaxValue) return false;

            var seq = new List<int>();
            for (int n = G; n != -1; n = prev[n]) seq.Add(n);
            seq.Reverse();

            BuildGeometry(seq, S, G, a, b, result);
            length = dist[G];
            return result.Count >= 2;
        }

        // ------------------------------------------------------------------
        private static void AddPt(List<Vector3> res, Vector3 p)
        {
            if (res.Count == 0 || Vector3.Distance(res[res.Count - 1], p) > 0.05f) res.Add(p);
        }

        private void AppendRoad(int ri, float d0, float d1, List<Vector3> res)
        {
            Road r = roads[ri];
            int n = Mathf.Max(1, Mathf.CeilToInt(Mathf.Abs(d1 - d0) / 2f));
            for (int i = 0; i <= n; i++) AddPt(res, r.SampleAt(Mathf.Lerp(d0, d1, i / (float)n)).pos);
        }

        private void BuildGeometry(List<int> seq, int S, int G, Snap a, Snap b, List<Vector3> res)
        {
            for (int i = 0; i < seq.Count; i++)
            {
                int u = seq[i];
                if (u == G) continue;

                if (u == S)
                {
                    int v = seq[i + 1];
                    if (v == G) AppendRoad(a.road, a.dist, b.dist, res);
                    else if (IsHubNode(v)) AddPt(res, a.point);
                    else AppendRoad(a.road, a.dist, EndDist(v), res);
                    continue;
                }

                if (IsHubNode(u))
                {
                    int p = seq[i - 1], nx = seq[i + 1];
                    IntersectionArm inArm = null, outArm = null;
                    if (p != S && endLink.TryGetValue(p, out var k1)) inArm = k1.Value;
                    if (nx != G && endLink.TryGetValue(nx, out var k2)) outArm = k2.Value;
                    AddTransit(hubs[u - hubBase], inArm, outArm, res);
                    if (nx == G) AddPt(res, b.point);
                    continue;
                }

                // Extrémité de route
                int w = seq[i + 1];
                if (IsHubNode(w)) continue;                       // traité au niveau de l'intersection
                if (w == G) AppendRoad(b.road, EndDist(u), b.dist, res);
                else if ((u >> 1) == (w >> 1)) AppendRoad(u >> 1, EndDist(u), EndDist(w), res);
                else { AddPt(res, EndPos(u)); AddPt(res, EndPos(w)); }
            }
        }

        /// <summary>Traversée d'une intersection : arc sur l'anneau (rond-point) ou courbe de Bézier (jonction).</summary>
        private static void AddTransit(RoadIntersection h, IntersectionArm A, IntersectionArm B, List<Vector3> res)
        {
            Vector3 c = h.transform.position + Vector3.up * h.heightOffset;
            Vector3 Far(IntersectionArm arm) { Vector3 f = h.ArmFar(arm); f.y = c.y; return f; }

            if (A == null && B == null) { AddPt(res, c); return; }
            if (B == null) { AddPt(res, Far(A)); AddPt(res, c); return; }
            if (A == null) { AddPt(res, c); AddPt(res, Far(B)); return; }
            if (A == B) { AddPt(res, Far(A)); AddPt(res, c); AddPt(res, Far(A)); return; }

            Vector3 dA = h.ArmDir(A), dB = h.ArmDir(B);
            AddPt(res, Far(A));

            if (h.shape == IntersectionShape.Roundabout)
            {
                float r = Mathf.Max(0.5f, h.outerRadius - h.ringWidth * 0.5f);
                float aA = Mathf.Atan2(dA.x, dA.z) * Mathf.Rad2Deg;
                float aB = Mathf.Atan2(dB.x, dB.z) * Mathf.Rad2Deg;
                float delta = Mathf.DeltaAngle(aA, aB);
                int steps = Mathf.Max(1, Mathf.CeilToInt(Mathf.Abs(delta) / 8f));
                AddPt(res, c + dA * r);
                for (int s = 1; s <= steps; s++)
                {
                    float ang = (aA + delta * s / steps) * Mathf.Deg2Rad;
                    AddPt(res, c + new Vector3(Mathf.Sin(ang), 0f, Mathf.Cos(ang)) * r);
                }
            }
            else
            {
                float k = Mathf.Min(10f, 0.5f * Mathf.Min(h.EffectiveLength(A), h.EffectiveLength(B)));
                Vector3 p0 = c + dA * k, p2 = c + dB * k;
                const int S = 8;
                for (int s = 0; s <= S; s++)
                {
                    float t = s / (float)S, u = 1f - t;
                    AddPt(res, u * u * p0 + 2f * u * t * c + t * t * p2);
                }
            }
            AddPt(res, Far(B));
        }
    }
}
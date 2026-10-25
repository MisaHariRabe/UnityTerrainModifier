using System;
using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace RoadTools
{
    public enum BridgeStyle
    {
        Slab,   // tablier plein, dessous plat (pont à poutres / dalle)
        Arch    // dessous en arcs entre les piliers (ou un seul grand arc sans pilier)
    }

    public enum PillarShape { Box, Cylinder }

    /// <summary>Une portion de la route (0..1 en distance) transformée en pont.</summary>
    [Serializable]
    public class BridgeSpan
    {
        public string name = "Pont";
        public bool enabled = true;

        [Range(0f, 1f)] public float start = 0.3f;
        [Range(0f, 1f)] public float end = 0.7f;

        [Tooltip("Slab : dessous plat. Arch : dessous en arcs.")]
        public BridgeStyle style = BridgeStyle.Slab;
        [Min(0.1f), Tooltip("Épaisseur du tablier (m).")]
        public float deckThickness = 1f;
        [Min(0f), Tooltip("Débord du tablier de chaque côté, au-delà de la chaussée et des trottoirs (m).")]
        public float overhang = 0.4f;
        [Min(0f), Tooltip("Style Arch : hauteur de l'arc, en plus de l'épaisseur du tablier (m).")]
        public float archRise = 3f;

        public bool parapets = true;
        [Min(0.1f)] public float parapetHeight = 1f;
        [Min(0.05f)] public float parapetThickness = 0.25f;

        [Tooltip("Décoché : pont sans pilier (le tablier est juste posé entre ses deux extrémités).")]
        public bool pillars = true;
        public PillarShape pillarShape = PillarShape.Box;
        [Min(2f), Tooltip("Distance visée entre deux piliers (m). Ils sont répartis régulièrement sur le pont.")]
        public float pillarSpacing = 20f;
        [Min(0.2f), Tooltip("Largeur d'un pilier, dans le sens de la largeur de la route (m).")]
        public float pillarWidth = 1.5f;
        [Min(0.2f), Tooltip("Épaisseur d'un pilier, dans le sens de la route (m).")]
        public float pillarDepth = 1.5f;
        [Range(1, 6), Tooltip("Nombre de colonnes côte à côte sous la largeur du tablier.")]
        public int pillarColumns = 1;
        [Min(0f), Tooltip("Enfoncement du pilier dans le sol (m).")]
        public float pillarFoundation = 0.5f;
        [Min(1f), Tooltip("Hauteur utilisée si aucun terrain n'est trouvé sous le pilier (m).")]
        public float pillarFallbackHeight = 8f;
    }

    /// <summary>
    /// Transforme une ou plusieurs portions d'une Route en pont : tablier, parapets et piliers (optionnels).
    /// Sous le pont, le terrain redevient naturel (la route ne le remplit plus), avec un raccord doux aux deux extrémités.
    /// Les piliers descendent jusqu'au terrain d'origine.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Road))]
    [AddComponentMenu("Roads/Road Bridge")]
    public class RoadBridge : MonoBehaviour, ITerrainModifier
    {
        [HideInInspector] public List<BridgeSpan> spans = new List<BridgeSpan> { new BridgeSpan() };

        [Header("Rendu")]
        [Tooltip("Matériau du tablier, des parapets et des piliers (béton...). Vide : celui des trottoirs de la route.")]
        public Material bridgeMaterial;
        public bool generateCollider = true;

        [Header("Terrain sous le pont")]
        [Tooltip("Redonne au terrain sa forme d'origine sous le pont (sinon la route comble la vallée).")]
        public bool restoreTerrain = true;
        [Min(0f), Tooltip("Largeur de terrain naturel gardée de chaque côté du tablier (m).")]
        public float terrainMargin = 3f;
        [Min(0.1f), Tooltip("Largeur du raccord sur les côtés (m).")]
        public float terrainFade = 8f;
        [Min(0.1f), Tooltip("Longueur du raccord aux deux extrémités du pont, là où la route retrouve le terrain (m).")]
        public float endFade = 8f;
        [Tooltip("Ordre de la couche terrain : doit être plus grand que celui de la route et des intersections.")]
        public int terrainLayerOrder = 50;

        private Road road;
        private bool rebuildScheduled, rebuildQueued;

        // ---- ITerrainModifier ----
        public Behaviour Owner => this;
        public int LayerOrder => terrainLayerOrder;
        public string Label => gameObject.name + " (pont)";
        public ModifierOperation Operation => ModifierOperation.Blend;

        // ---------- Cycle de vie ----------
        private void OnEnable()
        {
            road = GetComponent<Road>();
            Road.Rebuilt += OnRoadRebuilt;
            TerrainModifierManager.Register(this);
            RequestRebuild();
        }

        private void OnDisable()
        {
            Road.Rebuilt -= OnRoadRebuilt;
            TerrainModifierManager.Unregister(this);
            RoadUtil.RemoveChild(transform, "_BridgeMesh");
        }

        private void LateUpdate()
        {
            if (!rebuildQueued) return;
            rebuildQueued = false;
            Rebuild();
        }

        private void OnValidate() => RequestRebuild();

        private void OnRoadRebuilt(Road r)
        {
            if (r == road) RequestRebuild();
        }

        public void RequestRebuild()
        {
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                if (rebuildScheduled) return;
                rebuildScheduled = true;
                EditorApplication.delayCall += DelayedRebuild;
                return;
            }
#endif
            rebuildQueued = true;
        }

#if UNITY_EDITOR
        private void DelayedRebuild()
        {
            rebuildScheduled = false;
            if (this == null || !isActiveAndEnabled) return;
            Rebuild();
        }
#endif

        // ------------------------------------------------------------------
        //  Maillage
        // ------------------------------------------------------------------
        public void Rebuild()
        {
            rebuildScheduled = false;
            if (!isActiveAndEnabled) return;
            if (road == null) road = GetComponent<Road>();

            var mb = new MeshBuilder();
            float total = road.TotalLength;
            bool any = false;
            if (total > 1f)
                foreach (var span in spans)
                    if (span != null && span.enabled && BuildSpan(mb, span, total)) any = true;

            if (!any || mb.verts.Count == 0)
            {
                RoadUtil.RemoveChild(transform, "_BridgeMesh");
            }
            else
            {
                Mesh mesh = mb.ToMesh("BridgeMesh", transform.worldToLocalMatrix);
                Material m = bridgeMaterial != null ? bridgeMaterial : road.sidewalkMaterial;
                RoadUtil.SetMesh(transform, "_BridgeMesh", mesh, m, m, generateCollider);
            }

            TerrainModifierManager.RequestUpdate();
        }

        /// <summary>Épaisseur du tablier à x mètres du début du pont (style Arch : plus épais aux piliers, plus fin au milieu d'une travée).</summary>
        private static float ThicknessAt(BridgeSpan s, float x, float bay)
        {
            float th = s.deckThickness;
            if (s.style == BridgeStyle.Arch && bay > 0.5f)
            {
                float t = Mathf.Repeat(x, bay) / bay;
                float u = 2f * t - 1f;
                th += s.archRise * (1f - Mathf.Sqrt(Mathf.Max(0f, 1f - u * u)));   // demi-ellipse
            }
            return th;
        }

        private bool BuildSpan(MeshBuilder mb, BridgeSpan s, float total)
        {
            float d0 = Mathf.Clamp01(s.start) * total, d1 = Mathf.Clamp01(s.end) * total;
            float len = d1 - d0;
            if (len < 2f) return false;

            // Piliers répartis régulièrement ; une travée = distance entre deux appuis (ou le pont entier sans pilier).
            int piers = s.pillars ? Mathf.Max(0, Mathf.RoundToInt(len / Mathf.Max(2f, s.pillarSpacing)) - 1) : 0;
            float bay = len / (piers + 1);

            float step = s.style == BridgeStyle.Arch ? 0.75f : Mathf.Clamp(road.sampleSpacing, 0.5f, 2f);
            int n = Mathf.Max(2, Mathf.CeilToInt(len / step) + 1);

            var tl = new Vector3[n]; var tr = new Vector3[n];
            var bl = new Vector3[n]; var br = new Vector3[n];
            var right = new Vector3[n]; var dist = new float[n];

            for (int i = 0; i < n; i++)
            {
                float d = d0 + len * i / (n - 1f);
                RoadPoint p = road.SampleAt(d);
                RoadProfile pr = road.GetProfileAt(d);

                float wl = pr.left + (pr.swLeft ? pr.swLeftWidth : 0f) + s.overhang;
                float wr = pr.right + (pr.swRight ? pr.swRightWidth : 0f) + s.overhang;
                float topY = p.pos.y + road.heightOffset - 0.03f;       // juste sous la chaussée (pas de z-fighting)
                float th = ThicknessAt(s, d - d0, bay);

                Vector3 c = new Vector3(p.pos.x, topY, p.pos.z);
                tl[i] = c - p.right * wl;
                tr[i] = c + p.right * wr;
                bl[i] = tl[i] + Vector3.down * th;
                br[i] = tr[i] + Vector3.down * th;
                right[i] = p.right;
                dist[i] = d;
            }

            float tile = Mathf.Max(0.5f, road.uvTileLength);

            for (int i = 0; i < n - 1; i++)
            {
                int j = i + 1;
                float v0 = dist[i] / tile, v1 = dist[j] / tile;
                Vector3 rAvg = (right[i] + right[j]).normalized;

                Strip(mb, tl[i], tr[i], tl[j], tr[j], v0, v1, Vector3.up);        // dessus (visible sur les débords)
                Strip(mb, bl[i], br[i], bl[j], br[j], v0, v1, Vector3.down);      // dessous
                Strip(mb, tl[i], bl[i], tl[j], bl[j], v0, v1, -rAvg);             // flanc gauche
                Strip(mb, tr[i], br[i], tr[j], br[j], v0, v1, rAvg);              // flanc droit
            }

            // Culées : faces verticales aux deux extrémités.
            Vector3 tan0 = (tl[1] - tl[0]); tan0.y = 0f; tan0.Normalize();
            Vector3 tanN = (tl[n - 1] - tl[n - 2]); tanN.y = 0f; tanN.Normalize();
            Cap(mb, tl[0], tr[0], br[0], bl[0], -tan0);
            Cap(mb, tl[n - 1], tr[n - 1], br[n - 1], bl[n - 1], tanN);

            if (s.parapets) BuildParapets(mb, s, tl, tr, right, tile);
            for (int k = 1; k <= piers; k++) BuildPillarRow(mb, s, d0 + bay * k, d0, bay, tile);
            return true;
        }

        private static void Strip(MeshBuilder mb, Vector3 a0, Vector3 b0, Vector3 a1, Vector3 b1, float v0, float v1, Vector3 expected)
        {
            int i0 = mb.Add(a0, new Vector2(0f, v0)), i1 = mb.Add(b0, new Vector2(1f, v0));
            int i2 = mb.Add(b1, new Vector2(1f, v1)), i3 = mb.Add(a1, new Vector2(0f, v1));
            mb.Quad(mb.side, i0, i1, i2, i3, expected);
        }

        private static void Cap(MeshBuilder mb, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 expected)
        {
            int i0 = mb.Add(a, new Vector2(0, 1)), i1 = mb.Add(b, new Vector2(1, 1));
            int i2 = mb.Add(c, new Vector2(1, 0)), i3 = mb.Add(d, new Vector2(0, 0));
            mb.Quad(mb.side, i0, i1, i2, i3, expected);
        }

        private void BuildParapets(MeshBuilder mb, BridgeSpan s, Vector3[] tl, Vector3[] tr, Vector3[] right, float tile)
        {
            int n = tl.Length;
            var edgeL = new List<Vector3>(n); var outL = new List<Vector3>(n);
            var edgeR = new List<Vector3>(n); var outR = new List<Vector3>(n);
            var widths = new List<float>(n);

            for (int i = 0; i < n; i++)
            {
                // Le muret est posé sur le bord extérieur du tablier, légèrement rentré.
                edgeL.Add(tl[i] + right[i] * s.parapetThickness); outL.Add(-right[i]);
                edgeR.Add(tr[i] - right[i] * s.parapetThickness); outR.Add(right[i]);
                widths.Add(s.parapetThickness);
            }
            RoadMeshUtil.AddCurbRibbon(mb, edgeL, outL, widths, s.parapetHeight, tile);
            RoadMeshUtil.AddCurbRibbon(mb, edgeR, outR, widths, s.parapetHeight, tile);
        }

        // ---------- Piliers ----------
        private void BuildPillarRow(MeshBuilder mb, BridgeSpan s, float d, float d0, float bay, float tile)
        {
            RoadPoint p = road.SampleAt(d);
            RoadProfile pr = road.GetProfileAt(d);

            float topY = p.pos.y + road.heightOffset - 0.03f;
            float deckBottom = topY - ThicknessAt(s, d - d0, bay);

            float wl = pr.left + (pr.swLeft ? pr.swLeftWidth : 0f) + s.overhang;
            float wr = pr.right + (pr.swRight ? pr.swRightWidth : 0f) + s.overhang;
            float avail = Mathf.Max(0f, Mathf.Min(wl, wr) - s.pillarWidth * 0.5f - 0.2f);

            int cols = Mathf.Max(1, s.pillarColumns);
            for (int j = 0; j < cols; j++)
            {
                float lat = cols == 1 ? 0f : Mathf.Lerp(-avail, avail, j / (cols - 1f));
                Vector3 center = new Vector3(p.pos.x, 0f, p.pos.z) + p.right * lat;

                // Sol d'ORIGINE : c'est la forme que le terrain aura sous le pont une fois restauré.
                float ground = TerrainModifierManager.TrySampleBaseHeight(center.x, center.z, out float gy)
                    ? gy : deckBottom - s.pillarFallbackHeight;

                float bottom = ground - s.pillarFoundation;
                if (bottom >= deckBottom - 0.1f) continue;   // le sol est au niveau du tablier : pas de pilier

                if (s.pillarShape == PillarShape.Box) AddBox(mb, center, p.right, p.tangent, s.pillarWidth * 0.5f, s.pillarDepth * 0.5f, bottom, deckBottom + 0.05f, tile);
                else AddCylinder(mb, center, p.right, p.tangent, s.pillarWidth * 0.5f, s.pillarDepth * 0.5f, bottom, deckBottom + 0.05f, tile);
            }
        }

        private static void AddBox(MeshBuilder mb, Vector3 c, Vector3 right, Vector3 tan, float hx, float hz, float y0, float y1, float tile)
        {
            Vector3 r = right * hx, t = tan * hz;
            Vector3[] ring = { c - r - t, c + r - t, c + r + t, c - r + t };
            float v = (y1 - y0) / tile;

            for (int k = 0; k < 4; k++)
            {
                Vector3 a = ring[k], b = ring[(k + 1) % 4];
                Vector3 outward = (a + b) * 0.5f - c; outward.y = 0f;
                Vector3 a0 = new Vector3(a.x, y0, a.z), b0 = new Vector3(b.x, y0, b.z);
                Vector3 a1 = new Vector3(a.x, y1, a.z), b1 = new Vector3(b.x, y1, b.z);
                int i0 = mb.Add(a0, new Vector2(0, 0)), i1 = mb.Add(b0, new Vector2(1, 0));
                int i2 = mb.Add(b1, new Vector2(1, v)), i3 = mb.Add(a1, new Vector2(0, v));
                mb.Quad(mb.side, i0, i1, i2, i3, outward);
            }
        }

        private static void AddCylinder(MeshBuilder mb, Vector3 c, Vector3 right, Vector3 tan, float hx, float hz, float y0, float y1, float tile)
        {
            const int seg = 16;
            float v = (y1 - y0) / tile;
            Vector3 prev = default; bool has = false;

            for (int k = 0; k <= seg; k++)
            {
                float a = k / (float)seg * Mathf.PI * 2f;
                Vector3 p = c + right * (Mathf.Cos(a) * hx) + tan * (Mathf.Sin(a) * hz);
                if (has)
                {
                    Vector3 outward = (p + prev) * 0.5f - c; outward.y = 0f;
                    int i0 = mb.Add(new Vector3(prev.x, y0, prev.z), new Vector2(0, 0)), i1 = mb.Add(new Vector3(p.x, y0, p.z), new Vector2(1, 0));
                    int i2 = mb.Add(new Vector3(p.x, y1, p.z), new Vector2(1, v)), i3 = mb.Add(new Vector3(prev.x, y1, prev.z), new Vector2(0, v));
                    mb.Quad(mb.side, i0, i1, i2, i3, outward);
                }
                prev = p; has = true;
            }
        }

        // ------------------------------------------------------------------
        //  Terrain : redonne sa forme d'origine sous le pont
        // ------------------------------------------------------------------
        public void Apply(TerrainHeightContext c)
        {
            if (!restoreTerrain) return;
            if (road == null) road = GetComponent<Road>();
            if (road == null) return;

            float total = road.TotalLength;
            if (total < 1f) return;

            foreach (var span in spans)
                if (span != null && span.enabled) ApplySpan(c, span, total);
        }

        private void ApplySpan(TerrainHeightContext c, BridgeSpan s, float total)
        {
            float d0 = Mathf.Clamp01(s.start) * total, d1 = Mathf.Clamp01(s.end) * total;
            float len = d1 - d0;
            if (len < 2f) return;

            int n = Mathf.Max(2, Mathf.CeilToInt(len / 2f) + 1);
            var pos = new Vector2[n];
            var half = new float[n];
            Vector2 min = new Vector2(float.MaxValue, float.MaxValue), max = new Vector2(float.MinValue, float.MinValue);
            float maxHalf = 0f;

            for (int i = 0; i < n; i++)
            {
                RoadPoint p = road.SampleAt(d0 + len * i / (n - 1f));
                RoadProfile pr = road.GetProfileAt(p.dist);
                pos[i] = new Vector2(p.pos.x, p.pos.z);
                half[i] = Mathf.Max(pr.left + (pr.swLeft ? pr.swLeftWidth : 0f), pr.right + (pr.swRight ? pr.swRightWidth : 0f)) + s.overhang;
                min = Vector2.Min(min, pos[i]); max = Vector2.Max(max, pos[i]);
                maxHalf = Mathf.Max(maxHalf, half[i]);
            }

            float reach = maxHalf + terrainMargin + terrainFade;
            if (!c.TryGetRegion(min - Vector2.one * reach, max + Vector2.one * reach, out int rx0, out int rz0, out int rw, out int rh))
                return;

            // Segment le plus proche de chaque pixel (rastérisation par segment, comme le node spline).
            int cells = rw * rh;
            var bestSq = new float[cells];
            var bestK = new int[cells];
            var bestF = new float[cells];
            for (int i = 0; i < cells; i++) bestSq[i] = float.MaxValue;

            for (int k = 0; k < n - 1; k++)
            {
                Vector2 A = pos[k], B = pos[k + 1];
                float w = Mathf.Max(half[k], half[k + 1]) + terrainMargin + terrainFade;
                if (!c.TryGetRegion(Vector2.Min(A, B) - Vector2.one * w, Vector2.Max(A, B) + Vector2.one * w,
                                    out int sx0, out int sz0, out int sw, out int sh))
                    continue;

                Vector2 ab = B - A;
                float abLenSq = ab.sqrMagnitude;
                for (int z = sz0; z < sz0 + sh; z++)
                {
                    for (int x = sx0; x < sx0 + sw; x++)
                    {
                        Vector2 P = c.PixelToWorld(x, z);
                        float f = abLenSq > 1e-8f ? Mathf.Clamp01(Vector2.Dot(P - A, ab) / abLenSq) : 0f;
                        float dSq = (P - (A + ab * f)).sqrMagnitude;
                        int idx = (z - rz0) * rw + (x - rx0);
                        if (dSq < bestSq[idx]) { bestSq[idx] = dSq; bestK[idx] = k; bestF[idx] = f; }
                    }
                }
            }

            float invY = 1f / c.size.y;
            float segLen = len / (n - 1f);
            float[,] delta = new float[rh, rw];

            for (int idx = 0; idx < cells; idx++)
            {
                if (bestSq[idx] == float.MaxValue) continue;

                int k = bestK[idx];
                float f = bestF[idx];
                float halfW = Mathf.Lerp(half[k], half[k + 1], f);
                float d = Mathf.Sqrt(bestSq[idx]);

                // Côtés : terrain naturel jusqu'à (demi-largeur + marge), puis fondu.
                float flat = halfW + terrainMargin;
                float side = d <= flat ? 1f : 1f - ModifierMath.Smooth01((d - flat) / Mathf.Max(0.01f, terrainFade));
                if (side <= 0f) continue;

                // Extrémités : on garde le remblai de la route, qui se fond peu à peu dans le terrain naturel.
                float along = (k + f) * segLen;
                float ends = Mathf.Min(ModifierMath.Smooth01(along / Mathf.Max(0.01f, endFade)),
                                       ModifierMath.Smooth01((len - along) / Mathf.Max(0.01f, endFade)));
                float amount = side * ends;
                if (amount <= 0f) continue;

                int lx = idx % rw, lz = idx / rw;
                Vector2 world = c.PixelToWorld(rx0 + lx, rz0 + lz);
                if (!TerrainModifierManager.TrySampleBaseHeight(world.x, world.y, out float by)) continue;

                float baseNorm = Mathf.Clamp01((by - c.origin.y) * invY);
                float current = c.heights[rz0 + lz, rx0 + lx];
                delta[lz, lx] = (baseNorm - current) * amount;
            }

            c.ApplyDelta(delta, rx0, rz0, 1);
        }
    }
}
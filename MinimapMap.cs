using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace RoadTools
{
    /// <summary>
    /// Génère la texture de minimap EN COULEUR à partir du monde : relief ombré, routes (principales / secondaires),
    /// intersections, ponts, bâtiments, eau. Aucune texture du terrain n'est lue : toutes les couleurs viennent d'ici.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("Roads/Minimap Map")]
    public class MinimapMap : MonoBehaviour
    {
        [Header("Emprise du monde")]
        [Tooltip("Coché : l'emprise est l'union de tous les terrains actifs.")]
        public bool autoBounds = true;
        public Vector2 manualMin = Vector2.zero;
        public Vector2 manualSize = new Vector2(1000f, 1000f);
        [Min(0f), Tooltip("Marge ajoutée autour de l'emprise (m).")]
        public float margin = 0f;

        [Header("Qualité")]
        [Range(256, 4096)] public int resolution = 1024;
        [Range(1, 3), Tooltip("Sur-échantillonnage (bords plus nets pour les routes et l'eau).")]
        public int supersample = 2;

        [Header("Couleurs")]
        [ColorUsage(false)] public Color backgroundColor = new Color(0.04f, 0.05f, 0.07f);
        [ColorUsage(false), Tooltip("Couleur du terrain dans les zones basses.")]
        public Color terrainLowColor = new Color(0.16f, 0.22f, 0.15f);
        [ColorUsage(false), Tooltip("Couleur du terrain dans les zones hautes.")]
        public Color terrainHighColor = new Color(0.62f, 0.68f, 0.50f);
        [ColorUsage(false)] public Color primaryRoadColor = new Color(1f, 0.85f, 0.35f);
        [ColorUsage(false)] public Color secondaryRoadColor = new Color(0.92f, 0.92f, 0.92f);
        [ColorUsage(false)] public Color sidewalkColor = new Color(0.62f, 0.62f, 0.60f);
        [ColorUsage(false)] public Color bridgeColor = new Color(0.75f, 0.70f, 0.65f);
        [ColorUsage(false)] public Color waterColor = new Color(0.25f, 0.50f, 0.85f);
        [ColorUsage(false)] public Color buildingColor = new Color(0.45f, 0.40f, 0.38f);
        [ColorUsage(false)] public Color otherColor = new Color(0.35f, 0.35f, 0.40f);

        [Header("Routes principales / secondaires")]
        [Min(0f), Tooltip("Largeur TOTALE de la chaussée (gauche + droite, en m, au point le plus large de la route). " +
                          "Largeur >= cette valeur : route principale. Sinon : route secondaire. " +
                          "Une intersection est principale si l'une de ses branches l'est.")]
        public float primaryRoadWidth = 8f;

        [Header("Relief")]
        [Range(0f, 360f), Tooltip("Direction de la lumière (0 = nord, sens horaire).")]
        public float lightAzimuth = 315f;
        [Range(5f, 85f)] public float lightAltitude = 45f;
        [Tooltip("Force de l'ombrage des pentes.")]
        public float reliefGain = 4f;
        [Range(0f, 1f), Tooltip("Mélange bas -> haut du dégradé de terrain selon l'altitude.")]
        public float heightContrast = 0.35f;

        [Header("Contenu")]
        public bool includeRoads = true;
        public bool includeIntersections = true;
        public bool includeBridges = true;
        [Tooltip("Dessine l'emprise des TerrainFootprint.")]
        public bool includeBuildings = true;
        [Tooltip("Dessine l'eau : seuls les pixels où la surface de l'eau est AU-DESSUS du terrain sont colorés, " +
                 "donc l'eau épouse le relief (rivage, lac) au lieu de garder la forme du plan d'eau.")]
        public bool includeWater = true;
        [Tooltip("Layer(s) des objets d'eau (MeshRenderer, mesh lisible). Nothing = aucun.")]
        public LayerMask waterLayers = 0;
        [Tooltip("Tolérance verticale (m) : > 0 étend l'eau un peu vers la berge, < 0 la rétrécit.")]
        public float waterShoreTolerance = 0f;
        [Tooltip("Autres objets (MeshRenderer) à dessiner, par layer. Nothing = aucun.")]
        public LayerMask otherLayers = 0;

        [Header("Texture")]
        [Tooltip("PNG exporté depuis l'inspecteur. Utilisé en jeu à la place d'un nouveau bake.")]
        public Texture2D bakedAsset;
        public bool preferBakedAsset = true;
        [Tooltip("Génère la minimap au lancement du jeu si aucun asset n'est utilisé.")]
        public bool bakeOnPlay = true;

        [SerializeField, HideInInspector] private Vector2 boundsMin;
        [SerializeField, HideInInspector] private Vector2 boundsSize = Vector2.one;
        [SerializeField, HideInInspector] private bool hasBounds;

        private Texture2D runtime;
        public event Action Baked;

        public Texture2D Texture => runtime != null ? runtime : bakedAsset;
        public Texture2D RuntimeTexture => runtime;
        public bool HasMap => hasBounds && Texture != null;
        public Vector2 BoundsMin => boundsMin;
        public Vector2 BoundsSize => boundsSize;
        /// <summary>Incrémenté à chaque bake : la vue s'en sert pour savoir quand relire la texture.</summary>
        public int Version { get; private set; }

        // ---------- Conversions ----------
        public Vector2 WorldToUV(Vector3 w) => new Vector2((w.x - boundsMin.x) / boundsSize.x, (w.z - boundsMin.y) / boundsSize.y);
        public Vector3 UVToWorld(Vector2 uv, float y = 0f) => new Vector3(boundsMin.x + uv.x * boundsSize.x, y, boundsMin.y + uv.y * boundsSize.y);

        // ---------- Cycle de vie ----------
        private IEnumerator Start()
        {
            if (!Application.isPlaying) yield break;
            if (preferBakedAsset && bakedAsset != null)
            {
                if (runtime != null) { RoadUtil.Destroy(runtime); runtime = null; }
                Version++;
                Baked?.Invoke();
                yield break;
            }
            if (!bakeOnPlay) yield break;
            yield return null;   // laisse routes, intersections et terrain se construire
            yield return null;
            Bake();
        }

        private void OnDestroy()
        {
            if (runtime != null) RoadUtil.Destroy(runtime);
        }

        // ------------------------------------------------------------------
        //  Classement principale / secondaire
        // ------------------------------------------------------------------
        /// <summary>Largeur totale (gauche + droite) de la route à son point le plus large.</summary>
        public static float RoadWidth(Road r)
        {
            float total = r.TotalLength;
            if (total < 0.01f) return 0f;
            int n = Mathf.Clamp(Mathf.CeilToInt(total / 5f) + 1, 2, 128);
            float best = 0f;
            for (int i = 0; i < n; i++)
            {
                RoadProfile p = r.GetProfileAt(total * i / (n - 1f));
                best = Mathf.Max(best, p.left + p.right);
            }
            return best;
        }

        /// <summary>Largeur de la branche la plus large de l'intersection.</summary>
        public static float IntersectionWidth(RoadIntersection h)
        {
            float best = 0f;
            foreach (var a in h.arms)
            {
                if (a == null) continue;
                h.GetArmWidths(a, out float wl, out float wr);
                best = Mathf.Max(best, wl + wr);
            }
            return best;
        }

        private struct MeshItem
        {
            public Mesh mesh;
            public Matrix4x4 m;
            public Color32[] sub;
            public bool primary;
        }

        // ------------------------------------------------------------------
        //  Bake
        // ------------------------------------------------------------------
        public bool Bake(bool refreshTerrain = true)
        {
            Terrain[] terrains = Terrain.activeTerrains;

            Vector2 min, size;
            if (autoBounds && terrains.Length > 0)
            {
                Vector2 mx = new Vector2(float.MinValue, float.MinValue);
                min = new Vector2(float.MaxValue, float.MaxValue);
                foreach (var t in terrains)
                {
                    if (t.terrainData == null) continue;
                    Vector3 o = t.transform.position, s = t.terrainData.size;
                    min = Vector2.Min(min, new Vector2(o.x, o.z));
                    mx = Vector2.Max(mx, new Vector2(o.x + s.x, o.z + s.z));
                }
                size = mx - min;
            }
            else { min = manualMin; size = manualSize; }

            min -= Vector2.one * margin;
            size += Vector2.one * margin * 2f;
            if (size.x < 1f || size.y < 1f)
            {
                Debug.LogWarning("[Minimap] Emprise invalide : aucun terrain actif et emprise manuelle vide.", this);
                return false;
            }

            if (refreshTerrain)
                foreach (var m in TerrainModifierManager.Managers)
                    if (m != null && m.isActiveAndEnabled) m.ApplyModifications();

            float ppm = resolution / Mathf.Max(size.x, size.y);
            int W = Mathf.Max(8, Mathf.RoundToInt(size.x * ppm));
            int H = Mathf.Max(8, Mathf.RoundToInt(size.y * ppm));
            int ss = supersample;
            while (ss > 1 && (long)W * ss * H * ss > 16000000L) ss--;
            int HW = W * ss, HH = H * ss;

            // 1) Terrain (résolution normale) -> couleurs + hauteurs (les hauteurs servent à l'eau).
            var low = new Color32[W * H];
            var has = new bool[W * H];
            var heights = new float[W * H];
            BakeTerrain(terrains, min, size, W, H, low, has, heights);

            // 2) Grille haute résolution initialisée avec le terrain.
            Color32 bg = backgroundColor;
            var hi = new Color32[HW * HH];
            for (int y = 0; y < HH; y++)
            {
                int row = (y / ss) * W;
                for (int x = 0; x < HW; x++)
                {
                    int i = row + x / ss;
                    hi[y * HW + x] = has[i] ? low[i] : bg;
                }
            }

            var canvas = new Canvas(hi, HW, HH, min, size, heights, W, H, ss);

            // 3) Du dessous vers le dessus : autres objets, eau, bâtiments, routes, intersections, ponts.
            if (otherLayers.value != 0) DrawOthers(canvas);
            if (includeWater && waterLayers.value != 0) DrawWater(canvas);
            if (includeBuildings) DrawFootprints(canvas);

            // Routes + intersections : les secondaires d'abord, les principales par-dessus (croisements propres).
            var items = new List<MeshItem>();
            Color32 sw = sidewalkColor;
            if (includeRoads)
            {
                foreach (var r in FindObjectsByType<Road>(FindObjectsSortMode.None))
                {
                    if (r == null || !r.isActiveAndEnabled) continue;
                    if (!GetGenerated(r, "_RoadMesh", () => r.Rebuild(), out Mesh mesh, out Matrix4x4 m)) continue;
                    bool primary = RoadWidth(r) >= primaryRoadWidth - 0.001f;
                    Color32 rc = primary ? primaryRoadColor : secondaryRoadColor;
                    items.Add(new MeshItem { mesh = mesh, m = m, sub = new[] { rc, sw }, primary = primary });
                }
            }
            if (includeIntersections)
            {
                foreach (var h in FindObjectsByType<RoadIntersection>(FindObjectsSortMode.None))
                {
                    if (h == null || !h.isActiveAndEnabled) continue;
                    if (!GetGenerated(h, "_IntersectionMesh", () => h.Rebuild(false), out Mesh mesh, out Matrix4x4 m)) continue;
                    bool primary = IntersectionWidth(h) >= primaryRoadWidth - 0.001f;
                    Color32 rc = primary ? primaryRoadColor : secondaryRoadColor;
                    items.Add(new MeshItem { mesh = mesh, m = m, sub = new[] { rc, sw }, primary = primary });
                }
            }
            foreach (var it in items) if (!it.primary) canvas.DrawMesh(it.mesh, it.m, it.sub);
            foreach (var it in items) if (it.primary) canvas.DrawMesh(it.mesh, it.m, it.sub);

            if (includeBridges)
            {
                var sub = new[] { (Color32)bridgeColor };
                foreach (var b in FindObjectsByType<RoadBridge>(FindObjectsSortMode.None))
                {
                    if (b == null || !b.isActiveAndEnabled) continue;
                    if (GetGenerated(b, "_BridgeMesh", () => b.Rebuild(), out Mesh mesh, out Matrix4x4 m)) canvas.DrawMesh(mesh, m, sub);
                }
            }

            // 4) Réduction (moyenne ss x ss par canal) -> texture.
            var cols = new Color32[W * H];
            int cnt = ss * ss;
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    int sr = 0, sg = 0, sb = 0;
                    for (int dy = 0; dy < ss; dy++)
                    {
                        int row = (y * ss + dy) * HW + x * ss;
                        for (int dx = 0; dx < ss; dx++)
                        {
                            Color32 c = hi[row + dx];
                            sr += c.r; sg += c.g; sb += c.b;
                        }
                    }
                    cols[y * W + x] = new Color32((byte)(sr / cnt), (byte)(sg / cnt), (byte)(sb / cnt), 255);
                }
            }

            if (runtime != null) RoadUtil.Destroy(runtime);
            runtime = new Texture2D(W, H, TextureFormat.RGBA32, false)
            {
                name = "MinimapTexture",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave
            };
            runtime.SetPixels32(cols);
            runtime.Apply(false, false);

            boundsMin = min;
            boundsSize = size;
            hasBounds = true;
            Version++;
#if UNITY_EDITOR
            if (!Application.isPlaying) EditorUtility.SetDirty(this);
#endif
            Baked?.Invoke();
            return true;
        }

        // ------------------------------------------------------------------
        //  Terrain : hillshade coloré (dégradé bas -> haut)
        // ------------------------------------------------------------------
        private static bool TryHeight(Terrain[] ts, float wx, float wz, out float h)
        {
            foreach (var t in ts)
            {
                var d = t.terrainData;
                if (d == null) continue;
                Vector3 o = t.transform.position, s = d.size;
                float u = (wx - o.x) / s.x, v = (wz - o.z) / s.z;
                if (u < 0f || v < 0f || u > 1f || v > 1f) continue;
                h = o.y + d.GetInterpolatedHeight(u, v);
                return true;
            }
            h = 0f;
            return false;
        }

        private void BakeTerrain(Terrain[] ts, Vector2 min, Vector2 size, int W, int H, Color32[] colors, bool[] has, float[] heightsOut)
        {
            for (int i = 0; i < heightsOut.Length; i++) heightsOut[i] = float.NaN;

            int GW = W + 2, GH = H + 2;
            var hg = new float[GW * GH];
            var ok = new bool[GW * GH];
            float cx = size.x / W, cz = size.y / H;
            float mn = float.MaxValue, mx = float.MinValue;

            for (int gy = 0; gy < GH; gy++)
                for (int gx = 0; gx < GW; gx++)
                {
                    float wx = min.x + (gx - 1 + 0.5f) * cx, wz = min.y + (gy - 1 + 0.5f) * cz;
                    if (!TryHeight(ts, wx, wz, out float h)) continue;
                    int i = gy * GW + gx;
                    hg[i] = h; ok[i] = true;
                    if (h < mn) mn = h;
                    if (h > mx) mx = h;
                }
            if (mn > mx) return;   // aucun terrain
            float range = Mathf.Max(0.01f, mx - mn);

            float az = lightAzimuth * Mathf.Deg2Rad, alt = lightAltitude * Mathf.Deg2Rad;
            Vector3 L = new Vector3(Mathf.Sin(az) * Mathf.Cos(alt), Mathf.Sin(alt), Mathf.Cos(az) * Mathf.Cos(alt));
            float flat = L.y;

            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int gi = (y + 1) * GW + (x + 1);
                    if (!ok[gi]) continue;
                    float h = hg[gi];
                    float hl = ok[gi - 1] ? hg[gi - 1] : h, hr = ok[gi + 1] ? hg[gi + 1] : h;
                    float hd = ok[gi - GW] ? hg[gi - GW] : h, hu = ok[gi + GW] ? hg[gi + GW] : h;
                    float dx = (hr - hl) / (2f * cx), dz = (hu - hd) / (2f * cz);
                    Vector3 n = new Vector3(-dx, 1f, -dz).normalized;
                    float shade = Mathf.Max(0f, Vector3.Dot(n, L));
                    float hn = (h - mn) / range;
                    float t = 0.5f + (hn - 0.5f) * heightContrast + (shade - flat) * reliefGain;
                    colors[y * W + x] = Color.Lerp(terrainLowColor, terrainHighColor, Mathf.Clamp01(t));
                    has[y * W + x] = true;
                    heightsOut[y * W + x] = h;
                }
        }

        // ------------------------------------------------------------------
        //  Objets
        // ------------------------------------------------------------------
        private static bool GetGenerated(Component owner, string childName, Action rebuild, out Mesh mesh, out Matrix4x4 m)
        {
            mesh = null; m = Matrix4x4.identity;
            Transform t = owner.transform.Find(childName);
            if (t == null) { rebuild?.Invoke(); t = owner.transform.Find(childName); }
            if (t == null) return false;
            var mf = t.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) return false;
            mesh = mf.sharedMesh;
            m = t.localToWorldMatrix;
            return true;
        }

        private void DrawFootprints(Canvas c)
        {
            Color32 col = buildingColor;
            foreach (var fp in FindObjectsByType<TerrainFootprint>(FindObjectsSortMode.None))
            {
                if (fp == null || !fp.isActiveAndEnabled) continue;
                Vector2 h = fp.size * 0.5f;
                c.Quad(fp.FromFrame(new Vector2(-h.x, -h.y)), fp.FromFrame(new Vector2(h.x, -h.y)),
                       fp.FromFrame(new Vector2(h.x, h.y)), fp.FromFrame(new Vector2(-h.x, h.y)), col);
            }
        }

        private void DrawOthers(Canvas c)
        {
            var sub = new[] { (Color32)otherColor };
            Color32 col = otherColor;
            foreach (var mr in FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
            {
                if (mr == null || !mr.enabled || !mr.gameObject.activeInHierarchy) continue;
                if ((otherLayers.value & (1 << mr.gameObject.layer)) == 0) continue;
                if (mr.GetComponentInParent<Road>() != null || mr.GetComponentInParent<RoadIntersection>() != null
                    || mr.GetComponentInParent<RoadBridge>() != null) continue;
                if (includeBuildings && mr.GetComponentInParent<TerrainFootprint>() != null) continue;

                var mf = mr.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null && mf.sharedMesh.isReadable)
                    c.DrawMesh(mf.sharedMesh, mr.transform.localToWorldMatrix, sub);
                else
                {
                    Bounds b = mr.bounds;
                    c.Quad(new Vector3(b.min.x, 0, b.min.z), new Vector3(b.max.x, 0, b.min.z),
                           new Vector3(b.max.x, 0, b.max.z), new Vector3(b.min.x, 0, b.max.z), col);
                }
            }
        }

        /// <summary>
        /// Eau : chaque pixel du plan d'eau n'est coloré que si la surface de l'eau (hauteur interpolée sur le mesh)
        /// est au-dessus du terrain à cet endroit. Un plan d'eau caché sous le terrain disparaît donc, et le rivage
        /// suit la forme du terrain.
        /// </summary>
        private void DrawWater(Canvas c)
        {
            Color32 col = waterColor;
            foreach (var mr in FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
            {
                if (mr == null || !mr.enabled || !mr.gameObject.activeInHierarchy) continue;
                if ((waterLayers.value & (1 << mr.gameObject.layer)) == 0) continue;

                var mf = mr.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null && mf.sharedMesh.isReadable)
                    c.DrawWaterMesh(mf.sharedMesh, mr.transform.localToWorldMatrix, col, waterShoreTolerance);
                else
                {
                    // Mesh illisible : on retombe sur la boîte du renderer, à plat à sa hauteur maximale.
                    Bounds b = mr.bounds;
                    c.WaterQuad(new Vector3(b.min.x, b.max.y, b.min.z), new Vector3(b.max.x, b.max.y, b.min.z),
                                new Vector3(b.max.x, b.max.y, b.max.z), new Vector3(b.min.x, b.max.y, b.max.z), col, waterShoreTolerance);
                }
            }
        }

        // ------------------------------------------------------------------
        //  Rasterisation (plan XZ -> pixels)
        // ------------------------------------------------------------------
        private sealed class Canvas
        {
            private readonly Color32[] data;
            private readonly int W, H;
            private readonly float minX, minZ, invX, invZ;

            // Hauteurs du terrain (résolution normale) pour masquer l'eau sous le relief.
            private readonly float[] th;
            private readonly int lw, lh, ss;

            public Canvas(Color32[] data, int w, int h, Vector2 min, Vector2 size, float[] terrainHeights, int lowW, int lowH, int ss)
            {
                this.data = data; W = w; H = h;
                minX = min.x; minZ = min.y;
                invX = w / size.x; invZ = h / size.y;
                th = terrainHeights; lw = lowW; lh = lowH; this.ss = ss;
            }

            private Vector2 P(Vector3 w) => new Vector2((w.x - minX) * invX, (w.z - minZ) * invZ);

            /// <summary>Hauteur du terrain au centre d'un pixel haute résolution (qx, qy en pixels). -inf si pas de terrain.</summary>
            private float TerrainY(float qx, float qy)
            {
                if (th == null) return float.NegativeInfinity;
                float fx = qx / ss - 0.5f, fy = qy / ss - 0.5f;
                int x0 = Mathf.FloorToInt(fx), y0 = Mathf.FloorToInt(fy);
                float tx = fx - x0, ty = fy - y0;
                int xa = Mathf.Clamp(x0, 0, lw - 1), xb = Mathf.Clamp(x0 + 1, 0, lw - 1);
                int ya = Mathf.Clamp(y0, 0, lh - 1), yb = Mathf.Clamp(y0 + 1, 0, lh - 1);
                float h00 = th[ya * lw + xa], h10 = th[ya * lw + xb], h01 = th[yb * lw + xa], h11 = th[yb * lw + xb];

                if (float.IsNaN(h00) || float.IsNaN(h10) || float.IsNaN(h01) || float.IsNaN(h11))
                {
                    // Bord du terrain : cellule la plus proche, sinon "pas de terrain" (l'eau s'affiche).
                    int xi = Mathf.Clamp(Mathf.FloorToInt(qx / ss), 0, lw - 1);
                    int yi = Mathf.Clamp(Mathf.FloorToInt(qy / ss), 0, lh - 1);
                    float v = th[yi * lw + xi];
                    return float.IsNaN(v) ? float.NegativeInfinity : v;
                }
                return Mathf.Lerp(Mathf.Lerp(h00, h10, tx), Mathf.Lerp(h01, h11, tx), ty);
            }

            public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color32 g)
            {
                Vector2 pa = P(a), pb = P(b), pc = P(c), pd = P(d);
                Tri(pa, pb, pc, g);
                Tri(pa, pc, pd, g);
            }

            public void WaterQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color32 g, float tol)
            {
                Vector2 pa = P(a), pb = P(b), pc = P(c), pd = P(d);
                WaterTri(pa, pb, pc, a.y, b.y, c.y, g, tol);
                WaterTri(pa, pc, pd, a.y, c.y, d.y, g, tol);
            }

            public void DrawMesh(Mesh mesh, Matrix4x4 m, Color32[] subColor)
            {
                Vector3[] v = mesh.vertices;
                var p = new Vector2[v.Length];
                for (int i = 0; i < v.Length; i++) p[i] = P(m.MultiplyPoint3x4(v[i]));

                for (int s = 0; s < mesh.subMeshCount; s++)
                {
                    int[] t = mesh.GetTriangles(s);
                    Color32 g = subColor[Mathf.Min(s, subColor.Length - 1)];
                    for (int i = 0; i + 2 < t.Length; i += 3) Tri(p[t[i]], p[t[i + 1]], p[t[i + 2]], g);
                }
            }

            public void DrawWaterMesh(Mesh mesh, Matrix4x4 m, Color32 g, float tol)
            {
                Vector3[] v = mesh.vertices;
                var p = new Vector2[v.Length];
                var y = new float[v.Length];
                for (int i = 0; i < v.Length; i++)
                {
                    Vector3 w = m.MultiplyPoint3x4(v[i]);
                    p[i] = P(w);
                    y[i] = w.y;
                }
                int[] t = mesh.triangles;
                for (int i = 0; i + 2 < t.Length; i += 3)
                    WaterTri(p[t[i]], p[t[i + 1]], p[t[i + 2]], y[t[i]], y[t[i + 1]], y[t[i + 2]], g, tol);
            }

            public void Tri(Vector2 a, Vector2 b, Vector2 c, Color32 g)
            {
                float area = (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
                if (Mathf.Abs(area) < 1e-5f) return;   // face verticale (projection dégénérée)

                int x0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x))));
                int x1 = Mathf.Min(W - 1, Mathf.CeilToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x))));
                int y0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.y, Mathf.Min(b.y, c.y))));
                int y1 = Mathf.Min(H - 1, Mathf.CeilToInt(Mathf.Max(a.y, Mathf.Max(b.y, c.y))));
                float inv = 1f / area;

                for (int y = y0; y <= y1; y++)
                {
                    float py = y + 0.5f;
                    for (int x = x0; x <= x1; x++)
                    {
                        float qx = x + 0.5f;
                        float w0 = ((c.x - b.x) * (py - b.y) - (c.y - b.y) * (qx - b.x)) * inv;
                        float w1 = ((a.x - c.x) * (py - c.y) - (a.y - c.y) * (qx - c.x)) * inv;
                        float w2 = ((b.x - a.x) * (py - a.y) - (b.y - a.y) * (qx - a.x)) * inv;
                        if (w0 >= -1e-4f && w1 >= -1e-4f && w2 >= -1e-4f) data[y * W + x] = g;
                    }
                }
            }

            /// <summary>Comme Tri, mais le pixel n'est peint que si la hauteur d'eau interpolée dépasse celle du terrain.</summary>
            public void WaterTri(Vector2 a, Vector2 b, Vector2 c, float ya, float yb, float yc, Color32 g, float tol)
            {
                float area = (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
                if (Mathf.Abs(area) < 1e-5f) return;

                int x0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x))));
                int x1 = Mathf.Min(W - 1, Mathf.CeilToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x))));
                int y0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.y, Mathf.Min(b.y, c.y))));
                int y1 = Mathf.Min(H - 1, Mathf.CeilToInt(Mathf.Max(a.y, Mathf.Max(b.y, c.y))));
                float inv = 1f / area;

                for (int y = y0; y <= y1; y++)
                {
                    float py = y + 0.5f;
                    for (int x = x0; x <= x1; x++)
                    {
                        float qx = x + 0.5f;
                        float w0 = ((c.x - b.x) * (py - b.y) - (c.y - b.y) * (qx - b.x)) * inv;
                        float w1 = ((a.x - c.x) * (py - c.y) - (a.y - c.y) * (qx - c.x)) * inv;
                        float w2 = ((b.x - a.x) * (py - a.y) - (b.y - a.y) * (qx - a.x)) * inv;
                        if (w0 < -1e-4f || w1 < -1e-4f || w2 < -1e-4f) continue;

                        float waterY = w0 * ya + w1 * yb + w2 * yc;
                        if (TerrainY(qx, py) <= waterY + tol) data[y * W + x] = g;
                    }
                }
            }
        }

        private void OnDrawGizmosSelected()
        {
            if (!hasBounds) return;
            float y = Terrain.activeTerrain != null ? Terrain.activeTerrain.transform.position.y : 0f;
            Gizmos.color = new Color(1f, 1f, 1f, 0.6f);
            Gizmos.DrawWireCube(new Vector3(boundsMin.x + boundsSize.x * 0.5f, y, boundsMin.y + boundsSize.y * 0.5f),
                                new Vector3(boundsSize.x, 1f, boundsSize.y));
        }
    }
}
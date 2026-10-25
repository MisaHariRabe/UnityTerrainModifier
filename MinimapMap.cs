using System;
using System.Collections;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace RoadTools
{
    /// <summary>
    /// Génère la texture de minimap (niveaux de gris) à partir du monde : relief ombré, routes, intersections, ponts, bâtiments.
    /// Aucune couleur ni texture du terrain n'est lue.
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
        [Range(1, 3), Tooltip("Sur-échantillonnage des routes (bords plus nets).")]
        public int supersample = 2;

        [Header("Niveaux de gris (0 = noir, 1 = blanc)")]
        [Range(0f, 1f)] public float background = 0.06f;
        [Range(0f, 1f)] public float terrainDark = 0.14f;
        [Range(0f, 1f)] public float terrainLight = 0.50f;
        [Range(0f, 1f)] public float roadGray = 0.88f;
        [Range(0f, 1f)] public float sidewalkGray = 0.68f;
        [Range(0f, 1f)] public float bridgeGray = 0.78f;
        [Range(0f, 1f)] public float buildingGray = 0.34f;
        [Range(0f, 1f)] public float otherGray = 0.28f;

        [Header("Relief")]
        [Range(0f, 360f), Tooltip("Direction de la lumière (0 = nord, sens horaire).")]
        public float lightAzimuth = 315f;
        [Range(5f, 85f)] public float lightAltitude = 45f;
        [Tooltip("Force de l'ombrage des pentes.")]
        public float reliefGain = 4f;
        [Range(0f, 1f), Tooltip("Éclaircit les zones hautes, assombrit les zones basses.")]
        public float heightContrast = 0.35f;

        [Header("Contenu")]
        public bool includeRoads = true;
        public bool includeIntersections = true;
        public bool includeBridges = true;
        [Tooltip("Dessine l'emprise des TerrainFootprint.")]
        public bool includeBuildings = true;
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

            // 1) Terrain (résolution normale) -> gris.
            var low = new float[W * H];
            var has = new bool[W * H];
            BakeTerrain(terrains, min, size, W, H, low, has);

            // 2) Grille haute résolution initialisée avec le terrain.
            var hi = new float[HW * HH];
            for (int y = 0; y < HH; y++)
            {
                int row = (y / ss) * W;
                for (int x = 0; x < HW; x++)
                {
                    int i = row + x / ss;
                    hi[y * HW + x] = has[i] ? low[i] : background;
                }
            }

            var canvas = new Canvas(hi, HW, HH, min, size);

            // 3) Du dessous vers le dessus : autres objets, bâtiments, routes, intersections, ponts.
            if (otherLayers.value != 0) DrawOthers(canvas);
            if (includeBuildings) DrawFootprints(canvas);

            if (includeRoads)
            {
                var sub = new[] { roadGray, sidewalkGray };
                foreach (var r in FindObjectsByType<Road>(FindObjectsSortMode.None))
                {
                    if (r == null || !r.isActiveAndEnabled) continue;
                    if (GetGenerated(r, "_RoadMesh", () => r.Rebuild(), out Mesh mesh, out Matrix4x4 m)) canvas.DrawMesh(mesh, m, sub);
                }
            }
            if (includeIntersections)
            {
                var sub = new[] { roadGray, sidewalkGray };
                foreach (var h in FindObjectsByType<RoadIntersection>(FindObjectsSortMode.None))
                {
                    if (h == null || !h.isActiveAndEnabled) continue;
                    if (GetGenerated(h, "_IntersectionMesh", () => h.Rebuild(false), out Mesh mesh, out Matrix4x4 m)) canvas.DrawMesh(mesh, m, sub);
                }
            }
            if (includeBridges)
            {
                var sub = new[] { bridgeGray };
                foreach (var b in FindObjectsByType<RoadBridge>(FindObjectsSortMode.None))
                {
                    if (b == null || !b.isActiveAndEnabled) continue;
                    if (GetGenerated(b, "_BridgeMesh", () => b.Rebuild(), out Mesh mesh, out Matrix4x4 m)) canvas.DrawMesh(mesh, m, sub);
                }
            }

            // 4) Réduction (moyenne ss x ss) -> texture.
            var cols = new Color32[W * H];
            float inv = 1f / (ss * ss);
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    float sum = 0f;
                    for (int dy = 0; dy < ss; dy++)
                    {
                        int row = (y * ss + dy) * HW + x * ss;
                        for (int dx = 0; dx < ss; dx++) sum += hi[row + dx];
                    }
                    byte g = (byte)Mathf.Clamp(Mathf.RoundToInt(sum * inv * 255f), 0, 255);
                    cols[y * W + x] = new Color32(g, g, g, 255);
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
#if UNITY_EDITOR
            if (!Application.isPlaying) EditorUtility.SetDirty(this);
#endif
            Baked?.Invoke();
            return true;
        }

        // ------------------------------------------------------------------
        //  Terrain : hillshade en niveaux de gris
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

        private void BakeTerrain(Terrain[] ts, Vector2 min, Vector2 size, int W, int H, float[] gray, bool[] has)
        {
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
                    gray[y * W + x] = Mathf.Lerp(terrainDark, terrainLight, Mathf.Clamp01(t));
                    has[y * W + x] = true;
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
            foreach (var fp in FindObjectsByType<TerrainFootprint>(FindObjectsSortMode.None))
            {
                if (fp == null || !fp.isActiveAndEnabled) continue;
                Vector2 h = fp.size * 0.5f;
                c.Quad(fp.FromFrame(new Vector2(-h.x, -h.y)), fp.FromFrame(new Vector2(h.x, -h.y)),
                       fp.FromFrame(new Vector2(h.x, h.y)), fp.FromFrame(new Vector2(-h.x, h.y)), buildingGray);
            }
        }

        private void DrawOthers(Canvas c)
        {
            var sub = new[] { otherGray };
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
                           new Vector3(b.max.x, 0, b.max.z), new Vector3(b.min.x, 0, b.max.z), otherGray);
                }
            }
        }

        // ------------------------------------------------------------------
        //  Rasterisation (plan XZ -> pixels)
        // ------------------------------------------------------------------
        private sealed class Canvas
        {
            private readonly float[] data;
            private readonly int W, H;
            private readonly float minX, minZ, invX, invZ;

            public Canvas(float[] data, int w, int h, Vector2 min, Vector2 size)
            {
                this.data = data; W = w; H = h;
                minX = min.x; minZ = min.y;
                invX = w / size.x; invZ = h / size.y;
            }

            private Vector2 P(Vector3 w) => new Vector2((w.x - minX) * invX, (w.z - minZ) * invZ);

            public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float g)
            {
                Vector2 pa = P(a), pb = P(b), pc = P(c), pd = P(d);
                Tri(pa, pb, pc, g);
                Tri(pa, pc, pd, g);
            }

            public void DrawMesh(Mesh mesh, Matrix4x4 m, float[] subGray)
            {
                Vector3[] v = mesh.vertices;
                var p = new Vector2[v.Length];
                for (int i = 0; i < v.Length; i++) p[i] = P(m.MultiplyPoint3x4(v[i]));

                for (int s = 0; s < mesh.subMeshCount; s++)
                {
                    int[] t = mesh.GetTriangles(s);
                    float g = subGray[Mathf.Min(s, subGray.Length - 1)];
                    for (int i = 0; i + 2 < t.Length; i += 3) Tri(p[t[i]], p[t[i + 1]], p[t[i + 2]], g);
                }
            }

            public void Tri(Vector2 a, Vector2 b, Vector2 c, float g)
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
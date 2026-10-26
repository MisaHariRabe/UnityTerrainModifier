using System.Collections.Generic;
using UnityEngine;

namespace RoadTools
{
    public enum MinimapAnchor { TopLeft, TopRight, BottomLeft, BottomRight }

    /// <summary>
    /// Affichage de la minimap (IMGUI, aucune dépendance UI) : suit une cible, zoom molette,
    /// clic gauche = destination, clic droit = effacer. L'itinéraire suit les splines des routes et les intersections.
    ///
    /// La vue est composée sur le CPU dans une petite texture (rotation, recadrage, coins arrondis, bordure, itinéraire),
    /// ce qui permet un vrai recadrage à la rotation et une forme arrondie sans shader.
    /// </summary>
    [AddComponentMenu("Roads/Minimap View")]
    public class MinimapView : MonoBehaviour
    {
        [Header("Sources")]
        public MinimapMap map;
        [Tooltip("Le joueur / véhicule. Vide : la minimap est centrée sur cet objet.")]
        public Transform target;

        [Header("Destination")]
        public Transform destination;
        public bool useManualDestination;
        public Vector3 manualDestination;

        [Header("Affichage")]
        public MinimapAnchor anchor = MinimapAnchor.TopRight;
        public Vector2 margin = new Vector2(16f, 16f);
        [Min(64f)] public float size = 220f;
        [Min(10f), Tooltip("Largeur du monde visible dans la minimap (m).")]
        public float viewMeters = 250f;
        public Vector2 zoomRange = new Vector2(40f, 1500f);
        [Tooltip("La carte tourne : la direction de la cible pointe toujours vers le haut. La carte est recadrée (rien ne dépasse de la forme).")]
        public bool rotateWithTarget = false;
        public bool mouseZoom = true;
        public bool clickToSetDestination = true;
        [Range(0f, 1f)] public float opacity = 1f;
        public Color backColor = new Color(0f, 0f, 0f, 0.8f);

        [Header("Forme et bordure")]
        [Range(0f, 1f), Tooltip("Arrondi des coins : 0 = carré, 1 = cercle parfait.")]
        public float roundness = 0f;
        public bool showBorder = true;
        [Range(1f, 8f), Tooltip("Épaisseur de la bordure (pixels).")]
        public float borderThickness = 2f;
        public Color borderColor = new Color(1f, 1f, 1f, 0.85f);

        [Header("Icônes")]
        [Tooltip("Icône du joueur (doit pointer vers le HAUT). Vide : flèche par défaut.")]
        public Texture2D playerIcon;
        [Min(4f)] public float playerIconSize = 16f;
        [Tooltip("Teinte appliquée à l'icône du joueur.")]
        public Color markerColor = Color.white;
        public bool showNorth = true;
        [Tooltip("Icône du nord. Vide : un « N ».")]
        public Texture2D northIcon;
        [Min(4f)] public float northIconSize = 18f;

        [Header("Ennemis (optionnel)")]
        public bool showEnemies = false;
        [Tooltip("Layers des ennemis. Chaque ennemi doit avoir un Collider sur ce layer.")]
        public LayerMask enemyLayers = 0;
        [Tooltip("Icône des ennemis. Vide : un point.")]
        public Texture2D enemyIcon;
        [Min(3f)] public float enemyIconSize = 10f;
        public Color enemyColor = new Color(1f, 0.25f, 0.2f, 1f);
        [Min(0.05f), Tooltip("Intervalle de recherche des ennemis (s).")]
        public float enemyRefreshInterval = 0.25f;
        [Range(8, 512), Tooltip("Nombre max d'ennemis détectés à la fois.")]
        public int maxEnemies = 128;

        [Header("Infos")]
        [Tooltip("Affiche la distance entre la cible et la destination.")]
        public bool showDistance = true;

        [Header("Itinéraire")]
        public Color routeColor = new Color(1f, 0.55f, 0.1f, 1f);
        [Min(1f), Tooltip("Épaisseur du tracé sur la carte (m).")]
        public float routeWidth = 5f;
        public bool autoRecompute = true;
        [Min(0.1f)] public float recomputeInterval = 0.75f;
        [Min(0.5f), Tooltip("Distance parcourue avant recalcul (m).")]
        public float recomputeDistance = 4f;
        [Tooltip("Distance max entre un point et la route la plus proche (m).")]
        public float maxSnapDistance = 80f;
        [Tooltip("Deux bouts de route libres plus proches que ça sont considérés comme raccordés (m).")]
        public float joinDistance = 2f;

        // ---- État ----
        public IReadOnlyList<Vector3> Route => route;
        public float RouteLength { get; private set; }
        public bool HasRoute { get; private set; }

        private readonly List<Vector3> route = new List<Vector3>();
        private readonly List<Vector3> tmp = new List<Vector3>();
        private RoadNetwork network;
        private float networkTime;
        private bool networkDirty = true;
        private float lastCompute = -999f;
        private Vector3 lastFrom, lastDest;
        private bool computedOnce;
        private int routeVersion;

        // Ennemis
        private readonly List<Transform> enemies = new List<Transform>();
        private readonly HashSet<Transform> seen = new HashSet<Transform>();
        private Collider[] hitBuf;
        private float lastEnemyScan = -999f;

        // Rendu
        private Texture2D viewTex;
        private Color32[] viewBuf;
        private int viewS;
        private int lastKey = int.MinValue;
        private bool forceCompose = true;

        private Color32[] mapPixels;
        private int mapW, mapH;
        private Texture2D cachedMapTex;
        private int cachedMapVersion = -1;

        private Texture2D arrowTex, dotTex;
        private GUIStyle labelStyle;

        // Forme courante (mise à jour au début de OnGUI)
        private float curHalf, curRad;

        public Vector3 TargetPosition => target != null ? target.position : transform.position;
        private int ViewSize => Mathf.Max(64, Mathf.RoundToInt(size));

        // ------------------------------------------------------------------
        public bool TryGetDestination(out Vector3 p)
        {
            if (useManualDestination) { p = manualDestination; return true; }
            if (destination != null) { p = destination.position; return true; }
            p = default;
            return false;
        }

        public void SetDestination(Vector3 world, bool recompute = true)
        {
            manualDestination = world;
            useManualDestination = true;
            if (recompute) RecomputeRoute();
        }

        public void ClearDestination()
        {
            useManualDestination = false;
            destination = null;
            route.Clear();
            HasRoute = false;
            computedOnce = false;
            routeVersion++;
        }

        public void InvalidateNetwork() => networkDirty = true;

        /// <summary>Calcule le chemin cible -> destination en suivant routes et intersections.</summary>
        public bool RecomputeRoute()
        {
            route.Clear();
            HasRoute = false;
            RouteLength = 0f;
            routeVersion++;

            if (!TryGetDestination(out Vector3 dest)) return false;

            if (network == null || networkDirty || Time.realtimeSinceStartup - networkTime > 5f)
            {
                network = RoadNetwork.Build(joinDistance);
                networkDirty = false;
                networkTime = Time.realtimeSinceStartup;
            }

            Vector3 from = TargetPosition;
            if (network.TryFindPath(from, dest, tmp, out float len, maxSnapDistance))
            {
                route.Add(from);
                route.AddRange(tmp);
                route.Add(dest);
                HasRoute = true;
                RouteLength = len;
            }

            lastFrom = from; lastDest = dest;
            lastCompute = Time.unscaledTime;
            computedOnce = true;
            return HasRoute;
        }

        // ------------------------------------------------------------------
        private void OnEnable()
        {
            Road.Rebuilt += OnRoadRebuilt;
            if (map == null) map = FindFirstObjectByType<MinimapMap>();
            if (map != null) map.Baked += OnMapBaked;
        }

        private void OnDisable()
        {
            Road.Rebuilt -= OnRoadRebuilt;
            if (map != null) map.Baked -= OnMapBaked;
            Cleanup();
        }

        private void Cleanup()
        {
            if (viewTex != null) RoadUtil.Destroy(viewTex);
            if (arrowTex != null) RoadUtil.Destroy(arrowTex);
            if (dotTex != null) RoadUtil.Destroy(dotTex);
            viewTex = arrowTex = dotTex = null;
            viewBuf = null; mapPixels = null; cachedMapTex = null; cachedMapVersion = -1;
            lastKey = int.MinValue;
        }

        private void OnRoadRebuilt(Road r) => networkDirty = true;
        private void OnMapBaked() { forceCompose = true; }

        private void OnValidate()
        {
            size = Mathf.Max(64f, size);
            viewMeters = Mathf.Clamp(viewMeters, 10f, 100000f);
            forceCompose = true;
        }

        private void Update()
        {
            if (!Application.isPlaying) return;
            if (map == null) { map = FindFirstObjectByType<MinimapMap>(); if (map != null) map.Baked += OnMapBaked; }

            if (showEnemies && Time.unscaledTime - lastEnemyScan >= enemyRefreshInterval)
            {
                lastEnemyScan = Time.unscaledTime;
                RefreshEnemies();
            }
            else if (!showEnemies && enemies.Count > 0) enemies.Clear();

            if (!autoRecompute) return;

            if (!TryGetDestination(out Vector3 d))
            {
                if (route.Count > 0) { route.Clear(); HasRoute = false; routeVersion++; }
                return;
            }
            if (Time.unscaledTime - lastCompute < recomputeInterval) return;

            bool moved = (TargetPosition - lastFrom).sqrMagnitude > recomputeDistance * recomputeDistance;
            bool destChanged = (d - lastDest).sqrMagnitude > 0.25f;
            if (!computedOnce || moved || destChanged) RecomputeRoute();
        }

        // ------------------------------------------------------------------
        //  Ennemis : colliders sur les layers choisis, autour de la cible
        // ------------------------------------------------------------------
        private void RefreshEnemies()
        {
            enemies.Clear();
            if (enemyLayers.value == 0) return;
            if (hitBuf == null || hitBuf.Length != maxEnemies) hitBuf = new Collider[maxEnemies];

            int n = Physics.OverlapSphereNonAlloc(TargetPosition, viewMeters * 0.75f, hitBuf, enemyLayers, QueryTriggerInteraction.Collide);
            seen.Clear();
            for (int i = 0; i < n; i++)
            {
                Collider c = hitBuf[i];
                if (c == null) continue;
                Transform t = c.attachedRigidbody != null ? c.attachedRigidbody.transform : c.transform;
                if (target != null && (t == target || t.IsChildOf(target))) continue;
                if (seen.Add(t)) enemies.Add(t);
            }
        }

        // ------------------------------------------------------------------
        //  Forme arrondie (distance signée, coordonnées relatives au centre de la vue)
        // ------------------------------------------------------------------
        private float ShapeSdf(Vector2 p)
        {
            float qx = Mathf.Abs(p.x) - (curHalf - curRad);
            float qy = Mathf.Abs(p.y) - (curHalf - curRad);
            float ox = Mathf.Max(qx, 0f), oy = Mathf.Max(qy, 0f);
            return Mathf.Sqrt(ox * ox + oy * oy) + Mathf.Min(Mathf.Max(qx, qy), 0f) - curRad;
        }

        /// <summary>Distance du centre au bord de la forme dans une direction, moins une marge.</summary>
        private float EdgeDistance(Vector2 dir, float marginPx)
        {
            float lo = 0f, hi = curHalf * 1.5f;
            for (int i = 0; i < 14; i++)
            {
                float mid = (lo + hi) * 0.5f;
                if (ShapeSdf(dir * mid) <= -marginPx) lo = mid; else hi = mid;
            }
            return lo;
        }

        /// <summary>Ramène un point (repère local de la vue) à l'intérieur de la forme.</summary>
        private Vector2 ClampToShape(Vector2 p, float marginPx)
        {
            Vector2 c = new Vector2(curHalf, curHalf);
            Vector2 d = p - c;
            if (ShapeSdf(d) <= -marginPx) return p;
            float len = d.magnitude;
            if (len < 1e-4f) return p;
            Vector2 dir = d / len;
            return c + dir * Mathf.Min(len, EdgeDistance(dir, marginPx));
        }

        // ------------------------------------------------------------------
        //  Lecture CPU de la texture de la carte (copie par le GPU si elle n'est pas lisible)
        // ------------------------------------------------------------------
        private bool EnsureMapPixels()
        {
            Texture2D t = map != null ? map.Texture : null;
            if (t == null || !map.HasMap) return false;
            if (t == cachedMapTex && cachedMapVersion == map.Version && mapPixels != null) return true;

            mapW = t.width; mapH = t.height;
            if (t.isReadable) mapPixels = t.GetPixels32();
            else
            {
                RenderTexture rt = RenderTexture.GetTemporary(mapW, mapH, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(t, rt);
                RenderTexture prev = RenderTexture.active;
                RenderTexture.active = rt;
                var copy = new Texture2D(mapW, mapH, TextureFormat.RGBA32, false);
                copy.ReadPixels(new Rect(0, 0, mapW, mapH), 0, 0);
                mapPixels = copy.GetPixels32();
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                RoadUtil.Destroy(copy);
            }
            cachedMapTex = t;
            cachedMapVersion = map.Version;
            forceCompose = true;
            return mapPixels != null && mapPixels.Length == mapW * mapH;
        }

        private void EnsureViewTexture(int S)
        {
            if (viewTex != null && viewS == S) return;
            if (viewTex != null) RoadUtil.Destroy(viewTex);
            viewTex = new Texture2D(S, S, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave
            };
            viewBuf = new Color32[S * S];
            viewS = S;
            forceCompose = true;
        }

        // ------------------------------------------------------------------
        //  Composition de la vue (carte + itinéraire + masque arrondi + bordure)
        // ------------------------------------------------------------------
        private static Vector2 Rot(Vector2 v, float deg)
        {
            float a = deg * Mathf.Deg2Rad, c = Mathf.Cos(a), s = Mathf.Sin(a);
            return new Vector2(v.x * c - v.y * s, v.x * s + v.y * c);
        }

        private Vector2 W2V(Vector3 w, Vector3 centerW, float rot)
        {
            float mpp = viewMeters / curHalf * 0.5f;   // = viewMeters / S
            Vector2 off = new Vector2((w.x - centerW.x) / mpp, -(w.z - centerW.z) / mpp);
            off = Rot(off, rot);
            return new Vector2(curHalf + off.x, curHalf + off.y);
        }

        private static byte B(float v) => (byte)Mathf.Clamp(Mathf.RoundToInt(v * 255f), 0, 255);

        private void Compose(Vector3 centerW, float rot)
        {
            int S = viewS;
            Vector2 bmin = map.BoundsMin, bs = map.BoundsSize;
            float mpp = viewMeters / S;
            float a = -rot * Mathf.Deg2Rad;
            float ca = Mathf.Cos(a), sa = Mathf.Sin(a);
            Color back = backColor;
            float aM = opacity;
            float aB = back.a;

            // 1) Carte : chaque pixel de la vue est projeté dans le monde puis lu (bilinéaire) dans la texture.
            for (int sy = 0; sy < S; sy++)
            {
                float vy = sy + 0.5f - curHalf;
                int row = (S - 1 - sy) * S;   // Texture2D : ligne 0 en bas
                for (int sx = 0; sx < S; sx++)
                {
                    float vx = sx + 0.5f - curHalf;
                    float ux = vx * ca - vy * sa, uy = vx * sa + vy * ca;
                    float wx = centerW.x + ux * mpp, wz = centerW.z - uy * mpp;
                    float fu = (wx - bmin.x) / bs.x, fv = (wz - bmin.y) / bs.y;

                    float r, g, b, al;
                    if (fu < 0f || fv < 0f || fu > 1f || fv > 1f)
                    {
                        // Hors de la carte (possible en rotation) : fond uniquement => recadrage propre.
                        r = back.r; g = back.g; b = back.b; al = back.a;
                    }
                    else
                    {
                        float tx = fu * mapW - 0.5f, ty = fv * mapH - 0.5f;
                        int x0 = Mathf.FloorToInt(tx), y0 = Mathf.FloorToInt(ty);
                        float fx = tx - x0, fy = ty - y0;
                        int xa = Mathf.Clamp(x0, 0, mapW - 1), xb = Mathf.Clamp(x0 + 1, 0, mapW - 1);
                        int ya = Mathf.Clamp(y0, 0, mapH - 1), yb = Mathf.Clamp(y0 + 1, 0, mapH - 1);
                        Color32 c00 = mapPixels[ya * mapW + xa], c10 = mapPixels[ya * mapW + xb];
                        Color32 c01 = mapPixels[yb * mapW + xa], c11 = mapPixels[yb * mapW + xb];

                        float w00 = (1f - fx) * (1f - fy), w10 = fx * (1f - fy), w01 = (1f - fx) * fy, w11 = fx * fy;
                        float mr = (c00.r * w00 + c10.r * w10 + c01.r * w01 + c11.r * w11) / 255f;
                        float mg = (c00.g * w00 + c10.g * w10 + c01.g * w01 + c11.g * w11) / 255f;
                        float mb = (c00.b * w00 + c10.b * w10 + c01.b * w01 + c11.b * w11) / 255f;

                        // Carte (opacité aM) par-dessus le fond.
                        float aOut = aM + aB * (1f - aM);
                        if (aOut > 1e-5f)
                        {
                            float kb = aB * (1f - aM);
                            r = (mr * aM + back.r * kb) / aOut;
                            g = (mg * aM + back.g * kb) / aOut;
                            b = (mb * aM + back.b * kb) / aOut;
                        }
                        else { r = g = b = 0f; }
                        al = aOut;
                    }
                    viewBuf[row + sx] = new Color32(B(r), B(g), B(b), B(al));
                }
            }

            // 2) Itinéraire (tracé directement dans l'espace de la vue).
            if (route.Count >= 2) StampRoute(centerW, rot, S, mpp);

            // 3) Masque arrondi + bordure, uniquement près des bords (l'intérieur est inchangé).
            float t = showBorder ? borderThickness : 0f;
            float inner = curHalf - curRad - t - 1f;
            Color bc = borderColor;
            for (int sy = 0; sy < S; sy++)
            {
                float vy = sy + 0.5f - curHalf;
                int row = (S - 1 - sy) * S;
                bool rowInside = Mathf.Abs(vy) < inner;
                for (int sx = 0; sx < S; sx++)
                {
                    float vx = sx + 0.5f - curHalf;
                    if (rowInside && Mathf.Abs(vx) < inner) continue;

                    float d = ShapeSdf(new Vector2(vx, vy));
                    Color32 px = viewBuf[row + sx];
                    float r = px.r / 255f, g = px.g / 255f, b = px.b / 255f, al = px.a / 255f;

                    if (showBorder)
                    {
                        float m = Mathf.Clamp01(d + t + 0.5f);   // 1 dans la bande de bordure
                        r = Mathf.Lerp(r, bc.r, m); g = Mathf.Lerp(g, bc.g, m); b = Mathf.Lerp(b, bc.b, m);
                        al = Mathf.Lerp(al, bc.a, m);
                    }
                    al *= Mathf.Clamp01(0.5f - d);               // coins découpés, bord lissé
                    viewBuf[row + sx] = new Color32(B(r), B(g), B(b), B(al));
                }
            }

            viewTex.SetPixels32(viewBuf);
            viewTex.Apply(false, false);
        }

        private void StampRoute(Vector3 centerW, float rot, int S, float mpp)
        {
            float rPx = Mathf.Max(1.3f, routeWidth * 0.5f / mpp);
            Color rc = routeColor;
            Vector2 prev = W2V(route[0], centerW, rot);
            for (int i = 1; i < route.Count; i++)
            {
                Vector2 cur = W2V(route[i], centerW, rot);

                bool outside = (prev.x < -rPx && cur.x < -rPx) || (prev.x > S + rPx && cur.x > S + rPx)
                            || (prev.y < -rPx && cur.y < -rPx) || (prev.y > S + rPx && cur.y > S + rPx);
                if (!outside)
                {
                    float len = Vector2.Distance(prev, cur);
                    int steps = Mathf.Clamp(Mathf.CeilToInt(len / Mathf.Max(0.5f, rPx * 0.5f)), 1, 4000);
                    for (int s = 0; s <= steps; s++)
                    {
                        Vector2 p = Vector2.Lerp(prev, cur, s / (float)steps);
                        Disc(p.x, p.y, rPx, rc, S);
                    }
                }
                prev = cur;
            }
        }

        private void Disc(float cx, float cy, float r, Color c, int S)
        {
            int x0 = Mathf.Max(0, Mathf.FloorToInt(cx - r)), x1 = Mathf.Min(S - 1, Mathf.CeilToInt(cx + r));
            int y0 = Mathf.Max(0, Mathf.FloorToInt(cy - r)), y1 = Mathf.Min(S - 1, Mathf.CeilToInt(cy + r));
            float r2 = r * r;
            for (int y = y0; y <= y1; y++)
            {
                float dy = y + 0.5f - cy;
                int row = (S - 1 - y) * S;
                for (int x = x0; x <= x1; x++)
                {
                    float dx = x + 0.5f - cx;
                    if (dx * dx + dy * dy > r2) continue;
                    Color32 o = viewBuf[row + x];
                    float k = c.a;
                    viewBuf[row + x] = new Color32(
                        B(Mathf.Lerp(o.r / 255f, c.r, k)), B(Mathf.Lerp(o.g / 255f, c.g, k)), B(Mathf.Lerp(o.b / 255f, c.b, k)),
                        (byte)Mathf.Max(o.a, B(c.a)));
                }
            }
        }

        // ------------------------------------------------------------------
        //  IMGUI
        // ------------------------------------------------------------------
        private Rect GetRect(int S)
        {
            bool left = anchor == MinimapAnchor.TopLeft || anchor == MinimapAnchor.BottomLeft;
            bool top = anchor == MinimapAnchor.TopLeft || anchor == MinimapAnchor.TopRight;
            float x = left ? margin.x : Screen.width - S - margin.x;
            float y = top ? margin.y : Screen.height - S - margin.y;
            return new Rect(x, y, S, S);
        }

        private void EnsureResources()
        {
            if (arrowTex == null)
            {
                const int N = 32;
                arrowTex = new Texture2D(N, N, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
                var px = new Color32[N * N];
                for (int y = 0; y < N; y++)
                {
                    float half = (N - 1 - y) / (float)(N - 1) * 14f;       // pointe en haut
                    for (int x = 0; x < N; x++)
                        px[y * N + x] = Mathf.Abs(x - 15.5f) <= half ? new Color32(255, 255, 255, 255) : new Color32(0, 0, 0, 0);
                }
                arrowTex.SetPixels32(px); arrowTex.Apply();
            }
            if (dotTex == null)
            {
                const int N = 32;
                dotTex = new Texture2D(N, N, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
                var px = new Color32[N * N];
                for (int y = 0; y < N; y++)
                    for (int x = 0; x < N; x++)
                    {
                        float dx = x - 15.5f, dy = y - 15.5f;
                        px[y * N + x] = dx * dx + dy * dy <= 15f * 15f ? new Color32(255, 255, 255, 255) : new Color32(0, 0, 0, 0);
                    }
                dotTex.SetPixels32(px); dotTex.Apply();
            }
            if (labelStyle == null)
            {
                labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, alignment = TextAnchor.MiddleCenter };
                labelStyle.normal.textColor = Color.white;
            }
        }

        private static void DrawIcon(Texture tex, Vector2 center, float px, Color tint, float angle)
        {
            Matrix4x4 m0 = GUI.matrix;
            if (Mathf.Abs(angle) > 0.01f) GUIUtility.RotateAroundPivot(angle, center);
            Color old = GUI.color;
            GUI.color = tint;
            GUI.DrawTexture(new Rect(center.x - px * 0.5f, center.y - px * 0.5f, px, px), tex, ScaleMode.ScaleToFit);
            GUI.color = old;
            GUI.matrix = m0;
        }

        private static int Mix(int h, float v) => unchecked(h * 31 + v.GetHashCode());

        private void OnGUI()
        {
            if (map == null || !map.HasMap) return;

            int S = ViewSize;
            curHalf = S * 0.5f;
            curRad = Mathf.Clamp01(roundness) * curHalf;

            Event e = Event.current;
            Rect r = GetRect(S);
            Vector2 local = e.mousePosition - r.position;
            Vector2 half2 = new Vector2(curHalf, curHalf);
            bool inside = r.Contains(e.mousePosition) && ShapeSdf(local - half2) <= 0f;

            if (mouseZoom && inside && e.type == EventType.ScrollWheel)
            {
                viewMeters = Mathf.Clamp(viewMeters * (e.delta.y > 0f ? 1.12f : 0.89f), zoomRange.x, zoomRange.y);
                e.Use();
            }

            Vector3 tp = TargetPosition;
            float yaw = target != null ? target.eulerAngles.y : 0f;
            float rot = rotateWithTarget ? -yaw : 0f;

            Vector2 bs = map.BoundsSize;
            Vector2 su = new Vector2(viewMeters / bs.x, viewMeters / bs.y);
            Vector2 cUV = map.WorldToUV(tp);
            if (!rotateWithTarget)
            {
                cUV.x = su.x >= 1f ? 0.5f : Mathf.Clamp(cUV.x, su.x * 0.5f, 1f - su.x * 0.5f);
                cUV.y = su.y >= 1f ? 0.5f : Mathf.Clamp(cUV.y, su.y * 0.5f, 1f - su.y * 0.5f);
            }
            Vector3 centerW = map.UVToWorld(cUV, tp.y);
            float mpp = viewMeters / S;

            // Clics : gauche = destination, droit = effacer (seulement dans la forme).
            if (clickToSetDestination && inside && e.type == EventType.MouseDown)
            {
                if (e.button == 0)
                {
                    Vector2 v = Rot(local - half2, -rot);
                    Vector3 w = centerW + new Vector3(v.x * mpp, 0f, -v.y * mpp);
                    if (TerrainModifierManager.TrySampleBaseHeight(w.x, w.z, out float gy)) w.y = gy;
                    SetDestination(w);
                    e.Use();
                }
                else if (e.button == 1) { ClearDestination(); e.Use(); }
            }

            if (e.type != EventType.Repaint) return;
            if (!EnsureMapPixels()) return;
            EnsureResources();
            EnsureViewTexture(S);

            // Recompose seulement si quelque chose a changé.
            int key = 17;
            key = Mix(key, centerW.x); key = Mix(key, centerW.z); key = Mix(key, rot); key = Mix(key, viewMeters);
            key = Mix(key, roundness); key = Mix(key, borderThickness); key = Mix(key, showBorder ? 1f : 0f);
            key = Mix(key, opacity); key = Mix(key, routeVersion); key = Mix(key, routeWidth);
            key = Mix(key, backColor.r + backColor.g * 3f + backColor.b * 7f + backColor.a * 13f);
            key = Mix(key, borderColor.r + borderColor.g * 3f + borderColor.b * 7f + borderColor.a * 13f);
            key = Mix(key, routeColor.r + routeColor.g * 3f + routeColor.b * 7f + routeColor.a * 13f);
            if (forceCompose || key != lastKey)
            {
                Compose(centerW, rot);
                lastKey = key;
                forceCompose = false;
            }

            Color old = GUI.color;
            GUI.color = Color.white;
            GUI.DrawTexture(r, viewTex);

            Vector2 origin = r.position;

            // Ennemis (masqués hors de la forme)
            if (showEnemies)
            {
                Texture et = enemyIcon != null ? (Texture)enemyIcon : dotTex;
                Color etint = enemyIcon != null ? Color.white : enemyColor;
                foreach (var en in enemies)
                {
                    if (en == null) continue;
                    Vector2 p = W2V(en.position, centerW, rot);
                    if (ShapeSdf(p - half2) > -enemyIconSize * 0.5f) continue;
                    DrawIcon(et, origin + p, enemyIconSize, etint, 0f);
                }
            }

            // Destination
            if (TryGetDestination(out Vector3 dest))
            {
                Vector2 p = origin + ClampToShape(W2V(dest, centerW, rot), 8f);
                DrawIcon(dotTex, p, 14f, Color.white, 0f);
                DrawIcon(dotTex, p, 9f, routeColor, 0f);
            }

            // Joueur (orienté)
            {
                Vector2 p = origin + ClampToShape(W2V(tp, centerW, rot), playerIconSize * 0.5f);
                float ang = yaw + rot;
                if (playerIcon != null) DrawIcon(playerIcon, p, playerIconSize, markerColor, ang);
                else
                {
                    DrawIcon(arrowTex, p, playerIconSize + 6f, new Color(0f, 0f, 0f, 0.8f), ang);
                    DrawIcon(arrowTex, p, playerIconSize, markerColor, ang);
                }
            }

            // Nord : placé sur le bord de la forme, dans la direction du nord.
            if (showNorth)
            {
                Vector2 dir = Rot(new Vector2(0f, -1f), rot);
                Vector2 p = origin + half2 + dir * EdgeDistance(dir, northIconSize * 0.5f + 3f);
                if (northIcon != null) DrawIcon(northIcon, p, northIconSize, Color.white, 0f);
                else
                {
                    DrawIcon(dotTex, p, northIconSize, new Color(0f, 0f, 0f, 0.55f), 0f);
                    GUI.color = Color.white;
                    GUI.Label(new Rect(p.x - 10f, p.y - 8f, 20f, 16f), "N", labelStyle);
                }
            }

            // Distance joueur -> destination
            if (showDistance && TryGetDestination(out _))
            {
                string txt = HasRoute ? (RouteLength >= 1000f ? $"{RouteLength / 1000f:0.0} km" : $"{RouteLength:0} m") : "Pas d'itinéraire";
                float pw = Mathf.Min(S * 0.6f, 120f), ph = 20f;
                float top = S - ph - 4f;
                // Remonte la pastille jusqu'à ce que ses coins soient dans la forme (coins arrondis / cercle).
                while (top > curHalf && (ShapeSdf(new Vector2(-pw * 0.5f, top + ph - curHalf)) > -2f
                                      || ShapeSdf(new Vector2(pw * 0.5f, top + ph - curHalf)) > -2f))
                    top -= 2f;
                Rect pill = new Rect(origin.x + curHalf - pw * 0.5f, origin.y + top, pw, ph);
                GUI.color = new Color(0f, 0f, 0f, 0.65f);
                GUI.DrawTexture(pill, Texture2D.whiteTexture);
                GUI.color = Color.white;
                GUI.Label(pill, txt, labelStyle);
            }

            GUI.color = old;
        }

        // ------------------------------------------------------------------
        private void OnDrawGizmosSelected()
        {
            Gizmos.color = routeColor;
            for (int i = 1; i < route.Count; i++)
                Gizmos.DrawLine(route[i - 1] + Vector3.up * 0.8f, route[i] + Vector3.up * 0.8f);
            if (TryGetDestination(out Vector3 d)) Gizmos.DrawSphere(d + Vector3.up * 0.8f, 1.2f);
        }
    }
}
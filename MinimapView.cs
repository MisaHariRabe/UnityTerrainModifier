using System;
using System.Collections.Generic;
using UnityEngine;

namespace RoadTools
{
    public enum MinimapAnchor { TopLeft, TopRight, BottomLeft, BottomRight }

    /// <summary>
    /// Affichage de la minimap (IMGUI, aucune dépendance UI) : suit une cible, zoom molette,
    /// clic gauche = destination, clic droit = effacer. L'itinéraire suit les splines des routes et les intersections.
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
        [Tooltip("La carte tourne : la direction de la cible pointe toujours vers le haut.")]
        public bool rotateWithTarget = false;
        public bool mouseZoom = true;
        public bool clickToSetDestination = true;
        [Range(0f, 1f)] public float opacity = 1f;
        public Color backColor = new Color(0f, 0f, 0f, 0.8f);
        public Color borderColor = new Color(1f, 1f, 1f, 0.85f);
        public Color markerColor = new Color(1f, 1f, 1f, 1f);

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

        private Texture2D overlay;
        private Color32[] buf;
        private Texture2D arrowTex, dotTex;
        private GUIStyle labelStyle;

        public Vector3 TargetPosition => target != null ? target.position : transform.position;

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
            RedrawOverlay();
        }

        public void InvalidateNetwork() => networkDirty = true;

        /// <summary>Calcule le chemin cible -> destination en suivant routes et intersections.</summary>
        public bool RecomputeRoute()
        {
            route.Clear();
            HasRoute = false;
            RouteLength = 0f;

            if (!TryGetDestination(out Vector3 dest)) { RedrawOverlay(); return false; }

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
            RedrawOverlay();
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
            if (overlay != null) RoadUtil.Destroy(overlay);
            if (arrowTex != null) RoadUtil.Destroy(arrowTex);
            if (dotTex != null) RoadUtil.Destroy(dotTex);
            overlay = arrowTex = dotTex = null;
            buf = null;
        }

        private void OnRoadRebuilt(Road r) => networkDirty = true;
        private void OnMapBaked() => RedrawOverlay();

        private void OnValidate()
        {
            size = Mathf.Max(64f, size);
            viewMeters = Mathf.Clamp(viewMeters, 10f, 100000f);
        }

        private void Update()
        {
            if (!Application.isPlaying) return;
            if (map == null) { map = FindFirstObjectByType<MinimapMap>(); if (map != null) map.Baked += OnMapBaked; }
            if (!autoRecompute) return;

            if (!TryGetDestination(out Vector3 d))
            {
                if (route.Count > 0) { route.Clear(); HasRoute = false; RedrawOverlay(); }
                return;
            }
            if (Time.unscaledTime - lastCompute < recomputeInterval) return;

            bool moved = (TargetPosition - lastFrom).sqrMagnitude > recomputeDistance * recomputeDistance;
            bool destChanged = (d - lastDest).sqrMagnitude > 0.25f;
            if (!computedOnce || moved || destChanged) RecomputeRoute();
        }

        // ------------------------------------------------------------------
        //  Surcouche de l'itinéraire (texture transparente, même emprise que la carte)
        // ------------------------------------------------------------------
        private bool EnsureOverlay()
        {
            Texture2D t = map != null ? map.Texture : null;
            if (t == null || !map.HasMap) return false;
            if (overlay == null || overlay.width != t.width || overlay.height != t.height)
            {
                if (overlay != null) RoadUtil.Destroy(overlay);
                overlay = new Texture2D(t.width, t.height, TextureFormat.RGBA32, false)
                {
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                    hideFlags = HideFlags.HideAndDontSave
                };
                buf = new Color32[t.width * t.height];
            }
            return true;
        }

        private void RedrawOverlay()
        {
            if (!EnsureOverlay()) return;
            Array.Clear(buf, 0, buf.Length);

            if (route.Count >= 2)
            {
                int w = overlay.width, h = overlay.height;
                Vector2 bs = map.BoundsSize;
                float mpp = bs.x / w;
                float r = Mathf.Max(1.3f, routeWidth * 0.5f / mpp);
                Color32 col = routeColor;

                Vector2 Px(Vector3 p) { Vector2 uv = map.WorldToUV(p); return new Vector2(uv.x * w, uv.y * h); }

                Vector2 prev = Px(route[0]);
                for (int i = 1; i < route.Count; i++)
                {
                    Vector2 cur = Px(route[i]);
                    float len = Vector2.Distance(prev, cur);
                    int steps = Mathf.Max(1, Mathf.CeilToInt(len / Mathf.Max(0.5f, r * 0.5f)));
                    for (int s = 0; s <= steps; s++)
                    {
                        Vector2 p = Vector2.Lerp(prev, cur, s / (float)steps);
                        Disc(p.x, p.y, r, col, w, h);
                    }
                    prev = cur;
                }
            }
            overlay.SetPixels32(buf);
            overlay.Apply(false, false);
        }

        private void Disc(float cx, float cy, float r, Color32 c, int w, int h)
        {
            int x0 = Mathf.Max(0, Mathf.FloorToInt(cx - r)), x1 = Mathf.Min(w - 1, Mathf.CeilToInt(cx + r));
            int y0 = Mathf.Max(0, Mathf.FloorToInt(cy - r)), y1 = Mathf.Min(h - 1, Mathf.CeilToInt(cy + r));
            float r2 = r * r;
            for (int y = y0; y <= y1; y++)
            {
                float dy = y + 0.5f - cy;
                for (int x = x0; x <= x1; x++)
                {
                    float dx = x + 0.5f - cx;
                    if (dx * dx + dy * dy <= r2) buf[y * w + x] = c;
                }
            }
        }

        // ------------------------------------------------------------------
        //  IMGUI
        // ------------------------------------------------------------------
        private Rect GetRect()
        {
            bool left = anchor == MinimapAnchor.TopLeft || anchor == MinimapAnchor.BottomLeft;
            bool top = anchor == MinimapAnchor.TopLeft || anchor == MinimapAnchor.TopRight;
            float x = left ? margin.x : Screen.width - size - margin.x;
            float y = top ? margin.y : Screen.height - size - margin.y;
            return new Rect(x, y, size, size);
        }

        /// <summary>Rotation horaire à l'écran (y vers le bas).</summary>
        private static Vector2 Rot(Vector2 v, float deg)
        {
            float a = deg * Mathf.Deg2Rad, c = Mathf.Cos(a), s = Mathf.Sin(a);
            return new Vector2(v.x * c - v.y * s, v.x * s + v.y * c);
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

        private void OnGUI()
        {
            if (map == null || !map.HasMap) return;
            Event e = Event.current;
            Rect r = GetRect();
            bool inside = r.Contains(e.mousePosition);

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
            float mpp = viewMeters / size;

            // Clics : gauche = destination, droit = effacer.
            if (clickToSetDestination && inside && e.type == EventType.MouseDown)
            {
                if (e.button == 0)
                {
                    Vector2 v = Rot(e.mousePosition - r.center, -rot);
                    Vector3 w = centerW + new Vector3(v.x * mpp, 0f, -v.y * mpp);
                    if (TerrainModifierManager.TrySampleBaseHeight(w.x, w.z, out float gy)) w.y = gy;
                    SetDestination(w);
                    e.Use();
                }
                else if (e.button == 1) { ClearDestination(); e.Use(); }
            }

            if (e.type != EventType.Repaint) return;
            EnsureResources();

            Vector2 W2V(Vector3 w)
            {
                Vector2 off = new Vector2((w.x - centerW.x) / mpp, -(w.z - centerW.z) / mpp);
                off = Rot(off, rot);
                return new Vector2(size * 0.5f + off.x, size * 0.5f + off.y);
            }

            Color old = GUI.color;
            Texture2D tex = map.Texture;
            bool hasOverlay = EnsureOverlay() && route.Count >= 2;

            // Fond
            GUI.color = backColor; GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = new Color(1f, 1f, 1f, opacity);

            GUI.BeginGroup(r);
            Matrix4x4 gm = GUI.matrix;
            if (rotateWithTarget)
            {
                const float K = 1.4143f;
                float big = size * K;
                Vector2 sub = su * K;
                Rect uv = new Rect(cUV.x - sub.x * 0.5f, cUV.y - sub.y * 0.5f, sub.x, sub.y);
                Rect dr = new Rect((size - big) * 0.5f, (size - big) * 0.5f, big, big);
                GUIUtility.RotateAroundPivot(rot, new Vector2(size * 0.5f, size * 0.5f));
                GUI.DrawTextureWithTexCoords(dr, tex, uv);
                if (hasOverlay) GUI.DrawTextureWithTexCoords(dr, overlay, uv);
                GUI.matrix = gm;
            }
            else
            {
                Rect uv = new Rect(cUV.x - su.x * 0.5f, cUV.y - su.y * 0.5f, su.x, su.y);
                Rect dr = new Rect(0f, 0f, size, size);
                GUI.DrawTextureWithTexCoords(dr, tex, uv);
                if (hasOverlay) GUI.DrawTextureWithTexCoords(dr, overlay, uv);
            }

            // Destination
            if (TryGetDestination(out Vector3 dest))
            {
                Vector2 p = W2V(dest);
                p.x = Mathf.Clamp(p.x, 8f, size - 8f);
                p.y = Mathf.Clamp(p.y, 8f, size - 8f);
                GUI.color = Color.white;
                GUI.DrawTexture(new Rect(p.x - 7f, p.y - 7f, 14f, 14f), dotTex);
                GUI.color = routeColor;
                GUI.DrawTexture(new Rect(p.x - 4.5f, p.y - 4.5f, 9f, 9f), dotTex);
            }

            // Cible (flèche orientée)
            {
                Vector2 p = W2V(tp);
                p.x = Mathf.Clamp(p.x, 6f, size - 6f);
                p.y = Mathf.Clamp(p.y, 6f, size - 6f);
                float ang = yaw + rot;
                Matrix4x4 m0 = GUI.matrix;
                GUIUtility.RotateAroundPivot(ang, p);
                GUI.color = new Color(0f, 0f, 0f, 0.8f);
                GUI.DrawTexture(new Rect(p.x - 11f, p.y - 11f, 22f, 22f), arrowTex);
                GUI.color = markerColor;
                GUI.DrawTexture(new Rect(p.x - 8f, p.y - 8f, 16f, 16f), arrowTex);
                GUI.matrix = m0;
            }

            // Nord
            if (rotateWithTarget)
            {
                Vector2 n = Rot(new Vector2(0f, -size * 0.5f + 10f), rot) + new Vector2(size * 0.5f, size * 0.5f);
                GUI.color = Color.white;
                GUI.Label(new Rect(n.x - 10f, n.y - 8f, 20f, 16f), "N", labelStyle);
            }

            // Distance
            string txt = null;
            if (TryGetDestination(out _))
                txt = HasRoute ? (RouteLength >= 1000f ? $"{RouteLength / 1000f:0.0} km" : $"{RouteLength:0} m") : "Pas d'itinéraire";
            if (txt != null)
            {
                GUI.color = new Color(0f, 0f, 0f, 0.65f);
                GUI.DrawTexture(new Rect(0f, size - 20f, size, 20f), Texture2D.whiteTexture);
                GUI.color = Color.white;
                GUI.Label(new Rect(0f, size - 20f, size, 20f), txt, labelStyle);
            }
            GUI.EndGroup();

            // Bordure
            GUI.color = borderColor;
            GUI.DrawTexture(new Rect(r.x - 2f, r.y - 2f, r.width + 4f, 2f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(r.x - 2f, r.yMax, r.width + 4f, 2f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(r.x - 2f, r.y, 2f, r.height), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(r.xMax, r.y, 2f, r.height), Texture2D.whiteTexture);
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
using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace RoadTools
{
    public enum MapZoneShape { Circle, Box, Polygon }

    /// <summary>
    /// Une zone nommée du monde (quartier, forêt, port...). Le test se fait sur le plan XZ : la hauteur est ignorée.
    /// Position et rotation Y de l'objet placent la zone. Si plusieurs zones se chevauchent, la priorité la plus haute gagne
    /// (à égalité : la plus petite).
    /// </summary>
    [ExecuteAlways]
    [AddComponentMenu("Roads/Map Zone")]
    public class MapZone : MonoBehaviour
    {
        [Tooltip("Nom affiché à l'écran quand le joueur entre dans la zone.")]
        public string displayName = "Nouvelle zone";
        public MapZoneShape shape = MapZoneShape.Circle;

        [Min(1f)] public float radius = 100f;
        [Tooltip("Largeur (X) et profondeur (Z) de la boîte, dans le repère de l'objet tourné en Y.")]
        public Vector2 boxSize = new Vector2(200f, 200f);
        [Tooltip("Contour (X, Z) en mètres, relatif à l'objet. Au moins 3 points, dans n'importe quel sens.")]
        public List<Vector2> polygon = new List<Vector2>
        {
            new Vector2(-100f, -100f), new Vector2(100f, -100f), new Vector2(100f, 100f), new Vector2(-100f, 100f)
        };

        [Tooltip("Plus haut = prioritaire quand des zones se chevauchent (ex : un petit quartier dans une grande ville).")]
        public int priority = 0;

        public bool showGizmos = true;
        public Color gizmoColor = new Color(1f, 0.8f, 0.2f, 1f);

        // ---------- Registre ----------
        private static readonly List<MapZone> all = new List<MapZone>();
        public static IReadOnlyList<MapZone> All => all;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => all.Clear();   // "Enter Play Mode" sans rechargement de domaine

        private void OnEnable() { if (!all.Contains(this)) all.Add(this); }
        private void OnDisable() => all.Remove(this);

        /// <summary>Zone la plus prioritaire contenant ce point (null si aucune).</summary>
        public static MapZone Find(Vector3 world)
        {
            MapZone best = null;
            float bestArea = 0f;
            for (int i = 0; i < all.Count; i++)
            {
                MapZone z = all[i];
                if (z == null || !z.isActiveAndEnabled || !z.Contains(world)) continue;
                float a = z.Area;
                if (best == null || z.priority > best.priority || (z.priority == best.priority && a < bestArea))
                { best = z; bestArea = a; }
            }
            return best;
        }

        // ---------- Géométrie ----------
        private Vector2 ToLocal(Vector3 w)
        {
            Vector3 d = w - transform.position;
            float yaw = transform.eulerAngles.y * Mathf.Deg2Rad;
            float c = Mathf.Cos(yaw), s = Mathf.Sin(yaw);
            return new Vector2(d.x * c - d.z * s, d.x * s + d.z * c);   // même repère que TerrainFootprint
        }

        private Vector3 ToWorld(Vector2 l)
        {
            Vector3 p = transform.position;
            float yaw = transform.eulerAngles.y * Mathf.Deg2Rad;
            float c = Mathf.Cos(yaw), s = Mathf.Sin(yaw);
            return new Vector3(p.x + c * l.x + s * l.y, p.y, p.z - s * l.x + c * l.y);
        }

        public bool Contains(Vector3 world)
        {
            Vector2 l = ToLocal(world);
            switch (shape)
            {
                case MapZoneShape.Circle: return l.sqrMagnitude <= radius * radius;
                case MapZoneShape.Box: return Mathf.Abs(l.x) <= boxSize.x * 0.5f && Mathf.Abs(l.y) <= boxSize.y * 0.5f;
                default: return PointInPolygon(l);
            }
        }

        public float Area
        {
            get
            {
                switch (shape)
                {
                    case MapZoneShape.Circle: return Mathf.PI * radius * radius;
                    case MapZoneShape.Box: return boxSize.x * boxSize.y;
                    default:
                        float a = 0f;
                        for (int i = 0, n = polygon.Count; i < n; i++)
                        {
                            Vector2 p = polygon[i], q = polygon[(i + 1) % n];
                            a += p.x * q.y - q.x * p.y;
                        }
                        return Mathf.Abs(a) * 0.5f;
                }
            }
        }

        // Règle pair / impair.
        private bool PointInPolygon(Vector2 p)
        {
            int n = polygon.Count;
            if (n < 3) return false;
            bool inside = false;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                Vector2 a = polygon[i], b = polygon[j];
                if ((a.y > p.y) != (b.y > p.y) && p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x)
                    inside = !inside;
            }
            return inside;
        }

        private void OnValidate()
        {
            radius = Mathf.Max(1f, radius);
            boxSize = Vector2.Max(boxSize, Vector2.one);
        }

        // ---------- Gizmos ----------
        private void OnDrawGizmos()
        {
            if (!showGizmos) return;
            Gizmos.color = gizmoColor;

            var ring = new List<Vector2>();
            if (shape == MapZoneShape.Circle)
            {
                const int seg = 64;
                for (int i = 0; i < seg; i++)
                {
                    float a = i / (float)seg * Mathf.PI * 2f;
                    ring.Add(new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius);
                }
            }
            else if (shape == MapZoneShape.Box)
            {
                Vector2 h = boxSize * 0.5f;
                ring.Add(new Vector2(-h.x, -h.y)); ring.Add(new Vector2(h.x, -h.y));
                ring.Add(new Vector2(h.x, h.y)); ring.Add(new Vector2(-h.x, h.y));
            }
            else ring.AddRange(polygon);

            for (int i = 0; i < ring.Count; i++)
                DrawEdge(ring[i], ring[(i + 1) % ring.Count]);

#if UNITY_EDITOR
            Handles.Label(transform.position + Vector3.up * 2f, $"{displayName}  (prio {priority})");
#endif
        }

        // Les côtés sont sous-divisés pour épouser le relief.
        private void DrawEdge(Vector2 a, Vector2 b)
        {
            int seg = Mathf.Clamp(Mathf.CeilToInt(Vector2.Distance(a, b) / 4f), 1, 100);
            Vector3 prev = Ground(ToWorld(a));
            for (int i = 1; i <= seg; i++)
            {
                Vector3 q = Ground(ToWorld(Vector2.Lerp(a, b, i / (float)seg)));
                Gizmos.DrawLine(prev, q);
                prev = q;
            }
        }

        private Vector3 Ground(Vector3 w)
        {
            w.y = ModifierMath.GroundY(w.x, w.z, transform.position.y) + 0.3f;
            return w;
        }
    }

#if UNITY_EDITOR
    public static class MapZoneMenu
    {
        [MenuItem("GameObject/Road System/Zone de la map", false, 55)]
        private static void CreateZone(MenuCommand cmd)
        {
            var go = new GameObject("Zone");
            Undo.RegisterCreatedObjectUndo(go, "Créer une zone");
            GameObjectUtility.SetParentAndAlign(go, cmd.context as GameObject);
            go.transform.position = SpawnPoint();
            Undo.AddComponent<MapZone>(go);
            Selection.activeGameObject = go;
        }

        [MenuItem("GameObject/Road System/Affichage du nom de zone", false, 56)]
        private static void CreateDisplay(MenuCommand cmd)
        {
            var go = new GameObject("Zone Name Display");
            Undo.RegisterCreatedObjectUndo(go, "Créer l'affichage de zone");
            GameObjectUtility.SetParentAndAlign(go, cmd.context as GameObject);
            Undo.AddComponent<ZoneNameDisplay>(go);
            Selection.activeGameObject = go;
        }

        private static Vector3 SpawnPoint()
        {
            Vector3 p = Vector3.zero;
            var sv = SceneView.lastActiveSceneView;
            if (sv != null) p = sv.pivot;
            if (TerrainModifierManager.TrySampleBaseHeight(p.x, p.z, out float y)) p.y = y;
            return p;
        }
    }
#endif
}
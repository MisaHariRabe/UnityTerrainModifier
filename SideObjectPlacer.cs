using System.Collections.Generic;
using UnityEngine;

namespace RoadTools
{
    /// <summary>Polyligne avec longueurs cumulées : permet d'échantillonner position + tangente à une distance donnée.</summary>
    public sealed class PolyPath
    {
        private readonly List<Vector3> pts;
        private readonly float[] cum;
        public float Length { get; private set; }

        public PolyPath(IList<Vector3> points)
        {
            pts = new List<Vector3>(points);
            cum = new float[pts.Count];
            for (int i = 1; i < pts.Count; i++)
                cum[i] = cum[i - 1] + Vector3.Distance(pts[i], pts[i - 1]);
            Length = pts.Count > 0 ? cum[pts.Count - 1] : 0f;
        }

        /// <summary>Position et tangente HORIZONTALE (normalisée) à la distance d.</summary>
        public bool Sample(float d, out Vector3 pos, out Vector3 tangent)
        {
            pos = Vector3.zero; tangent = Vector3.forward;
            int n = pts.Count;
            if (n < 2 || Length < 0.01f) return false;

            d = Mathf.Clamp(d, 0f, Length);
            int last = -1;
            for (int i = 0; i < n - 1; i++)
            {
                float seg = cum[i + 1] - cum[i];
                if (seg < 1e-4f) continue;
                last = i;
                if (d <= cum[i + 1]) break;
            }
            if (last < 0) return false;

            float f = Mathf.Clamp01((d - cum[last]) / (cum[last + 1] - cum[last]));
            pos = Vector3.Lerp(pts[last], pts[last + 1], f);
            Vector3 t = pts[last + 1] - pts[last];
            t.y = 0f;
            tangent = t.sqrMagnitude > 1e-8f ? t.normalized : Vector3.forward;
            return true;
        }
    }

    /// <summary>Tirages aléatoires d'un emplacement (toujours tous consommés => résultat stable).</summary>
    public struct SideObjectRoll
    {
        public double roll;
        public GameObject prefab;
        public float lat, fwd, yawJ, randYaw, scale;
        public bool Occupied(SideObjectRule rule) => prefab != null && roll <= rule.probability;
    }

    /// <summary>Logique de placement partagée entre les routes et les intersections.</summary>
    public static class SideObjectPlacer
    {
        private static float Jitter(System.Random rng, float amount) =>
            ((float)rng.NextDouble() * 2f - 1f) * amount;

        public static SideObjectRoll Roll(SideObjectRule rule, System.Random rng)
        {
            var r = new SideObjectRoll();
            r.roll = rng.NextDouble();
            r.prefab = PickPrefab(rule, rng);
            r.lat = Jitter(rng, rule.lateralJitter);
            r.fwd = Jitter(rng, rule.forwardJitter);
            r.yawJ = Jitter(rng, rule.randomYaw);
            r.randYaw = (float)rng.NextDouble() * 360f;
            r.scale = Mathf.Lerp(rule.scaleRange.x, rule.scaleRange.y, (float)rng.NextDouble());
            return r;
        }

        /// <summary>Tirage pondéré. Les prefabs décochés (enabled = false) sont ignorés.</summary>
        public static GameObject PickPrefab(SideObjectRule rule, System.Random rng)
        {
            float sum = 0f;
            foreach (var e in rule.prefabs) if (Usable(e)) sum += Mathf.Max(0f, e.weight);
            if (sum <= 0f) return null;

            float r = (float)rng.NextDouble() * sum;
            foreach (var e in rule.prefabs)
            {
                if (!Usable(e)) continue;
                r -= Mathf.Max(0f, e.weight);
                if (r <= 0f) return e.prefab;
            }
            return null;
        }

        private static bool Usable(SideObjectEntry e) => e != null && e.enabled && e.prefab != null;

        public static bool HasAnyPrefab(SideObjectRule rule)
        {
            if (rule == null || !rule.enabled || rule.prefabs == null) return false;
            foreach (var e in rule.prefabs) if (Usable(e) && e.weight > 0f) return true;
            return false;
        }

        /// <summary>Longueur du prefab (mètres, échelle racine incluse) le long de l'axe choisi, d'après ses meshes.</summary>
        public static float PrefabLength(GameObject prefab, SideObjectAxis axis)
        {
            if (prefab == null) return 0f;
            Matrix4x4 inv = prefab.transform.worldToLocalMatrix;
            bool any = false;
            Bounds b = new Bounds();

            foreach (var mf in prefab.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                Matrix4x4 m = inv * mf.transform.localToWorldMatrix;
                Bounds mb = mf.sharedMesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = mb.center + Vector3.Scale(mb.extents,
                        new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));
                    Vector3 c = m.MultiplyPoint3x4(corner);
                    if (!any) { b = new Bounds(c, Vector3.zero); any = true; }
                    else b.Encapsulate(c);
                }
            }
            if (!any) return 0f;

            Vector3 s = prefab.transform.localScale;
            return axis == SideObjectAxis.X ? b.size.x * Mathf.Abs(s.x) : b.size.z * Mathf.Abs(s.z);
        }

        /// <summary>Pas entre deux emplacements : espacement fixe, ou longueur du plus grand prefab actif.</summary>
        public static float Step(SideObjectRule rule)
        {
            float fixedStep = Mathf.Max(0.5f, rule.spacing);
            if (!rule.useBoundsSpacing) return fixedStep;

            float best = 0f;
            foreach (var e in rule.prefabs)
                if (Usable(e) && e.weight > 0f) best = Mathf.Max(best, PrefabLength(e.prefab, rule.lengthAxis));
            if (best < 0.01f) return fixedStep;

            best *= Mathf.Max(rule.scaleRange.x, rule.scaleRange.y);
            return Mathf.Max(0.1f, best + rule.spacingPadding);
        }

        /// <summary>
        /// Instancie l'objet. faceRoad = direction horizontale vers la route ; along = direction du sens de la route
        /// (peut être inclinée pour suivre la pente).
        /// </summary>
        public static GameObject Spawn(SideObjectRule rule, SideObjectRoll r, Transform root,
                                       Vector3 pos, Vector3 faceRoad, Vector3 along)
        {
            Quaternion rot;
            switch (rule.orientation)
            {
                case SideObjectOrientation.FaceRoad: rot = Quaternion.LookRotation(faceRoad, Vector3.up); break;
                case SideObjectOrientation.AwayFromRoad: rot = Quaternion.LookRotation(-faceRoad, Vector3.up); break;
                case SideObjectOrientation.AlongRoad:
                    rot = Quaternion.LookRotation(along, Vector3.up);
                    // Prefab modélisé "en largeur" : on tourne son axe X dans le sens de la route.
                    if (rule.lengthAxis == SideObjectAxis.X) rot *= Quaternion.Euler(0f, -90f, 0f);
                    break;
                default: rot = Quaternion.Euler(0f, r.randYaw, 0f); break;
            }
            rot *= Quaternion.Euler(0f, rule.yawOffset + r.yawJ, 0f);

            GameObject go = UnityEngine.Object.Instantiate(r.prefab, root);
            go.transform.SetPositionAndRotation(pos, rot);
            go.transform.localScale = r.prefab.transform.localScale * r.scale;
            go.name = r.prefab.name;
            RoadUtil.MarkDontSave(go);
            return go;
        }

        /// <summary>
        /// Place une règle le long d'une polyligne (intersections). La polyligne est parcourue de façon à ce que
        /// l'extérieur (le côté opposé à la route) soit à GAUCHE du parcours.
        /// </summary>
        public static void PlaceAlongPath(SideObjectRule rule, int ruleIndex, int pathIndex, Transform root,
                                          PolyPath path, bool hasSidewalk, float sidewalkWidth, float sidewalkHeight)
        {
            if (path == null) return;
            float len = path.Length;
            float startD = rule.start * len, endD = rule.end * len;
            if (endD <= startD) return;

            var rng = new System.Random(rule.seed * 7919 + ruleIndex * 104729 + pathIndex * 1299709);
            float step = Step(rule);

            int guard = 0;
            for (float d = startD; d <= endD + 0.001f && guard < 20000; d += step, guard++)
            {
                float dd = Mathf.Clamp(d + Jitter(rng, rule.spacingJitter * step), startD, endD);
                SideObjectRoll roll = Roll(rule, rng);
                if (!roll.Occupied(rule)) continue;
                if (!path.Sample(dd, out Vector3 p, out Vector3 tan)) continue;

                Vector3 outward = -Vector3.Cross(Vector3.up, tan);
                float anchor = rule.anchor == SideObjectAnchor.SidewalkOuterEdge && hasSidewalk ? sidewalkWidth : 0f;
                float lat = anchor + rule.offset + roll.lat;
                Vector3 pos = p + outward * lat + tan * (rule.forwardOffset + roll.fwd);

                float y = p.y + rule.heightOffset;
                if (hasSidewalk && lat < sidewalkWidth) y += sidewalkHeight; // posé sur le trottoir
                if (rule.heightMode == SideObjectHeight.SnapToTerrain && RoadUtil.TrySampleTerrainHeight(pos, out float ty))
                    y = ty + rule.heightOffset;
                pos.y = y;

                Spawn(rule, roll, root, pos, -outward, tan);
            }
        }
    }

    public enum SideObjectPreset
    {
        Streetlights,
        Trees,
        Bollards,
        Fence,
        GuardRail,
        JerseyBarrier,
        Hedge,
        Signs,
        IslandTrees
    }

    /// <summary>Préréglages de placement : ils ne touchent ni aux prefabs ni à la portion (start/end) ; tout reste ajustable ensuite.</summary>
    public static class SideObjectPresets
    {
        public static readonly SideObjectPreset[] All = (SideObjectPreset[])System.Enum.GetValues(typeof(SideObjectPreset));

        public static string Label(SideObjectPreset p)
        {
            switch (p)
            {
                case SideObjectPreset.Streetlights: return "Lampadaires";
                case SideObjectPreset.Trees: return "Arbres";
                case SideObjectPreset.Bollards: return "Bornes / poteaux";
                case SideObjectPreset.Fence: return "Clôture";
                case SideObjectPreset.GuardRail: return "Glissière de sécurité";
                case SideObjectPreset.JerseyBarrier: return "Barrière béton (jersey)";
                case SideObjectPreset.Hedge: return "Haie";
                case SideObjectPreset.Signs: return "Panneaux";
                case SideObjectPreset.IslandTrees: return "Îlot central (rond-point)";
                default: return p.ToString();
            }
        }

        public static string Describe(SideObjectPreset p)
        {
            switch (p)
            {
                case SideObjectPreset.Fence: return "Éléments bout à bout, alignés sur la route, posés au bord extérieur du trottoir.";
                case SideObjectPreset.GuardRail: return "Éléments bout à bout (léger chevauchement), alignés sur la route et sa pente, au bord de la chaussée.";
                case SideObjectPreset.JerseyBarrier: return "Blocs bout à bout au bord de la chaussée.";
                case SideObjectPreset.Hedge: return "Éléments bout à bout, collés au terrain.";
                case SideObjectPreset.IslandTrees: return "Intersections : objets répartis sur le pourtour de l'îlot central.";
                default: return "Objets espacés régulièrement.";
            }
        }

        public static void Apply(SideObjectPreset preset, SideObjectRule r)
        {
            // Base neutre : le préréglage définit tout le placement.
            r.side = RoadSide.Both;
            r.spacing = 12f; r.useBoundsSpacing = false; r.spacingPadding = 0f;
            r.lengthAxis = SideObjectAxis.Z; r.alignToRoadSlope = false;
            r.spacingJitter = 0f; r.probability = 1f;
            r.anchor = SideObjectAnchor.SidewalkOuterEdge; r.offset = 1f;
            r.lateralJitter = 0f; r.forwardOffset = 0f; r.forwardJitter = 0f;
            r.heightOffset = 0f; r.heightMode = SideObjectHeight.RoadLevel;
            r.orientation = SideObjectOrientation.FaceRoad; r.yawOffset = 0f; r.randomYaw = 0f;
            r.scaleRange = Vector2.one;
            r.zone = IntersectionZone.Corners;
            r.name = Label(preset);

            switch (preset)
            {
                case SideObjectPreset.Streetlights:
                    r.spacing = 25f; r.offset = 0.5f;
                    break;
                case SideObjectPreset.Trees:
                    r.spacing = 10f; r.spacingJitter = 0.15f; r.probability = 0.9f;
                    r.offset = 1.5f; r.lateralJitter = 0.3f; r.forwardJitter = 1f;
                    r.orientation = SideObjectOrientation.Random;
                    r.scaleRange = new Vector2(0.85f, 1.15f);
                    break;
                case SideObjectPreset.Bollards:
                    r.spacing = 3f; r.anchor = SideObjectAnchor.RoadEdge; r.offset = 0.3f;
                    r.orientation = SideObjectOrientation.AlongRoad;
                    break;
                case SideObjectPreset.Fence:
                    r.useBoundsSpacing = true; r.offset = 0.4f;
                    r.orientation = SideObjectOrientation.AlongRoad; r.alignToRoadSlope = true;
                    break;
                case SideObjectPreset.GuardRail:
                    r.useBoundsSpacing = true; r.spacingPadding = -0.05f;
                    r.anchor = SideObjectAnchor.RoadEdge; r.offset = 0.6f;
                    r.orientation = SideObjectOrientation.AlongRoad; r.alignToRoadSlope = true;
                    break;
                case SideObjectPreset.JerseyBarrier:
                    r.useBoundsSpacing = true;
                    r.anchor = SideObjectAnchor.RoadEdge; r.offset = 0.5f;
                    r.orientation = SideObjectOrientation.AlongRoad; r.alignToRoadSlope = true;
                    break;
                case SideObjectPreset.Hedge:
                    r.useBoundsSpacing = true; r.offset = 0.8f;
                    r.orientation = SideObjectOrientation.AlongRoad; r.alignToRoadSlope = true;
                    r.heightMode = SideObjectHeight.SnapToTerrain;
                    break;
                case SideObjectPreset.Signs:
                    r.spacing = 40f; r.spacingJitter = 0.3f; r.probability = 0.8f; r.offset = 0.3f;
                    break;
                case SideObjectPreset.IslandTrees:
                    r.zone = IntersectionZone.Island;
                    r.spacing = 6f; r.spacingJitter = 0.2f; r.offset = 1.5f;
                    r.orientation = SideObjectOrientation.Random;
                    r.scaleRange = new Vector2(0.9f, 1.2f);
                    break;
            }
        }
    }
}

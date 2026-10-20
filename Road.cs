using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Splines;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace RoadTools
{
    /// <summary>
    /// Route générée le long d'une spline : largeurs gauche/droite par section, trottoirs, objets latéraux,
    /// et aplanissement automatique du terrain (via TerrainSplineProfileNode).
    /// </summary>
    [ExecuteAlways]
    [RequireComponent(typeof(SplineContainer))]
    [AddComponentMenu("Roads/Road")]
    public class Road : MonoBehaviour
    {
        public static event Action<Road> Rebuilt;

        [Header("Dimensions par défaut")]
        [Min(0f)] public float leftWidth = 4f;
        [Min(0f)] public float rightWidth = 4f;
        [Tooltip("Longueur sur laquelle les largeurs de deux sections se fondent l'une dans l'autre.")]
        [Min(0f)] public float transitionLength = 6f;
        [Min(0f), Tooltip("Surélévation de la chaussée pour éviter le z-fighting avec le terrain.")]
        public float heightOffset = 0.05f;

        [Header("Génération")]
        [Range(0.5f, 10f)] public float sampleSpacing = 2f;
        [Min(0.5f), Tooltip("Longueur (m) d'une répétition de la texture dans le sens de la route.")]
        public float uvTileLength = 8f;
        public Material roadMaterial;
        public Material sidewalkMaterial;
        public bool generateCollider = true;
        public bool autoRebuild = true;

        [Header("Terrain")]
        [Tooltip("Aplanit le terrain sous la route (nécessite un TerrainModifierManager).")]
        public bool conformTerrain = true;
        [Min(0f), Tooltip("Largeur du raccord entre la route et le terrain naturel.")]
        public float terrainMargin = 6f;
        [Tooltip("Valeur initiale appliquée à la création du node terrain (ensuite, modifiez-la directement sur le TerrainSplineProfileNode).")]
        public int terrainLayerOrder = 0;
        [Tooltip("La largeur d'aplanissement de chaque point suit les dimensions de la route + la marge. Décochez pour régler les largeurs à la main sur le TerrainSplineProfileNode.")]
        public bool autoTerrainWidth = true;
        [Tooltip("Ajoute automatiquement un TerrainModifierManager aux terrains traversés par la route.")]
        public bool autoAddManager = true;

        [Header("Affichage")]
        public bool showGizmos = true;

        [HideInInspector] public List<RoadSection> sections = new List<RoadSection>();
        [HideInInspector] public List<SideObjectRule> sideObjects = new List<SideObjectRule>();

        // ---------- État ----------
        private struct DenseSample { public int curve; public float tc; public float dist; }

        [SerializeField, HideInInspector] private float lastTerrainMargin = -1f;
        // Dernières largeurs calculées automatiquement par point : permet de reconnaître une largeur modifiée à la main.
        [SerializeField, HideInInspector] private List<float> lastAutoWidths = new List<float>();

        private SplineContainer container;
        private readonly List<RoadPoint> points = new List<RoadPoint>();
        private readonly List<float> knotDistances = new List<float>();
        private readonly List<DenseSample> dense = new List<DenseSample>();
        private float totalLength;
        private bool pathDirty = true;
        private bool rebuildScheduled;
        private bool rebuildQueued;

        public int LastVertexCount { get; private set; }
        public float TotalLength { get { EnsurePath(); return totalLength; } }
        public IReadOnlyList<RoadPoint> Points { get { EnsurePath(); return points; } }

        public SplineContainer GetContainer()
        {
            if (container == null) container = GetComponent<SplineContainer>();
            return container;
        }

        // ---------- Cycle de vie ----------
        private void OnEnable()
        {
            GetContainer();
            Spline.Changed += OnSplineChanged;
            transform.hasChanged = false;
            Schedule(true);
        }

        private void OnDisable()
        {
            Spline.Changed -= OnSplineChanged;
            ClearGenerated();
        }

        private void Update()
        {
            if (transform.hasChanged)
            {
                transform.hasChanged = false;
                RequestRebuild();
            }
        }

        private void LateUpdate()
        {
            if (!rebuildQueued) return;
            rebuildQueued = false;
            Rebuild();
        }

        private void OnValidate()
        {
            leftWidth = Mathf.Max(0f, leftWidth);
            rightWidth = Mathf.Max(0f, rightWidth);
            RequestRebuild();
        }

        private void OnSplineChanged(Spline spline, int knotIndex, SplineModification modification)
        {
            var c = GetContainer();
            if (c != null && spline == c.Spline) RequestRebuild();
        }

        public void RequestRebuild()
        {
            pathDirty = true;
            if (autoRebuild) Schedule(false);
        }

        private void Schedule(bool force)
        {
            pathDirty = true;
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

        // ---------- Chemin ----------
        private void EnsurePath()
        {
            if (pathDirty) BuildPath();
        }

        private void BuildPath()
        {
            points.Clear();
            knotDistances.Clear();
            dense.Clear();
            totalLength = 0f;
            pathDirty = false;

            var c = GetContainer();
            if (c == null || c.Spline == null) return;
            Spline sp = c.Spline;
            int count = sp.Count;
            if (count < 2) return;

            bool closed = sp.Closed;
            int curves = closed ? count : count - 1;
            const int D = 32;

            // 1) Table dense (longueur d'arc cumulée, en monde).
            float acc = 0f;
            Vector3 prev = default;
            bool first = true;
            for (int ci = 0; ci < curves; ci++)
            {
                BezierCurve curve = sp.GetCurve(ci);
                for (int k = 0; k < D; k++)
                {
                    float tc = k / (float)D;
                    Vector3 p = transform.TransformPoint((Vector3)CurveUtility.EvaluatePosition(curve, tc));
                    if (!first) acc += Vector3.Distance(prev, p);
                    if (k == 0) knotDistances.Add(acc);
                    dense.Add(new DenseSample { curve = ci, tc = tc, dist = acc });
                    prev = p;
                    first = false;
                }
            }
            {
                BezierCurve last = sp.GetCurve(curves - 1);
                Vector3 p = transform.TransformPoint((Vector3)CurveUtility.EvaluatePosition(last, 1f));
                acc += Vector3.Distance(prev, p);
                dense.Add(new DenseSample { curve = curves - 1, tc = 1f, dist = acc });
                knotDistances.Add(acc);
            }
            totalLength = acc;
            if (totalLength < 0.01f) return;

            // 2) Rééchantillonnage régulier en distance.
            int n = Mathf.Max(2, Mathf.CeilToInt(totalLength / Mathf.Max(0.25f, sampleSpacing)) + 1);
            int di = 0;
            Vector3 lastTan = transform.forward;

            for (int k = 0; k < n; k++)
            {
                float d = totalLength * k / (n - 1f);
                while (di < dense.Count - 2 && dense[di + 1].dist < d) di++;

                DenseSample a = dense[di], b = dense[di + 1];
                float span = b.dist - a.dist;
                float f = span > 1e-5f ? Mathf.Clamp01((d - a.dist) / span) : 0f;
                float tb = b.curve == a.curve ? b.tc : 1f;
                float tc = Mathf.Lerp(a.tc, tb, f);

                BezierCurve curve = sp.GetCurve(a.curve);
                Vector3 pos = transform.TransformPoint((Vector3)CurveUtility.EvaluatePosition(curve, tc));
                Vector3 tan = transform.TransformVector((Vector3)CurveUtility.EvaluateTangent(curve, tc));
                tan.y = 0f;
                if (tan.sqrMagnitude < 1e-6f) tan = lastTan;
                tan.Normalize();
                lastTan = tan;

                points.Add(new RoadPoint
                {
                    pos = pos,
                    tangent = tan,
                    right = Vector3.Cross(Vector3.up, tan).normalized,
                    dist = d,
                    s = d / totalLength
                });
            }
        }

        /// <summary>Point interpolé à une distance donnée (mètres).</summary>
        public RoadPoint SampleAt(float dist)
        {
            EnsurePath();
            int n = points.Count;
            if (n == 0) return default;
            if (n == 1) return points[0];

            float f = Mathf.Clamp(dist / Mathf.Max(0.0001f, totalLength), 0f, 1f) * (n - 1);
            int i = Mathf.Min((int)f, n - 2);
            float t = f - i;
            RoadPoint a = points[i], b = points[i + 1];

            Vector3 tan = Vector3.Lerp(a.tangent, b.tangent, t);
            if (tan.sqrMagnitude < 1e-6f) tan = a.tangent;
            tan.Normalize();

            return new RoadPoint
            {
                pos = Vector3.Lerp(a.pos, b.pos, t),
                tangent = tan,
                right = Vector3.Cross(Vector3.up, tan).normalized,
                dist = dist,
                s = dist / Mathf.Max(0.0001f, totalLength)
            };
        }

        // ---------- Profil (dimensions à une distance donnée) ----------
        public RoadProfile GetProfileAt(float dist)
        {
            EnsurePath();
            return EvaluateProfile(dist);
        }

        private RoadProfile EvaluateProfile(float dist)
        {
            var p = new RoadProfile { left = leftWidth, right = rightWidth, swHeight = 0.15f };
            float total = totalLength;

            for (int i = 0; i < sections.Count; i++)
            {
                RoadSection s = sections[i];
                if (s == null || !s.enabled) continue;

                float a = s.start * total, b = s.end * total;
                float f = Window(dist, a, b, transitionLength, s.start <= 0.0001f, s.end >= 0.9999f);
                if (f <= 0f) continue;

                if (s.overrideWidth)
                {
                    p.left = Mathf.Lerp(p.left, s.leftWidth, f);
                    p.right = Mathf.Lerp(p.right, s.rightWidth, f);
                }

                if (f > 0.5f)
                {
                    if (s.sidewalkLeft) { p.swLeft = true; p.swLeftWidth = s.sidewalkLeftWidth; p.swHeight = s.sidewalkHeight; }
                    if (s.sidewalkRight) { p.swRight = true; p.swRightWidth = s.sidewalkRightWidth; p.swHeight = s.sidewalkHeight; }
                }
            }
            return p;
        }

        private static float Window(float x, float a, float b, float blend, bool openStart, bool openEnd)
        {
            if (blend <= 0.01f) return (x >= a && x <= b) ? 1f : 0f;
            float up = openStart ? 1f : ModifierMath.Smooth01((x - (a - blend * 0.5f)) / blend);
            float down = openEnd ? 1f : 1f - ModifierMath.Smooth01((x - (b - blend * 0.5f)) / blend);
            return up * down;
        }

        /// <summary>Trouve sur quelle courbe de la spline (et à quel t) se trouve une distance en mètres. Sert à couper la route.</summary>
        public bool TryLocateOnSpline(float dist, out int curve, out float t)
        {
            curve = 0; t = 0f;
            EnsurePath();
            if (dense.Count < 2 || totalLength < 0.01f) return false;

            dist = Mathf.Clamp(dist, 0f, totalLength);
            int di = 0;
            while (di < dense.Count - 2 && dense[di + 1].dist < dist) di++;

            DenseSample a = dense[di], b = dense[di + 1];
            float span = b.dist - a.dist;
            float f = span > 1e-5f ? Mathf.Clamp01((dist - a.dist) / span) : 0f;
            float tb = b.curve == a.curve ? b.tc : 1f;
            curve = a.curve;
            t = Mathf.Lerp(a.tc, tb, f);
            return true;
        }

        /// <summary>Suivi interne des largeurs auto du node terrain (exposé pour l'outil de coupe).</summary>
        public List<float> AutoWidthTracking => lastAutoWidths;

        /// <summary>Largeurs à une extrémité (utilisé par les intersections).</summary>
        public void GetEndWidths(RoadEnd end, out float left, out float right)
        {
            EnsurePath();
            RoadProfile p = EvaluateProfile(end == RoadEnd.Start ? 0f : totalLength);
            left = p.left;
            right = p.right;
        }

        // ---------- Reconstruction ----------
        public void Rebuild()
        {
            rebuildScheduled = false;
            if (!isActiveAndEnabled) return;

            pathDirty = true;
            EnsurePath();

            if (points.Count < 2)
            {
                ClearGenerated();
                return;
            }

            BuildMesh();
            BuildSideObjects();
            SyncTerrain();
            Rebuilt?.Invoke(this);
        }

        public void ClearGenerated()
        {
            RoadUtil.RemoveChild(transform, "_RoadMesh");
            RoadUtil.RemoveChild(transform, "_SideObjects");
        }

        private void BuildMesh()
        {
            var mb = new MeshBuilder();
            int n = points.Count;
            var prof = new RoadProfile[n];
            for (int k = 0; k < n; k++) prof[k] = EvaluateProfile(points[k].dist);

            // Chaussée
            int prevL = 0, prevR = 0;
            for (int k = 0; k < n; k++)
            {
                RoadPoint p = points[k];
                RoadProfile pr = prof[k];
                Vector3 center = p.pos + Vector3.up * heightOffset;
                float v = p.dist / uvTileLength;
                int il = mb.Add(center - p.right * pr.left, new Vector2(0f, v));
                int ir = mb.Add(center + p.right * pr.right, new Vector2(1f, v));
                if (k > 0) mb.Quad(mb.road, prevL, prevR, ir, il, Vector3.up);
                prevL = il; prevR = ir;
            }

            BuildSidewalks(mb, prof, true);
            BuildSidewalks(mb, prof, false);

            Mesh mesh = mb.ToMesh("RoadMesh", transform.worldToLocalMatrix);
            LastVertexCount = mb.verts.Count;
            RoadUtil.SetMesh(transform, "_RoadMesh", mesh, roadMaterial, sidewalkMaterial, generateCollider);
        }

        private void BuildSidewalks(MeshBuilder mb, RoadProfile[] prof, bool left)
        {
            int n = points.Count;
            int k = 0;
            while (k < n)
            {
                if (!HasSidewalk(prof[k], left)) { k++; continue; }

                int start = k;
                while (k < n && HasSidewalk(prof[k], left)) k++;
                int end = k - 1;
                if (end - start < 1) continue;

                var edge = new List<Vector3>();
                var outward = new List<Vector3>();
                var widths = new List<float>();
                float sign = left ? -1f : 1f;

                for (int i = start; i <= end; i++)
                {
                    RoadPoint p = points[i];
                    RoadProfile pr = prof[i];
                    float roadW = left ? pr.left : pr.right;
                    edge.Add(p.pos + Vector3.up * heightOffset + p.right * (sign * roadW));
                    outward.Add(p.right * sign);
                    widths.Add(left ? pr.swLeftWidth : pr.swRightWidth);
                }

                float h = prof[(start + end) / 2].swHeight;
                RoadMeshUtil.AddCurbRibbon(mb, edge, outward, widths, h, uvTileLength);
            }
        }

        private static bool HasSidewalk(RoadProfile p, bool left) => left ? p.swLeft : p.swRight;

        // ---------- Objets latéraux ----------
        private void BuildSideObjects()
        {
            Transform root = RoadUtil.GetChild(transform, "_SideObjects");
            RoadUtil.ClearChildren(root);

            float total = totalLength;
            for (int ri = 0; ri < sideObjects.Count; ri++)
            {
                SideObjectRule rule = sideObjects[ri];
                if (rule == null || !rule.enabled || rule.prefabs == null || rule.prefabs.Count == 0) continue;

                float startD = rule.start * total, endD = rule.end * total;
                if (endD <= startD) continue;

                var rng = new System.Random(rule.seed * 7919 + ri * 104729);
                // Espacement fixe, ou longueur du prefab (clôtures, glissières...).
                float spacing = SideObjectPlacer.Step(rule);

                int guard = 0;
                for (float d = startD; d <= endD + 0.001f && guard < 20000; d += spacing, guard++)
                {
                    float jitter = ((float)rng.NextDouble() * 2f - 1f) * rule.spacingJitter * spacing;
                    float dd = Mathf.Clamp(d + jitter, startD, endD);

                    if (rule.side != RoadSide.Right) PlaceSideObject(rule, rng, root, dd, true);
                    if (rule.side != RoadSide.Left) PlaceSideObject(rule, rng, root, dd, false);
                }
            }
        }

        private void PlaceSideObject(SideObjectRule rule, System.Random rng, Transform root, float dist, bool left)
        {
            // Tous les tirages sont consommés même si l'emplacement est vide => résultat stable quand on change la probabilité.
            SideObjectRoll roll = SideObjectPlacer.Roll(rule, rng);
            if (!roll.Occupied(rule)) return;

            RoadPoint p = SampleAt(dist);
            RoadProfile pr = EvaluateProfile(dist);
            float sign = left ? -1f : 1f;

            float roadEdge = left ? pr.left : pr.right;
            bool hasSw = left ? pr.swLeft : pr.swRight;
            float swW = left ? pr.swLeftWidth : pr.swRightWidth;
            float anchorDist = roadEdge + (rule.anchor == SideObjectAnchor.SidewalkOuterEdge && hasSw ? swW : 0f);

            float lat = anchorDist + rule.offset + roll.lat;
            Vector3 pos = p.pos + p.right * (sign * lat) + p.tangent * (rule.forwardOffset + roll.fwd);

            float y = p.pos.y + heightOffset + rule.heightOffset;
            if (hasSw && lat < roadEdge + swW) y += pr.swHeight; // posé sur le trottoir
            if (rule.heightMode == SideObjectHeight.SnapToTerrain && RoadUtil.TrySampleTerrainHeight(pos, out float ty))
                y = ty + rule.heightOffset;
            pos.y = y;

            Vector3 faceRoad = left ? p.right : -p.right;
            // Sens de la route, éventuellement incliné pour suivre la pente (clôtures, glissières).
            Vector3 along = p.tangent;
            if (rule.alignToRoadSlope && rule.orientation == SideObjectOrientation.AlongRoad)
            {
                Vector3 a = SampleAt(dist - 1f).pos, b = SampleAt(dist + 1f).pos;
                Vector3 s = b - a;
                if (s.sqrMagnitude > 1e-6f) along = s.normalized;
            }

            SideObjectPlacer.Spawn(rule, roll, root, pos, faceRoad, along);
        }

        // ---------- Terrain ----------
        private void SyncTerrain()
        {
            if (!conformTerrain) return;
            var c = GetContainer();
            if (c == null || c.Spline == null) return;

            // Le node terrain n'est configuré qu'UNE FOIS, à sa création : vos réglages (opération, force,
            // lissage, couche, profil...) ne sont plus jamais écrasés par la route.
            var node = GetComponent<TerrainSplineProfileNode>();
            bool created = false;
            if (node == null)
            {
                node = gameObject.AddComponent<TerrainSplineProfileNode>();
                created = true;
                node.mode = SplineModifierMode.Path;
                node.operation = ModifierOperation.Blend;
                node.strength = 1f;
                node.smoothness = 2;
                node.layerOrder = terrainLayerOrder;
                node.smoothProfile = true;
                node.showGizmos = false;
                node.samplesPerCurve = 24;
            }
            node.SyncHandlesWithSpline();

            // Seul élément dérivé de la route : la largeur d'aplanissement de chaque point.
            int count = Mathf.Min(c.Spline.Count, node.pointHandles.Count);
            float maxHalf = 0f;
            for (int i = 0; i < count; i++)
            {
                float dist = i < knotDistances.Count ? knotDistances[i] : totalLength;
                RoadProfile pr = EvaluateProfile(dist);
                float half = Mathf.Max(pr.left + (pr.swLeft ? pr.swLeftWidth : 0f),
                                       pr.right + (pr.swRight ? pr.swRightWidth : 0f));
                maxHalf = Mathf.Max(maxHalf, half);

                if (autoTerrainWidth)
                {
                    float auto = half + terrainMargin;
                    float cur = node.pointHandles[i].baseWidth;
                    // Le point est "auto" s'il est nouveau ou si sa valeur est toujours celle que la route avait calculée.
                    // Si tu l'as modifié sur le node terrain, la différence est détectée et ta valeur est conservée.
                    bool untouched = i >= lastAutoWidths.Count || Mathf.Abs(cur - lastAutoWidths[i]) < 0.001f;
                    if (untouched) node.pointHandles[i].baseWidth = auto;
                    if (i < lastAutoWidths.Count) lastAutoWidths[i] = auto; else lastAutoWidths.Add(auto);
                }
            }
            if (lastAutoWidths.Count > count) lastAutoWidths.RemoveRange(count, lastAutoWidths.Count - count);

            // Le fondu extérieur n'est recalculé qu'à la création ou quand la marge change.
            if (created || Mathf.Abs(lastTerrainMargin - terrainMargin) > 1e-4f)
            {
                node.edgeFade = Mathf.Clamp01(terrainMargin / Mathf.Max(0.01f, maxHalf + terrainMargin));
                lastTerrainMargin = terrainMargin;
            }

            if (autoAddManager && points.Count > 0)
            {
                var b = new Bounds(points[0].pos, Vector3.zero);
                for (int i = 1; i < points.Count; i++) b.Encapsulate(points[i].pos);
                float pad = (maxHalf + terrainMargin) * 2f;
                b.Expand(new Vector3(pad, 100000f, pad));
                TerrainModifierManager.EnsureManagersForBounds(b);
            }

#if UNITY_EDITOR
            if (!Application.isPlaying) EditorUtility.SetDirty(node);
#endif
            TerrainModifierManager.RequestUpdate();
        }

        // ---------- Gizmos ----------
        private void OnDrawGizmos()
        {
            if (!showGizmos) return;
            EnsurePath();
            int n = points.Count;
            if (n < 2) return;

            for (int k = 1; k < n; k++)
            {
                RoadPoint a = points[k - 1], b = points[k];
                RoadProfile pa = EvaluateProfile(a.dist), pb = EvaluateProfile(b.dist);
                Vector3 up = Vector3.up * (heightOffset + 0.02f);

                Gizmos.color = new Color(1f, 0.85f, 0.2f, 0.9f);
                Gizmos.DrawLine(a.pos + up - a.right * pa.left, b.pos + up - b.right * pb.left);
                Gizmos.DrawLine(a.pos + up + a.right * pa.right, b.pos + up + b.right * pb.right);

                Gizmos.color = new Color(1f, 1f, 1f, 0.35f);
                Gizmos.DrawLine(a.pos + up, b.pos + up);

                Gizmos.color = new Color(0.4f, 0.9f, 1f, 0.9f);
                if (pa.swLeft && pb.swLeft)
                    Gizmos.DrawLine(a.pos + up - a.right * (pa.left + pa.swLeftWidth), b.pos + up - b.right * (pb.left + pb.swLeftWidth));
                if (pa.swRight && pb.swRight)
                    Gizmos.DrawLine(a.pos + up + a.right * (pa.right + pa.swRightWidth), b.pos + up + b.right * (pb.right + pb.swRightWidth));
            }
        }
    }
}
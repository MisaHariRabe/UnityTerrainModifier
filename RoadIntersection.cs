using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Splines;
using Unity.Mathematics;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace RoadTools
{
    public enum IntersectionShape
    {
        Junction,    // polygone de jonction : Y, T, X, étoile à N branches, avec coins arrondis
        Roundabout,  // anneau + branches évasées (nombre et angles de sorties libres)
        EndCap       // bout de route : disque (cul-de-sac) avec une seule entrée, îlot central optionnel
    }

    public enum IntersectionPreset
    {
        Y,
        T,
        X,
        Roundabout,        // N sorties régulières
        RoundaboutY,       // rond-point avec 3 sorties en Y
        RoundaboutT,       // rond-point avec 3 sorties en T
        EndCap             // bout de route en cercle, une seule entrée
    }

    [ExecuteAlways]
    [AddComponentMenu("Roads/Road Intersection")]
    public class RoadIntersection : MonoBehaviour
    {
        [Header("Forme")]
        public IntersectionShape shape = IntersectionShape.Junction;
        public List<IntersectionArm> arms = new List<IntersectionArm>();

        [Header("Jonction")]
        [Min(0f), Tooltip("Rayon d'arrondi des coins entre deux branches.")]
        public float cornerRadius = 6f;

        [Header("Rond-point")]
        [Min(2f)] public float outerRadius = 18f;
        [Min(1f)] public float ringWidth = 8f;
        [Min(0f), Tooltip("Hauteur de la bordure de l'îlot central.")]
        public float islandHeight = 0.15f;
        [Min(0f), Tooltip("Évasement de chaque sortie au contact de l'anneau.")]
        public float flare = 2.5f;
        [Min(0.5f), Tooltip("Longueur sur laquelle l'évasement se résorbe.")]
        public float flareLength = 6f;

        [Header("Bout de route (cercle)")]
        [Tooltip("Îlot central au milieu du cercle de retournement.")]
        public bool centerIsland = false;
        [Min(0.5f), Tooltip("Rayon de l'îlot central.")]
        public float islandRadius = 3f;

        [Header("Trottoirs")]
        public bool sidewalks = true;
        [Min(0.1f)] public float sidewalkWidth = 2.5f;
        [Min(0.01f)] public float sidewalkHeight = 0.15f;

        [Header("Rendu")]
        [Min(0f)] public float heightOffset = 0.05f;
        [Min(0.5f)] public float uvTileLength = 8f;
        public Material roadMaterial;
        public Material sidewalkMaterial;
        public bool generateCollider = true;

        [Header("Routes")]
        [Tooltip("Replace automatiquement les extrémités des routes raccordées sur leur branche.")]
        public bool autoSnapRoads = true;
        [Tooltip("Utilise la largeur des routes raccordées pour dimensionner les branches.")]
        public bool useRoadWidths = true;

        [Header("Terrain")]
        public bool conformTerrain = true;
        [Min(0f)] public float terrainMargin = 8f;
        [Tooltip("Valeur initiale appliquée à la création du node terrain (ensuite, modifiez-la directement sur le TerrainModifierNode).")]
        public int terrainLayerOrder = 1;
        [Tooltip("Le rayon d'aplanissement suit la taille de l'intersection + la marge. Décochez pour le régler à la main sur le TerrainModifierNode.")]
        public bool autoTerrainRadius = true;
        [Tooltip("Ajoute automatiquement un TerrainModifierManager aux terrains recouverts par l'intersection.")]
        public bool autoAddManager = true;

        [Header("Affichage")]
        public bool showGizmos = true;

        [HideInInspector] public List<SideObjectRule> sideObjects = new List<SideObjectRule>();

        private struct CornerPath
        {
            public PolyPath path;
            public bool sidewalk;
        }

        // Remplis à chaque reconstruction : coins (entre deux branches) et îlot central, utilisés par les sideobjects.
        private readonly List<CornerPath> corners = new List<CornerPath>();
        private PolyPath islandPath;

        private bool rebuildScheduled, rebuildQueued, pendingSnap;

        // ---------- Cycle de vie ----------
        private void OnEnable()
        {
            Road.Rebuilt += OnRoadRebuilt;
            transform.hasChanged = false;
            RequestRebuild(true);
        }

        private void OnDisable()
        {
            Road.Rebuilt -= OnRoadRebuilt;
            RoadUtil.RemoveChild(transform, "_IntersectionMesh");
            RoadUtil.RemoveChild(transform, "_SideObjects");
        }

        private void Update()
        {
            if (transform.hasChanged)
            {
                transform.hasChanged = false;
                RequestRebuild(true);
            }
        }

        private void LateUpdate()
        {
            if (!rebuildQueued) return;
            rebuildQueued = false;
            bool snap = pendingSnap; pendingSnap = false;
            Rebuild(snap);
        }

        private void OnValidate() => RequestRebuild(true);

        private void OnRoadRebuilt(Road r)
        {
            // Pas de re-snap ici (évite les boucles route <-> intersection) : on met seulement la géométrie à jour.
            foreach (var a in arms)
                if (a != null && a.road == r) { RequestRebuild(false); return; }
        }

        public void RequestRebuild(bool snap)
        {
            pendingSnap |= snap;
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
            bool snap = pendingSnap; pendingSnap = false;
            Rebuild(snap);
        }
#endif

        // ---------- Presets ----------
        public void ApplyPreset(IntersectionPreset preset, int exits = 4)
        {
            float[] yaws;
            bool wasEndCap = shape == IntersectionShape.EndCap;
            switch (preset)
            {
                case IntersectionPreset.Y: shape = IntersectionShape.Junction; yaws = new[] { 180f, 40f, 320f }; break;
                case IntersectionPreset.T: shape = IntersectionShape.Junction; yaws = new[] { 270f, 90f, 180f }; break;
                case IntersectionPreset.X: shape = IntersectionShape.Junction; yaws = new[] { 0f, 90f, 180f, 270f }; break;
                case IntersectionPreset.RoundaboutY: shape = IntersectionShape.Roundabout; yaws = new[] { 180f, 40f, 320f }; break;
                case IntersectionPreset.RoundaboutT: shape = IntersectionShape.Roundabout; yaws = new[] { 270f, 90f, 180f }; break;
                case IntersectionPreset.EndCap:
                    shape = IntersectionShape.EndCap;
                    yaws = new[] { 180f };                       // l'entrée regarde vers l'arrière de l'objet
                    if (!wasEndCap) outerRadius = 10f;           // un cul-de-sac est plus petit qu'un rond-point
                    break;
                default:
                    shape = IntersectionShape.Roundabout;
                    exits = Mathf.Clamp(exits, 1, 12);
                    yaws = new float[exits];
                    for (int i = 0; i < exits; i++) yaws[i] = 360f / exits * i;
                    break;
            }

            float len = shape != IntersectionShape.Junction ? outerRadius + flareLength + 6f : 14f;

            // Conserve les routes déjà raccordées (par index).
            var old = new List<IntersectionArm>(arms);
            arms.Clear();
            for (int i = 0; i < yaws.Length; i++)
            {
                var a = new IntersectionArm { yaw = yaws[i], length = len };
                if (i < old.Count && old[i] != null) { a.road = old[i].road; a.roadEnd = old[i].roadEnd; a.leftWidth = old[i].leftWidth; a.rightWidth = old[i].rightWidth; }
                arms.Add(a);
            }
            RequestRebuild(true);
        }

        // ---------- Géométrie des branches ----------
        private struct ArmInfo
        {
            public IntersectionArm arm;
            public Vector3 d, right;
            public float wl, wr, len;
        }

        public float EffectiveLength(IntersectionArm a) =>
            shape != IntersectionShape.Junction ? Mathf.Max(a.length, outerRadius + flareLength + 1f) : a.length;

        public Vector3 ArmDir(IntersectionArm a)
        {
            float r = a.yaw * Mathf.Deg2Rad;
            Vector3 w = transform.TransformDirection(new Vector3(Mathf.Sin(r), 0f, Mathf.Cos(r)));
            w.y = 0f;
            return w.sqrMagnitude > 1e-6f ? w.normalized : Vector3.forward;
        }

        public Vector3 ArmFar(IntersectionArm a) => transform.position + ArmDir(a) * EffectiveLength(a);

        public void GetArmWidths(IntersectionArm a, out float wl, out float wr)
        {
            wl = a.leftWidth; wr = a.rightWidth;
            if (useRoadWidths && a.road != null)
            {
                a.road.GetEndWidths(a.roadEnd, out float l, out float r);
                // Route raccordée par son DÉBUT : elle part du centre => gauche/droite identiques à la branche.
                // Raccordée par sa FIN : elle arrive vers le centre => gauche/droite inversées.
                if (a.roadEnd == RoadEnd.Start) { wl = l; wr = r; } else { wl = r; wr = l; }
            }
        }

        private List<ArmInfo> GetInfos()
        {
            var sorted = new List<IntersectionArm>();
            foreach (var a in arms) if (a != null) sorted.Add(a);
            sorted.Sort((x, y) => Mathf.Repeat(x.yaw, 360f).CompareTo(Mathf.Repeat(y.yaw, 360f)));

            var list = new List<ArmInfo>(sorted.Count);
            foreach (var a in sorted)
            {
                GetArmWidths(a, out float wl, out float wr);
                Vector3 d = ArmDir(a);
                list.Add(new ArmInfo { arm = a, d = d, right = Vector3.Cross(Vector3.up, d).normalized, wl = Mathf.Max(0.5f, wl), wr = Mathf.Max(0.5f, wr), len = EffectiveLength(a) });
            }
            return list;
        }

        // ---------- Raccord des routes ----------
        public void SnapRoads()
        {
            foreach (var a in arms) if (a != null && a.road != null) SnapRoad(a);
        }

        public void SnapRoad(IntersectionArm arm)
        {
            Road road = arm.road;
            if (road == null) return;
            var container = road.GetComponent<SplineContainer>();
            if (container == null || container.Spline == null || container.Spline.Count < 2) return;

            Spline sp = container.Spline;
            int idx = arm.roadEnd == RoadEnd.Start ? 0 : sp.Count - 1;

            Vector3 dir = ArmDir(arm);
            Vector3 far = ArmFar(arm);
            far.y = transform.position.y;
            Vector3 fwd = arm.roadEnd == RoadEnd.Start ? dir : -dir;

            Vector3 localPos = road.transform.InverseTransformPoint(far);
            Quaternion localRot = Quaternion.Inverse(road.transform.rotation) * Quaternion.LookRotation(fwd, Vector3.up);

            BezierKnot k = sp[idx];
            bool same = math.distance(k.Position, (float3)localPos) < 0.002f
                        && Quaternion.Angle((Quaternion)k.Rotation, localRot) < 0.05f
                        && sp.GetTangentMode(idx) == TangentMode.Mirrored;
            if (same) return;

#if UNITY_EDITOR
            if (!Application.isPlaying) Undo.RecordObject(container, "Raccorder la route");
#endif
            float mag = math.length(k.TangentOut);
            if (mag < 1f) mag = 6f;

            k.Position = localPos;
            k.Rotation = localRot;
            k.TangentIn = new float3(0f, 0f, -mag);
            k.TangentOut = new float3(0f, 0f, mag);
            sp.SetKnot(idx, k);
            sp.SetTangentMode(idx, TangentMode.Mirrored);
        }

        // ---------- Reconstruction ----------
        public void Rebuild(bool snap = true)
        {
            rebuildScheduled = false;
            if (!isActiveAndEnabled) return;

            if (snap && autoSnapRoads) SnapRoads();

            corners.Clear();
            islandPath = null;

            var mb = new MeshBuilder();
            if (shape == IntersectionShape.Junction) BuildJunction(mb);
            else BuildRoundabout(mb);

            if (mb.verts.Count == 0) { RoadUtil.RemoveChild(transform, "_IntersectionMesh"); }
            else
            {
                Mesh mesh = mb.ToMesh("IntersectionMesh", transform.worldToLocalMatrix);
                RoadUtil.SetMesh(transform, "_IntersectionMesh", mesh, roadMaterial, sidewalkMaterial, generateCollider);
            }

            BuildSideObjects();
            SyncTerrain();
        }

        // ---------- Objets latéraux ----------
        /// <summary>Distance du centre à partir de laquelle on place les objets sur les coins (utilisé pour les gizmos de l'éditeur).</summary>
        public void GetCornerAnchors(List<KeyValuePair<IntersectionArm, Vector3>> result)
        {
            result.Clear();
            var infos = GetInfos();
            int n = infos.Count;
            if (n < 1 || (n < 2 && shape == IntersectionShape.Junction)) return;

            Vector3 c = transform.position + Vector3.up * heightOffset;
            for (int i = 0; i < n; i++)
            {
                ArmInfo A = infos[i], B = infos[(i + 1) % n];
                float gap = Vector3.SignedAngle(A.d, B.d, Vector3.up);
                if (gap <= 0f) gap += 360f;
                Vector3 mid = Quaternion.AngleAxis(gap * 0.5f, Vector3.up) * A.d;
                float dist = shape != IntersectionShape.Junction
                    ? outerRadius + sidewalkWidth * 0.5f
                    : Mathf.Max(3f, Mathf.Min(A.len, B.len) * 0.45f);
                result.Add(new KeyValuePair<IntersectionArm, Vector3>(A.arm, c + mid * dist));
            }
        }

        private void BuildSideObjects()
        {
            bool any = false;
            if (sideObjects != null)
                foreach (var r in sideObjects) if (SideObjectPlacer.HasAnyPrefab(r)) { any = true; break; }

            if (!any)
            {
                RoadUtil.RemoveChild(transform, "_SideObjects");
                return;
            }

            Transform root = RoadUtil.GetChild(transform, "_SideObjects");
            RoadUtil.ClearChildren(root);

            for (int ri = 0; ri < sideObjects.Count; ri++)
            {
                SideObjectRule rule = sideObjects[ri];
                if (!SideObjectPlacer.HasAnyPrefab(rule)) continue;

                if (rule.zone == IntersectionZone.Island)
                {
                    if (islandPath != null)
                        SideObjectPlacer.PlaceAlongPath(rule, ri, 0, root, islandPath, false, 0f, 0f);
                }
                else
                {
                    for (int ci = 0; ci < corners.Count; ci++)
                        SideObjectPlacer.PlaceAlongPath(rule, ri, ci + 1, root, corners[ci].path,
                                                        corners[ci].sidewalk, sidewalkWidth, sidewalkHeight);
                }
            }
        }

        private Vector2 PlanarUV(Vector3 p) => new Vector2(p.x / uvTileLength, p.z / uvTileLength);

        // ---------- Jonction ----------
        private void BuildJunction(MeshBuilder mb)
        {
            var infos = GetInfos();
            int n = infos.Count;
            if (n < 2) return;

            Vector3 c = transform.position;
            c.y += heightOffset;
            Vector3 up = Vector3.up;

            var outline = new List<Vector3>();
            var polys = new List<List<Vector3>>();

            for (int i = 0; i < n; i++)
            {
                ArmInfo A = infos[i], B = infos[(i + 1) % n];
                Vector3 aL = c + A.d * A.len - A.right * A.wl;
                Vector3 aR = c + A.d * A.len + A.right * A.wr;
                Vector3 bL = c + B.d * B.len - B.right * B.wl;

                outline.Add(aL);
                outline.Add(aR);

                var poly = new List<Vector3> { aR };
                if (TryCorner(c, A, B, cornerRadius, out Vector3 Ti, out Vector3 M, out Vector3 Tj))
                {
                    if (Vector3.Distance(Ti, Tj) < 0.01f)
                    {
                        outline.Add(M); poly.Add(M);
                    }
                    else
                    {
                        const int S = 8;
                        for (int s = 0; s <= S; s++)
                        {
                            float t = s / (float)S, u = 1f - t;
                            Vector3 p = u * u * Ti + 2f * u * t * M + t * t * Tj;
                            outline.Add(p); poly.Add(p);
                        }
                    }
                }
                poly.Add(bL);
                polys.Add(poly);
            }

            // Remplissage en éventail depuis le centre.
            int center = mb.Add(c, PlanarUV(c));
            var idx = new int[outline.Count];
            for (int k = 0; k < idx.Length; k++) idx[k] = mb.Add(outline[k], PlanarUV(outline[k]));
            for (int k = 0; k < idx.Length; k++)
                mb.Tri(mb.road, center, idx[k], idx[(k + 1) % idx.Length], up);

            // Un trottoir par coin, activable branche par branche (arm.sidewalkToNext).
            for (int i = 0; i < polys.Count; i++)
            {
                bool sw = sidewalks && n >= 3 && infos[i].arm != null && infos[i].arm.sidewalkToNext;
                if (sw) RoadMeshUtil.AddClockwisePolylineCurb(mb, polys[i], sidewalkWidth, sidewalkHeight, uvTileLength);
                corners.Add(new CornerPath { path = new PolyPath(polys[i]), sidewalk = sw });
            }
        }

        private static float Cross2(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

        /// <summary>
        /// Coin entre la branche A et la suivante B (sens horaire) : point de mitre M et points de tangence Ti/Tj
        /// d'une courbe de Bézier quadratique (contrôle en M) qui arrondit le coin.
        /// </summary>
        private static bool TryCorner(Vector3 c, ArmInfo A, ArmInfo B, float radius, out Vector3 Ti, out Vector3 M, out Vector3 Tj)
        {
            Ti = M = Tj = Vector3.zero;

            float gap = Vector3.SignedAngle(A.d, B.d, Vector3.up);
            if (gap < 0f) gap += 360f;
            if (gap >= 170f || gap <= 10f) return false; // quasi aligné : côté droit

            Vector2 dA = new Vector2(A.d.x, A.d.z), dB = new Vector2(B.d.x, B.d.z);
            Vector2 nA = new Vector2(A.right.x, A.right.z), nB = new Vector2(B.right.x, B.right.z);
            Vector2 rhs = -(nA * A.wr + nB * B.wl);

            float det = Cross2(dA, -dB);
            if (Mathf.Abs(det) < 1e-4f) return false;

            float u = Cross2(rhs, -dB) / det;
            float v = Cross2(dA, rhs) / det;
            if (u <= 0f || v <= 0f) return false;

            M = c + A.right * A.wr + A.d * u;

            if (radius < 0.05f)
            {
                Ti = Tj = M;
                return true;
            }

            float t = radius / Mathf.Tan(gap * 0.5f * Mathf.Deg2Rad);
            float tMax = Mathf.Min(A.len - u - 0.05f, B.len - v - 0.05f);
            if (tMax < 0.3f) return false;
            t = Mathf.Min(t, tMax);

            Ti = c + A.right * A.wr + A.d * (u + t);
            Tj = c - B.right * B.wl + B.d * (v + t);
            return true;
        }

        // ---------- Rond-point ----------
        private struct SpokeGeom
        {
            public Vector3 rFar, rMid, rRing, lFar, lMid, lRing;
        }

        private void BuildRoundabout(MeshBuilder mb)
        {
            var infos = GetInfos();
            int n = infos.Count;
            if (n == 0) return;

            Vector3 c = transform.position;
            c.y += heightOffset;
            Vector3 up = Vector3.up;

            float R = outerRadius;
            // Bout de route : disque plein (r minuscule pour éviter un trou), ou anneau autour d'un îlot optionnel.
            float r = shape == IntersectionShape.EndCap
                ? (centerIsland ? Mathf.Clamp(islandRadius, 0.5f, R - 0.5f) : 0.05f)
                : Mathf.Clamp(outerRadius - ringWidth, 0.5f, R - 0.5f);
            const int SEG = 72;

            // Anneau
            int pi = 0, po = 0;
            for (int k = 0; k <= SEG; k++)
            {
                float a = k / (float)SEG * Mathf.PI * 2f;
                Vector3 dir = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
                float vv = k / (float)SEG * (2f * Mathf.PI * R / uvTileLength);
                int ii = mb.Add(c + dir * r, new Vector2(0f, vv));
                int io = mb.Add(c + dir * R, new Vector2(1f, vv));
                if (k > 0) mb.Quad(mb.road, pi, po, io, ii, up);
                pi = ii; po = io;
            }

            // Îlot central : bordure + dessus
            if (r > 0.8f)
            {
                // Chemin des sideobjects de l'îlot : parcours anti-horaire, l'« extérieur » (gauche) pointe vers le centre.
                var circle = new List<Vector3>(SEG + 1);
                for (int k = 0; k <= SEG; k++)
                {
                    float ca = (1f - k / (float)SEG) * Mathf.PI * 2f;
                    circle.Add(c + new Vector3(Mathf.Sin(ca) * r, islandHeight, Mathf.Cos(ca) * r));
                }
                islandPath = new PolyPath(circle);

                float h = islandHeight;
                int topCenter = mb.Add(c + up * h, new Vector2(0.5f, 0.5f));
                int pb = 0, pt = 0, pTop = 0;
                for (int k = 0; k <= SEG; k++)
                {
                    float a = k / (float)SEG * Mathf.PI * 2f;
                    Vector3 dir = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
                    Vector3 b = c + dir * r;
                    int ib = mb.Add(b, new Vector2(k / (float)SEG * 8f, 0f));
                    int it = mb.Add(b + up * h, new Vector2(k / (float)SEG * 8f, 0.15f));
                    int iTop = mb.Add(b + up * h, new Vector2(0.5f + dir.x * 0.5f, 0.5f + dir.z * 0.5f));
                    if (k > 0)
                    {
                        mb.Quad(mb.side, pb, ib, it, pt, dir);
                        mb.Tri(mb.side, topCenter, pTop, iTop, up);
                    }
                    pb = ib; pt = it; pTop = iTop;
                }
            }

            // Sorties évasées
            var geoms = new SpokeGeom[n];
            for (int i = 0; i < n; i++)
            {
                ArmInfo I = infos[i];
                float fl = Mathf.Min(flare, R * 0.4f);
                float flLen = Mathf.Clamp(flareLength, 0.5f, Mathf.Max(0.5f, I.len - R - 0.5f));
                float hwL = Mathf.Min(I.wl + fl, R * 0.95f);
                float hwR = Mathf.Min(I.wr + fl, R * 0.95f);

                const int K = 6;
                var rows = new int[3][];
                for (int row = 0; row < 3; row++)
                {
                    rows[row] = new int[K + 1];
                    for (int q = 0; q <= K; q++)
                    {
                        float t = q / (float)K;
                        Vector3 pt;
                        if (row == 0)
                        {
                            float lat = Mathf.Lerp(-hwL, hwR, t);
                            float dd = Mathf.Sqrt(Mathf.Max(0.01f, R * R - lat * lat));
                            pt = c + I.d * dd + I.right * lat;
                        }
                        else
                        {
                            float lat = Mathf.Lerp(-I.wl, I.wr, t);
                            float dd = row == 1 ? R + flLen : I.len;
                            pt = c + I.d * dd + I.right * lat;
                        }
                        rows[row][q] = mb.Add(pt, PlanarUV(pt));
                    }
                }
                for (int row = 0; row < 2; row++)
                    for (int q = 0; q < K; q++)
                        mb.Quad(mb.road, rows[row][q], rows[row][q + 1], rows[row + 1][q + 1], rows[row + 1][q], up);

                float ddR = Mathf.Sqrt(Mathf.Max(0.01f, R * R - hwR * hwR));
                float ddL = Mathf.Sqrt(Mathf.Max(0.01f, R * R - hwL * hwL));
                geoms[i] = new SpokeGeom
                {
                    rFar = c + I.d * I.len + I.right * I.wr,
                    rMid = c + I.d * (R + flLen) + I.right * I.wr,
                    rRing = c + I.d * ddR + I.right * hwR,
                    lFar = c + I.d * I.len - I.right * I.wl,
                    lMid = c + I.d * (R + flLen) - I.right * I.wl,
                    lRing = c + I.d * ddL - I.right * hwL
                };
            }

            // Trottoirs extérieurs entre deux sorties consécutives (sens horaire)
            // Coins extérieurs : trottoir activable par coin, et chemin pour les sideobjects.
            {
                for (int i = 0; i < n; i++)
                {
                    SpokeGeom A = geoms[i], B = geoms[(i + 1) % n];
                    float phiA = Mathf.Atan2(A.rRing.x - c.x, A.rRing.z - c.z);
                    float phiB = Mathf.Atan2(B.lRing.x - c.x, B.lRing.z - c.z);
                    if (phiB <= phiA) phiB += Mathf.PI * 2f;
                    float diff = phiB - phiA;
                    if (n > 1 && diff > Mathf.PI * 1.8f) continue; // évasements qui se chevauchent

                    var poly = new List<Vector3> { A.rFar, A.rMid, A.rRing };
                    int steps = Mathf.Max(2, Mathf.CeilToInt(diff * R / 2f));
                    for (int s = 1; s < steps; s++)
                    {
                        float phi = phiA + diff * s / steps;
                        poly.Add(c + new Vector3(Mathf.Sin(phi), 0f, Mathf.Cos(phi)) * R);
                    }
                    poly.Add(B.lRing); poly.Add(B.lMid); poly.Add(B.lFar);

                    bool sw = sidewalks && infos[i].arm != null && infos[i].arm.sidewalkToNext;
                    if (sw) RoadMeshUtil.AddClockwisePolylineCurb(mb, poly, sidewalkWidth, sidewalkHeight, uvTileLength);
                    corners.Add(new CornerPath { path = new PolyPath(poly), sidewalk = sw });
                }
            }
        }

        // ---------- Terrain ----------
        public float Extent()
        {
            float e = shape != IntersectionShape.Junction ? outerRadius + flare : 0f;
            foreach (var a in arms)
            {
                if (a == null) continue;
                GetArmWidths(a, out float wl, out float wr);
                float len = EffectiveLength(a);
                float w = Mathf.Max(wl, wr);
                e = Mathf.Max(e, Mathf.Sqrt(len * len + w * w));
            }
            return Mathf.Max(e, 4f);
        }

        private void SyncTerrain()
        {
            if (!conformTerrain) return;

            // Configuré une seule fois à la création : vos réglages ne sont plus écrasés ensuite.
            var node = GetComponent<TerrainModifierNode>();
            bool created = false;
            if (node == null)
            {
                node = gameObject.AddComponent<TerrainModifierNode>();
                created = true;
                node.operation = ModifierOperation.Blend;
                node.strength = 1f;
                node.smoothness = 2;
                node.layerOrder = terrainLayerOrder;
                node.showGizmos = false;
            }

            float extent = Extent();
            float k = Mathf.Clamp(extent / (extent + terrainMargin), 0.05f, 0.95f);

            if (autoAddManager)
            {
                float side = (extent + terrainMargin) * 2f;
                TerrainModifierManager.EnsureManagersForBounds(new Bounds(transform.position, new Vector3(side, 100000f, side)));
            }

            // Seul élément dérivé de l'intersection : le rayon.
            if (autoTerrainRadius || created) node.radius = extent + terrainMargin;

            // La courbe plateau n'est régénérée que si elle est encore celle que nous avons créée
            // (une courbe modifiée ou un préréglage de l'inspecteur est conservé tel quel).
            if (created || IsGeneratedPlateau(node.falloffCurve))
                node.falloffCurve = new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(k, 1f), new Keyframe(1f, 0f));

#if UNITY_EDITOR
            if (!Application.isPlaying) EditorUtility.SetDirty(node);
#endif
            TerrainModifierManager.RequestUpdate();
        }

        private static bool IsGeneratedPlateau(AnimationCurve c)
        {
            if (c == null || c.length != 3) return false;
            Keyframe a = c[0], b = c[1], d = c[2];
            return Mathf.Approximately(a.time, 0f) && Mathf.Approximately(a.value, 1f)
                && Mathf.Approximately(b.value, 1f)
                && Mathf.Approximately(d.time, 1f) && Mathf.Approximately(d.value, 0f)
                && Mathf.Abs(a.outTangent) < 1e-4f && Mathf.Abs(b.inTangent) < 1e-4f
                && Mathf.Abs(b.outTangent) < 1e-4f && Mathf.Abs(d.inTangent) < 1e-4f;
        }

        // ---------- Gizmos ----------
        private void OnDrawGizmos()
        {
            if (!showGizmos) return;
            Vector3 c = transform.position;

            if (shape != IntersectionShape.Junction)
            {
                Gizmos.color = new Color(1f, 0.55f, 0.1f, 0.8f);
                DrawCircle(c, outerRadius);
                if (shape == IntersectionShape.Roundabout) DrawCircle(c, Mathf.Max(0.5f, outerRadius - ringWidth));
                else if (centerIsland) DrawCircle(c, Mathf.Clamp(islandRadius, 0.5f, outerRadius - 0.5f));
            }

            foreach (var a in arms)
            {
                if (a == null) continue;
                GetArmWidths(a, out float wl, out float wr);
                Vector3 d = ArmDir(a);
                Vector3 right = Vector3.Cross(Vector3.up, d);
                Vector3 far = ArmFar(a);

                Gizmos.color = new Color(1f, 0.85f, 0.2f, 0.9f);
                Gizmos.DrawLine(c, far);
                Gizmos.DrawLine(far - right * wl, far + right * wr);
                Gizmos.color = a.road != null ? new Color(0.3f, 1f, 0.4f) : new Color(1f, 0.4f, 0.3f);
                Gizmos.DrawSphere(far, 0.5f);
            }
        }

        private static void DrawCircle(Vector3 c, float r)
        {
            const int seg = 48;
            Vector3 prev = c + new Vector3(0, 0.1f, r);
            for (int i = 1; i <= seg; i++)
            {
                float a = i / (float)seg * Mathf.PI * 2f;
                Vector3 p = c + new Vector3(Mathf.Sin(a) * r, 0.1f, Mathf.Cos(a) * r);
                Gizmos.DrawLine(prev, p);
                prev = p;
            }
        }
    }
}
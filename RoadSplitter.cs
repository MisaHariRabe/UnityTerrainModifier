#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Splines;
using Unity.Mathematics;

namespace RoadTools
{
    /// <summary>
    /// Coupe une route en deux à une distance donnée. La forme de la spline est conservée exactement
    /// (la courbe coupée est subdivisée par l'algorithme de De Casteljau), les sections et règles de sideobjects
    /// sont redécoupées, le node terrain est partagé entre les deux moitiés et les intersections raccordées
    /// à la fin de la route restent raccordées (à la nouvelle moitié).
    /// </summary>
    public static class RoadSplitter
    {
        public static readonly string[] PresetLabels =
        {
            "Aucune (juste couper)", "Intersection T", "Intersection X", "Intersection Y",
            "Rond-point (4 sorties)", "Rond-point T", "Rond-point Y"
        };

        private static readonly IntersectionPreset[] Presets =
        {
            IntersectionPreset.T, IntersectionPreset.X, IntersectionPreset.Y,
            IntersectionPreset.Roundabout, IntersectionPreset.RoundaboutT, IntersectionPreset.RoundaboutY
        };

        private static float3 Lerp(float3 a, float3 b, float t) => math.lerp(a, b, t);

        /// <summary>
        /// Coupe la route à `dist` mètres. Retourne la nouvelle route (la seconde moitié) ou null en cas d'échec.
        /// presetIndex : 0 = aucune intersection, 1.. = préréglage de PresetLabels.
        /// </summary>
        public static Road Split(Road road, float dist, int presetIndex, out RoadIntersection hub)
        {
            hub = null;
            var container = road.GetContainer();
            if (container == null || container.Spline == null) return null;
            Spline sp = container.Spline;

            if (sp.Closed) { Debug.LogWarning("[Road Splitter] Une route bouclée ne peut pas être coupée : ouvrez-la d'abord."); return null; }
            float total = road.TotalLength;
            if (sp.Count < 2 || total < 2.5f) return null;
            if (dist < 1f || dist > total - 1f)
            {
                Debug.LogWarning("[Road Splitter] Point de coupe trop proche d'une extrémité (minimum 1 m).");
                return null;
            }
            if (!road.TryLocateOnSpline(dist, out int ci, out float t)) return null;

            int n = sp.Count;
            float s = dist / total;
            RoadPoint cutPoint = road.SampleAt(dist);

            // ---- État d'origine, relevé AVANT toute modification ----
            var knots = new BezierKnot[n];
            var modes = new TangentMode[n];
            for (int i = 0; i < n; i++) { knots[i] = sp[i]; modes[i] = sp.GetTangentMode(i); }
            BezierCurve curve = sp.GetCurve(ci);

            var node = road.GetComponent<TerrainSplineProfileNode>();
            List<PointProfileHandle> oldHandles = null;
            if (node != null)
            {
                node.SyncHandlesWithSpline();
                oldHandles = new List<PointProfileHandle>();
                foreach (var h in node.pointHandles) oldHandles.Add(new PointProfileHandle(h));
            }
            var oldAuto = new List<float>(road.AutoWidthTracking);

            // ---- De Casteljau : coupe de la courbe en t ----
            float3 a = Lerp(curve.P0, curve.P1, t), b = Lerp(curve.P1, curve.P2, t), c = Lerp(curve.P2, curve.P3, t);
            float3 d = Lerp(a, b, t), e = Lerp(b, c, t);
            float3 m = Lerp(d, e, t);
            // Courbe gauche : P0, a, d, m   —   courbe droite : m, e, c, P3

            float3 vOut = e - m, vIn = d - m;
            float3 fwd = math.lengthsq(vOut) > 1e-8f ? math.normalize(vOut) : (math.lengthsq(vIn) > 1e-8f ? math.normalize(-vIn) : new float3(0, 0, 1));
            quaternion rot = quaternion.LookRotationSafe(fwd, math.up());
            quaternion invRot = math.inverse(rot);
            BezierKnot kM = new BezierKnot(m, math.mul(invRot, vIn), math.mul(invRot, vOut), rot);

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Couper la route");
            int group = Undo.GetCurrentGroup();

            // ---- Nouvelle route (seconde moitié) ----
            var go = new GameObject(road.name + " (2)");
            Undo.RegisterCreatedObjectUndo(go, "Couper la route");
            go.transform.SetParent(road.transform.parent, false);
            go.transform.SetSiblingIndex(road.transform.GetSiblingIndex() + 1);
            go.transform.localPosition = road.transform.localPosition;
            go.transform.localRotation = road.transform.localRotation;
            go.transform.localScale = road.transform.localScale;

            Road nr = go.AddComponent<Road>();                      // ajoute aussi le SplineContainer
            EditorUtility.CopySerialized(road, nr);                  // mêmes réglages (matériaux, largeurs, sections, règles...)
            var nc = go.GetComponent<SplineContainer>();
            Spline nsp = nc.Spline;
            nsp.Clear();

            // Knot de coupe, puis les knots restants.
            nsp.Add(kM, TangentMode.Broken);
            for (int i = ci + 1; i < n; i++)
            {
                BezierKnot k = knots[i];
                TangentMode mode = modes[i];
                if (i == ci + 1)
                {
                    // Tangente d'entrée = (c - P3), exprimée dans le repère du knot.
                    k.TangentIn = math.mul(math.inverse(k.Rotation), c - curve.P3);
                    mode = TangentMode.Broken;
                }
                nsp.Add(k, mode);
            }
            nsp.Closed = false;

            // ---- Route d'origine : première moitié ----
            Undo.RecordObject(container, "Couper la route");
            Undo.RecordObject(road, "Couper la route");
            if (node != null) Undo.RecordObject(node, "Couper la route");

            for (int i = n - 1; i > ci; i--) sp.RemoveAt(i);

            BezierKnot kA = knots[ci];
            kA.TangentOut = math.mul(math.inverse(kA.Rotation), a - curve.P0);
            sp.SetTangentMode(ci, TangentMode.Broken);
            sp.SetKnot(ci, kA);
            sp.Add(kM, TangentMode.Broken);

            // ---- Node terrain : profils découpés et interpolés ----
            PointProfileHandle hm = null;
            if (oldHandles != null && oldHandles.Count == n)
            {
                hm = LerpHandle(oldHandles[ci], oldHandles[ci + 1], t);

                node.SyncHandlesWithSpline();
                node.pointHandles.Clear();
                for (int i = 0; i <= ci; i++) node.pointHandles.Add(new PointProfileHandle(oldHandles[i]));
                node.pointHandles.Add(new PointProfileHandle(hm));
                EditorUtility.SetDirty(node);

                var newNode = go.AddComponent<TerrainSplineProfileNode>();
                EditorUtility.CopySerialized(node, newNode);
                newNode.pointHandles = new List<PointProfileHandle> { new PointProfileHandle(hm) };
                for (int i = ci + 1; i < n; i++) newNode.pointHandles.Add(new PointProfileHandle(oldHandles[i]));
            }

            // Suivi des largeurs automatiques.
            if (oldAuto.Count == n)
            {
                float am = Mathf.Lerp(oldAuto[ci], oldAuto[ci + 1], t);
                var la = road.AutoWidthTracking; la.Clear();
                for (int i = 0; i <= ci; i++) la.Add(oldAuto[i]);
                la.Add(am);
                var lb = nr.AutoWidthTracking; lb.Clear();
                lb.Add(am);
                for (int i = ci + 1; i < n; i++) lb.Add(oldAuto[i]);
            }
            else { road.AutoWidthTracking.Clear(); nr.AutoWidthTracking.Clear(); }

            // ---- Sections et règles de sideobjects redécoupées ----
            RemapRanges(road, 0f, s);
            RemapRanges(nr, s, 1f);

            // ---- Intersections raccordées à l'ancienne FIN : elles passent à la seconde moitié ----
            foreach (var h in Object.FindObjectsByType<RoadIntersection>(FindObjectsSortMode.None))
            {
                bool changed = false;
                foreach (var arm in h.arms)
                {
                    if (arm != null && arm.road == road && arm.roadEnd == RoadEnd.End)
                    {
                        if (!changed) Undo.RecordObject(h, "Couper la route");
                        arm.road = nr;
                        changed = true;
                    }
                }
                if (changed) { EditorUtility.SetDirty(h); h.RequestRebuild(false); }
            }

            // ---- Intersection optionnelle au point de coupe ----
            if (presetIndex > 0 && presetIndex <= Presets.Length)
                hub = CreateHub(road, nr, container.transform.TransformPoint((Vector3)m), cutPoint.tangent, Presets[presetIndex - 1]);

            EditorUtility.SetDirty(container);
            EditorUtility.SetDirty(road);
            EditorUtility.SetDirty(nr);
            road.RequestRebuild();
            nr.RequestRebuild();
            TerrainModifierManager.RequestUpdate();
            Undo.CollapseUndoOperations(group);
            return nr;
        }

        // ------------------------------------------------------------------
        private static PointProfileHandle LerpHandle(PointProfileHandle a, PointProfileHandle b, float t) => new PointProfileHandle
        {
            peakOffsetY = Mathf.Lerp(a.peakOffsetY, b.peakOffsetY, t),
            baseWidth = Mathf.Lerp(a.baseWidth, b.baseWidth, t),
            edgePositionT = Mathf.Lerp(a.edgePositionT, b.edgePositionT, t),
            edgeOffsetY = Mathf.Lerp(a.edgeOffsetY, b.edgeOffsetY, t)
        };

        /// <summary>Ramène les plages 0..1 (sections, règles) à la portion [lo, hi] de la route d'origine.</summary>
        private static void RemapRanges(Road r, float lo, float hi)
        {
            float span = Mathf.Max(1e-4f, hi - lo);
            for (int i = r.sections.Count - 1; i >= 0; i--)
            {
                var x = r.sections[i];
                if (x == null) continue;
                if (!Remap(ref x.start, ref x.end, lo, hi, span)) r.sections.RemoveAt(i);
            }
            for (int i = r.sideObjects.Count - 1; i >= 0; i--)
            {
                var x = r.sideObjects[i];
                if (x == null) continue;
                if (!Remap(ref x.start, ref x.end, lo, hi, span)) r.sideObjects.RemoveAt(i);
            }
        }

        private static bool Remap(ref float start, ref float end, float lo, float hi, float span)
        {
            if (end <= lo || start >= hi) return false;
            float ns = (Mathf.Max(start, lo) - lo) / span;
            float ne = (Mathf.Min(end, hi) - lo) / span;
            start = Mathf.Clamp01(ns);
            end = Mathf.Clamp01(ne);
            return end - start > 1e-4f;
        }

        /// <summary>
        /// Termine une route par un bout de route en cercle (cul-de-sac), raccordé à son début ou à sa fin.
        /// Le cercle est placé dans le prolongement de la route : la route elle-même ne bouge pas.
        /// </summary>
        public static RoadIntersection AddEndCap(Road road, RoadEnd end)
        {
            if (road.Points.Count < 2) return null;
            RoadPoint p = road.SampleAt(end == RoadEnd.Start ? 0f : road.TotalLength);
            Vector3 outward = end == RoadEnd.Start ? -p.tangent : p.tangent;   // direction qui s'éloigne de la route
            outward.y = 0f;
            if (outward.sqrMagnitude < 1e-6f) return null;
            outward.Normalize();

            var go = new GameObject("Bout de route");
            Undo.RegisterCreatedObjectUndo(go, "Bout de route");
            go.transform.SetParent(road.transform.parent, false);
            go.transform.rotation = Quaternion.Euler(0f, Mathf.Atan2(outward.x, outward.z) * Mathf.Rad2Deg, 0f);   // avant du hub = vers l'extérieur

            var hub = go.AddComponent<RoadIntersection>();
            hub.ApplyPreset(IntersectionPreset.EndCap);
            var arm = hub.arms[0];                                   // yaw 180 : regarde vers la route
            arm.road = road;
            arm.roadEnd = end;
            road.GetEndWidths(end, out float l, out float r);
            arm.leftWidth = end == RoadEnd.Start ? l : r;
            arm.rightWidth = end == RoadEnd.Start ? r : l;

            // Le centre est reculé de la longueur de la branche : l'extrémité de la route tombe pile sur le bout de la branche.
            go.transform.position = p.pos + outward * hub.EffectiveLength(arm);
            hub.roadMaterial = road.roadMaterial;
            hub.sidewalkMaterial = road.sidewalkMaterial;
            hub.RequestRebuild(true);
            return hub;
        }

        // ------------------------------------------------------------------
        private static RoadIntersection CreateHub(Road a, Road b, Vector3 worldPos, Vector3 tangent, IntersectionPreset preset)
        {
            var go = new GameObject(preset.ToString().Replace("Roundabout", "Rond-point ") + " (coupe)");
            Undo.RegisterCreatedObjectUndo(go, "Créer l'intersection");
            go.transform.SetParent(a.transform.parent, false);

            tangent.y = 0f;
            if (tangent.sqrMagnitude < 1e-6f) tangent = Vector3.forward;
            tangent.Normalize();

            // Y : la route arrive par l'arrière (branche 180°) et repart sur la branche à 40°.
            // Autres : route traversante, branche à 270° (côté A) et 90° (côté B), la branche restante est libre.
            bool y = preset == IntersectionPreset.Y || preset == IntersectionPreset.RoundaboutY;
            float theta = y ? Mathf.Atan2(tangent.x, tangent.z) : Mathf.Atan2(-tangent.z, tangent.x);
            go.transform.SetPositionAndRotation(worldPos, Quaternion.Euler(0f, theta * Mathf.Rad2Deg, 0f));

            var hub = go.AddComponent<RoadIntersection>();
            hub.ApplyPreset(preset, 4);

            float yawA = y ? 180f : 270f, yawB = y ? 40f : 90f;
            var armA = hub.arms.Find(x => Mathf.Abs(Mathf.DeltaAngle(x.yaw, yawA)) < 1f);
            var armB = hub.arms.Find(x => Mathf.Abs(Mathf.DeltaAngle(x.yaw, yawB)) < 1f);
            if (armA != null) { armA.road = a; armA.roadEnd = RoadEnd.End; }
            if (armB != null) { armB.road = b; armB.roadEnd = RoadEnd.Start; }

            // Mêmes matériaux que la route.
            hub.roadMaterial = a.roadMaterial;
            hub.sidewalkMaterial = a.sidewalkMaterial;

            hub.RequestRebuild(true);
            return hub;
        }
    }

    /// <summary>Outil scène : clique sur la route pour la couper (ou utilise le curseur de l'inspecteur).</summary>
    [InitializeOnLoad]
    public static class RoadSplitterTool
    {
        private static Road target;
        private static bool mouseMode;
        private static float sliderDist = -1f;
        private static int presetIdx;
        private static bool showSection = true;

        static RoadSplitterTool() { SceneView.duringSceneGui += OnScene; }

        [MenuItem("GameObject/Road System/Couper la route (outil)", false, 32)]
        private static void StartFromMenu()
        {
            target = Selection.activeGameObject.GetComponent<Road>();
            mouseMode = target != null;
            SceneView.RepaintAll();
        }

        [MenuItem("GameObject/Road System/Couper la route (outil)", true)]
        private static bool StartFromMenuValidate() =>
            Selection.activeGameObject != null && Selection.activeGameObject.GetComponent<Road>() != null;

        // ------------------------------------------------------------------ Inspecteur
        public static void DrawInspector(Road road)
        {
            target = road;
            float total = road.TotalLength;

            showSection = EditorGUILayout.Foldout(showSection, "Couper la route", true);
            if (!showSection) return;

            if (total < 3f) { EditorGUILayout.HelpBox("Route trop courte pour être coupée.", MessageType.None); return; }

            presetIdx = EditorGUILayout.Popup(new GUIContent("Insérer au point de coupe", "Crée une intersection au point de coupe et la raccorde aux deux moitiés."), presetIdx, RoadSplitter.PresetLabels);

            if (sliderDist < 1f || sliderDist > total - 1f) sliderDist = total * 0.5f;
            EditorGUI.BeginChangeCheck();
            sliderDist = EditorGUILayout.Slider(new GUIContent("Position (m)", "Distance depuis le début de la route."), sliderDist, 1f, total - 1f);
            if (EditorGUI.EndChangeCheck()) SceneView.RepaintAll();

            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField("Bout de route (cercle)", EditorStyles.miniBoldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("À la fin")) AddCap(road, RoadEnd.End);
                if (GUILayout.Button("Au début")) AddCap(road, RoadEnd.Start);
            }
            EditorGUILayout.Space(2);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Couper à cette position", GUILayout.Height(24))) DoSplit(road, sliderDist);

                var prev = GUI.backgroundColor;
                if (mouseMode) GUI.backgroundColor = new Color(1f, 0.6f, 0.3f);
                if (GUILayout.Button(mouseMode ? "Annuler (Échap)" : "Couper à la souris", GUILayout.Height(24)))
                {
                    mouseMode = !mouseMode;
                    SceneView.RepaintAll();
                }
                GUI.backgroundColor = prev;
            }
        }

        private static void AddCap(Road road, RoadEnd end)
        {
            var hub = RoadSplitter.AddEndCap(road, end);
            if (hub != null) Selection.activeGameObject = hub.gameObject;
        }

        private static void DoSplit(Road road, float dist)
        {
            var nr = RoadSplitter.Split(road, dist, presetIdx, out var hub);
            mouseMode = false;
            if (nr != null) Selection.activeGameObject = hub != null ? hub.gameObject : nr.gameObject;
            sliderDist = -1f;
            SceneView.RepaintAll();
        }

        // ------------------------------------------------------------------ Scène
        private static void OnScene(SceneView sv)
        {
            if (target == null) return;
            if (Selection.activeGameObject != target.gameObject) { mouseMode = false; return; }

            var pts = target.Points;
            if (pts.Count < 2 || target.TotalLength < 3f) return;

            Event e = Event.current;
            int id = GUIUtility.GetControlID(FocusType.Passive);

            float dist;
            bool valid;
            if (mouseMode)
            {
                if (e.type == EventType.Layout) HandleUtility.AddDefaultControl(id);
                valid = NearestOnRoad(pts, e.mousePosition, out dist, out float px);
                valid &= px < 60f;

                if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape) { mouseMode = false; e.Use(); sv.Repaint(); return; }

                if (valid && e.GetTypeForControl(id) == EventType.MouseDown && e.button == 0 && !e.alt && HandleUtility.nearestControl == id)
                {
                    e.Use();
                    DoSplit(target, dist);
                    return;
                }
                if (e.type == EventType.MouseMove) sv.Repaint();

                Handles.BeginGUI();
                GUI.Label(new Rect(12, 10, 460, 22), "Couper la route : clique sur la route (Échap pour annuler)", EditorStyles.boldLabel);
                Handles.EndGUI();
            }
            else
            {
                if (sliderDist < 1f) return;
                dist = sliderDist;
                valid = true;
            }

            if (!valid || e.type != EventType.Repaint) return;

            RoadPoint p = target.SampleAt(dist);
            target.GetEndWidths(RoadEnd.Start, out float wl, out float wr);
            float half = Mathf.Max(wl, wr, 2f) + 3f;

            Handles.color = new Color(1f, 0.3f, 0.2f);
            Vector3 up = Vector3.up * 0.4f;
            Handles.DrawAAPolyLine(5f, p.pos + up - p.right * half, p.pos + up + p.right * half);
            float sz = HandleUtility.GetHandleSize(p.pos) * 0.12f;
            Handles.SphereHandleCap(0, p.pos + up, Quaternion.identity, sz, EventType.Repaint);
            Handles.Label(p.pos + up + Vector3.up * sz * 3f,
                $"Coupe à {dist:0.#} m  ({dist:0.#} + {target.TotalLength - dist:0.#})" + (presetIdx > 0 ? "\n+ " + RoadSplitter.PresetLabels[presetIdx] : ""));
        }

        /// <summary>Point de la route le plus proche de la souris (en pixels). dist = mètres depuis le début.</summary>
        private static bool NearestOnRoad(IReadOnlyList<RoadPoint> pts, Vector2 mouse, out float dist, out float pixelDistance)
        {
            dist = 0f; pixelDistance = float.MaxValue;
            for (int i = 0; i < pts.Count - 1; i++)
            {
                Vector2 a = HandleUtility.WorldToGUIPoint(pts[i].pos), b = HandleUtility.WorldToGUIPoint(pts[i + 1].pos);
                Vector2 ab = b - a;
                float len2 = ab.sqrMagnitude;
                float u = len2 > 1e-4f ? Mathf.Clamp01(Vector2.Dot(mouse - a, ab) / len2) : 0f;
                float d = Vector2.Distance(mouse, a + ab * u);
                if (d < pixelDistance)
                {
                    pixelDistance = d;
                    dist = Mathf.Lerp(pts[i].dist, pts[i + 1].dist, u);
                }
            }
            return pixelDistance < float.MaxValue;
        }
    }
}
#endif
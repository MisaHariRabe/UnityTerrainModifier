#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.Splines;
using Unity.Mathematics;

namespace RoadTools
{
    [CustomEditor(typeof(RoadBridge))]
    public class RoadBridgeEditor : Editor
    {
        private int selIndex = 0;

        private static readonly Color[] Palette =
        {
            new Color(1f, 0.6f, 0.2f), new Color(0.25f, 0.8f, 1f), new Color(0.6f, 1f, 0.4f),
            new Color(1f, 0.4f, 0.7f), new Color(0.8f, 0.7f, 1f), new Color(1f, 0.95f, 0.3f)
        };

        // ------------------------------------------------------------------ Menu : route -> pont
        [MenuItem("GameObject/Road System/Transformer en pont", false, 34)]
        private static void MakeBridge()
        {
            foreach (var go in Selection.gameObjects)
            {
                if (go.GetComponent<Road>() == null || go.GetComponent<RoadBridge>() != null) continue;
                Undo.AddComponent<RoadBridge>(go);
            }
        }

        [MenuItem("GameObject/Road System/Transformer en pont", true)]
        private static bool MakeBridgeValidate()
        {
            foreach (var go in Selection.gameObjects)
                if (go.GetComponent<Road>() != null && go.GetComponent<RoadBridge>() == null) return true;
            return false;
        }

        // ------------------------------------------------------------------ Inspecteur
        public override void OnInspectorGUI()
        {
            var bridge = (RoadBridge)target;
            var road = bridge.GetComponent<Road>();
            serializedObject.Update();

            Rect header = GUILayoutUtility.GetRect(0, 22, GUILayout.ExpandWidth(true));
            Color hc = new Color(1f, 0.6f, 0.2f);
            EditorGUI.DrawRect(header, new Color(hc.r, hc.g, hc.b, 0.2f));
            EditorGUI.DrawRect(new Rect(header.x, header.y, 4, header.height), hc);
            GUI.Label(new Rect(header.x + 10, header.y + 2, header.width - 12, 18),
                $"Pont  •  {bridge.spans.Count} portion(s)  •  route de {road.TotalLength:0.#} m", EditorStyles.boldLabel);

            if (road.Points.Count < 2)
                EditorGUILayout.HelpBox("La route a besoin d'au moins 2 nœuds (Ctrl+clic dans la scène).", MessageType.Info);

            EditorGUILayout.HelpBox(
                "Chaque portion de route devient un pont : tablier, parapets, piliers (optionnels). Sous le pont, le terrain retrouve sa forme d'origine.\n" +
                "Astuce : place des nœuds de spline aux deux bouts du pont, puis « Niveler le tablier » pour qu'il soit droit au-dessus de la vallée.",
                MessageType.None);

            var list = serializedObject.FindProperty("spans");
            int remove = -1, dup = -1;
            for (int i = 0; i < list.arraySize; i++)
            {
                DrawSpan(road, list.GetArrayElementAtIndex(i), i, out bool del, out bool cp);
                if (del) remove = i;
                if (cp) dup = i;
            }
            if (remove >= 0) { list.DeleteArrayElementAtIndex(remove); selIndex = Mathf.Clamp(selIndex, 0, Mathf.Max(0, list.arraySize - 1)); }
            if (dup >= 0) list.InsertArrayElementAtIndex(dup);

            if (GUILayout.Button("+ Ajouter une portion de pont"))
            {
                int idx = list.arraySize;
                list.InsertArrayElementAtIndex(idx);
                var p = list.GetArrayElementAtIndex(idx);
                p.FindPropertyRelative("name").stringValue = $"Pont {idx + 1}";
                p.FindPropertyRelative("start").floatValue = 0.3f;
                p.FindPropertyRelative("end").floatValue = 0.7f;
                p.isExpanded = true;
                selIndex = idx;
                SceneView.RepaintAll();
            }

            EditorGUILayout.Space(6);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("bridgeMaterial"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("generateCollider"));

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Terrain sous le pont", EditorStyles.boldLabel);
            var rt = serializedObject.FindProperty("restoreTerrain");
            EditorGUILayout.PropertyField(rt);
            if (rt.boolValue)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("terrainMargin"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("terrainFade"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("endFade"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("terrainLayerOrder"));
            }

            if (serializedObject.ApplyModifiedProperties())
                bridge.RequestRebuild();

            EditorGUILayout.Space(6);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("Niveler le tablier", "Aligne la hauteur des nœuds situés à l'intérieur de chaque pont sur une pente droite entre ses deux extrémités."), GUILayout.Height(24)))
                    LevelDeck(bridge, road);
                if (GUILayout.Button("Reconstruire", GUILayout.Height(24))) bridge.Rebuild();
            }
        }

        private void DrawSpan(Road road, SerializedProperty p, int i, out bool del, out bool dup)
        {
            del = dup = false;
            Color col = Palette[i % Palette.Length];
            bool selected = selIndex == i;

            var name = p.FindPropertyRelative("name");
            var enabled = p.FindPropertyRelative("enabled");
            var start = p.FindPropertyRelative("start");
            var end = p.FindPropertyRelative("end");

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            using (new EditorGUILayout.HorizontalScope())
            {
                var r = GUILayoutUtility.GetRect(5, 18, GUILayout.Width(5));
                EditorGUI.DrawRect(r, col);

                p.isExpanded = EditorGUILayout.Foldout(p.isExpanded,
                    $"{name.stringValue}   {start.floatValue * 100f:0}% – {end.floatValue * 100f:0}%", true);
                GUILayout.FlexibleSpace();
                enabled.boolValue = EditorGUILayout.Toggle(enabled.boolValue, GUILayout.Width(18));

                var old = GUI.backgroundColor;
                if (selected) GUI.backgroundColor = col;
                if (GUILayout.Button(new GUIContent("✎", "Éditer les extrémités dans la vue Scène"), GUILayout.Width(24)))
                {
                    selIndex = i;
                    SceneView.RepaintAll();
                }
                GUI.backgroundColor = old;

                if (GUILayout.Button("⧉", GUILayout.Width(24))) dup = true;
                if (GUILayout.Button("✕", GUILayout.Width(24))) del = true;
            }

            if (p.isExpanded)
            {
                EditorGUILayout.PropertyField(name);

                float a = start.floatValue, b = end.floatValue;
                EditorGUI.BeginChangeCheck();
                EditorGUILayout.MinMaxSlider(new GUIContent("Portion"), ref a, ref b, 0f, 1f);
                if (EditorGUI.EndChangeCheck()) { start.floatValue = a; end.floatValue = b; }
                float total = road.TotalLength;
                EditorGUILayout.LabelField($"De {a * total:0.#} m à {b * total:0.#} m  (longueur {(b - a) * total:0.#} m)", EditorStyles.miniLabel);

                bool arch = p.FindPropertyRelative("style").enumValueIndex == (int)BridgeStyle.Arch;
                bool parapets = p.FindPropertyRelative("parapets").boolValue;
                bool pillars = p.FindPropertyRelative("pillars").boolValue;

                // Tous les autres champs, en masquant ceux qui ne servent à rien dans la configuration choisie.
                SerializedProperty it = p.Copy();
                SerializedProperty stop = p.GetEndProperty();
                bool enter = true;
                while (it.NextVisible(enter) && !SerializedProperty.EqualContents(it, stop))
                {
                    enter = false;
                    string n = it.name;
                    if (n == "name" || n == "enabled" || n == "start" || n == "end") continue;
                    if (n == "archRise" && !arch) continue;
                    if (n.StartsWith("parapet") && n != "parapets" && !parapets) continue;
                    if (n.StartsWith("pillar") && n != "pillars" && !pillars) continue;
                    EditorGUILayout.PropertyField(it, true);
                }
            }
            EditorGUILayout.EndVertical();
        }

        // ------------------------------------------------------------------ Nivellement du tablier
        private static void LevelDeck(RoadBridge bridge, Road road)
        {
            var container = road.GetContainer();
            if (container == null || container.Spline == null || container.Spline.Count < 3) return;

            Spline sp = container.Spline;
            float total = road.TotalLength;
            var pts = road.Points;
            if (pts.Count < 2 || total < 1f) return;

            Undo.RecordObject(container, "Niveler le tablier");

            foreach (var span in bridge.spans)
            {
                if (span == null || !span.enabled) continue;
                float d0 = span.start * total, d1 = span.end * total;
                if (d1 - d0 < 1f) continue;

                float y0 = road.SampleAt(d0).pos.y, y1 = road.SampleAt(d1).pos.y;

                for (int i = 0; i < sp.Count; i++)
                {
                    Vector3 w = road.transform.TransformPoint((Vector3)sp[i].Position);

                    // Distance le long de la route de ce nœud : on prend le point de route le plus proche.
                    float best = float.MaxValue, d = 0f;
                    for (int k = 0; k < pts.Count; k++)
                    {
                        float dx = pts[k].pos.x - w.x, dz = pts[k].pos.z - w.z;
                        float q = dx * dx + dz * dz;
                        if (q < best) { best = q; d = pts[k].dist; }
                    }
                    if (d <= d0 + 0.05f || d >= d1 - 0.05f) continue;   // seuls les nœuds à l'intérieur du pont bougent

                    float y = Mathf.Lerp(y0, y1, (d - d0) / (d1 - d0));
                    Vector3 local = road.transform.InverseTransformPoint(new Vector3(w.x, y, w.z));
                    BezierKnot kn = sp[i];
                    kn.Position = new float3(local.x, local.y, local.z);
                    sp.SetKnot(i, kn);
                }
            }
            EditorUtility.SetDirty(container);
            road.RequestRebuild();
        }

        // ------------------------------------------------------------------ Scène
        private void OnSceneGUI()
        {
            var bridge = (RoadBridge)target;
            var road = bridge.GetComponent<Road>();
            if (road == null || road.Points.Count < 2 || road.TotalLength < 0.1f) return;

            for (int i = 0; i < bridge.spans.Count; i++)
            {
                var s = bridge.spans[i];
                if (s == null || !s.enabled) continue;
                DrawRange(road, s.start, s.end, Palette[i % Palette.Length], s.name, selIndex == i);
            }

            if (selIndex >= 0 && selIndex < bridge.spans.Count)
            {
                serializedObject.Update();
                var p = serializedObject.FindProperty("spans").GetArrayElementAtIndex(selIndex);
                var start = p.FindPropertyRelative("start");
                var end = p.FindPropertyRelative("end");
                float total = road.TotalLength;

                Handles.color = Color.white;
                DragEnd(road, start, end, true, total);
                DragEnd(road, end, start, false, total);
                if (serializedObject.ApplyModifiedProperties()) bridge.RequestRebuild();
            }
        }

        private static void DrawRange(Road road, float a, float b, Color col, string label, bool bold)
        {
            float total = road.TotalLength;
            float d0 = a * total, d1 = Mathf.Max(b * total, d0 + 0.1f);
            int steps = Mathf.Clamp(Mathf.CeilToInt((d1 - d0) / 2f), 2, 400);

            var line = new Vector3[steps + 1];
            for (int k = 0; k <= steps; k++)
                line[k] = road.SampleAt(Mathf.Lerp(d0, d1, k / (float)steps)).pos + Vector3.up * 0.7f;

            Handles.color = new Color(col.r, col.g, col.b, bold ? 1f : 0.55f);
            Handles.DrawAAPolyLine(bold ? 6f : 3f, line);
            Handles.Label(line[0] + Vector3.up * 0.4f, label);
        }

        private void DragEnd(Road road, SerializedProperty prop, SerializedProperty other, bool isStart, float total)
        {
            Vector3 pos = road.SampleAt(prop.floatValue * total).pos + Vector3.up * 0.8f;
            float size = HandleUtility.GetHandleSize(pos) * 0.14f;

            EditorGUI.BeginChangeCheck();
            Vector3 np = Handles.FreeMoveHandle(pos, size, Vector3.zero, Handles.SphereHandleCap);
            if (EditorGUI.EndChangeCheck())
            {
                float s = NearestNormalized(road, np);
                s = isStart ? Mathf.Min(s, other.floatValue - 0.005f) : Mathf.Max(s, other.floatValue + 0.005f);
                prop.floatValue = Mathf.Clamp01(s);
            }
            Handles.Label(pos + Vector3.up * size * 2f, $"{(isStart ? "Début" : "Fin")} {prop.floatValue * total:0.#} m");
        }

        private static float NearestNormalized(Road road, Vector3 p)
        {
            var pts = road.Points;
            float best = float.MaxValue, bestS = 0f;
            for (int i = 0; i < pts.Count; i++)
            {
                float dx = pts[i].pos.x - p.x, dz = pts[i].pos.z - p.z;
                float d = dx * dx + dz * dz;
                if (d < best) { best = d; bestS = pts[i].s; }
            }
            return bestS;
        }
    }
}
#endif
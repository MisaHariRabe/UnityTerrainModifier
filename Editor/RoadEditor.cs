#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Splines;
using Unity.Mathematics;

namespace RoadTools
{
    [CustomEditor(typeof(Road))]
    public class RoadEditor : Editor
    {
        private enum SelKind { None, Section, Rule }

        private SelKind selKind = SelKind.None;
        private int selIndex = -1;

        private static bool showSections = true;
        private static bool showRules = true;

        private static readonly Color[] Palette =
        {
            new Color(0.25f, 0.8f, 1f), new Color(1f, 0.6f, 0.2f), new Color(0.6f, 1f, 0.4f),
            new Color(1f, 0.4f, 0.7f), new Color(0.8f, 0.7f, 1f), new Color(1f, 0.95f, 0.3f)
        };

        // ------------------------------------------------------------------ Inspecteur
        public override void OnInspectorGUI()
        {
            var road = (Road)target;
            serializedObject.Update();

            // En-tête
            Rect header = GUILayoutUtility.GetRect(0, 22, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(header, new Color(1f, 0.85f, 0.2f, 0.18f));
            EditorGUI.DrawRect(new Rect(header.x, header.y, 4, header.height), new Color(1f, 0.85f, 0.2f));
            GUI.Label(new Rect(header.x + 10, header.y + 2, header.width - 12, 18),
                $"Route  •  {road.TotalLength:0.#} m  •  {road.sections.Count} section(s)  •  {road.sideObjects.Count} règle(s) d'objets",
                EditorStyles.boldLabel);

            var c = road.GetContainer();
            if (c != null && c.Spline != null && c.Spline.Count < 2)
                EditorGUILayout.HelpBox("Ajoutez au moins 2 nœuds à la spline pour générer la route.", MessageType.Info);

            if (road.conformTerrain && road.Points.Count > 1)
                RoadEditorUtil.DrawTerrainStatus(road.SampleAt(road.TotalLength * 0.5f).pos);

            DrawPropertiesExcluding(serializedObject, "m_Script", "sections", "sideObjects");

            EditorGUILayout.Space(6);
            DrawSections(road);

            EditorGUILayout.Space(6);
            DrawSideObjectRules(road);

            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space(8);
            RoadSplitterTool.DrawInspector(road);

            // Outils
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Outils", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Reconstruire", GUILayout.Height(24))) road.Rebuild();
                if (GUILayout.Button(new GUIContent("Poser les nœuds au sol", "Aligne la hauteur des nœuds sur le terrain de base."), GUILayout.Height(24)))
                    SnapKnotsToGround(road);
            }
            EditorGUILayout.LabelField($"Sommets générés : {road.LastVertexCount}", EditorStyles.miniLabel);
        }

        private static void SnapKnotsToGround(Road road)
        {
            var c = road.GetContainer();
            if (c == null || c.Spline == null) return;

            Undo.RecordObject(c, "Poser les nœuds au sol");
            Spline sp = c.Spline;
            int moved = 0;
            for (int i = 0; i < sp.Count; i++)
            {
                BezierKnot k = sp[i];
                Vector3 w = road.transform.TransformPoint((Vector3)k.Position);
                if (!TerrainModifierManager.TrySampleBaseHeight(w.x, w.z, out float y)) continue;
                moved++;
                Vector3 local = road.transform.InverseTransformPoint(new Vector3(w.x, y, w.z));
                k.Position = new float3(local.x, local.y, local.z);
                sp.SetKnot(i, k);
            }
            EditorUtility.SetDirty(c);

            if (moved == 0)
                EditorUtility.DisplayDialog("Poser les nœuds au sol",
                    "Aucun nœud ne se trouve au-dessus d'un terrain actif de la scène.", "OK");
        }

        // ------------------------------------------------------------------ Sections
        private void DrawSections(Road road)
        {
            var list = serializedObject.FindProperty("sections");
            showSections = EditorGUILayout.Foldout(showSections, $"Sections ({list.arraySize})", true, EditorStyles.foldoutHeader);
            if (showSections)
            {
                EditorGUILayout.HelpBox("Chaque section change les largeurs gauche/droite et/ou ajoute des trottoirs sur une portion de la route. Les sections suivantes passent par-dessus les précédentes.", MessageType.None);

                int remove = -1, duplicate = -1;
                for (int i = 0; i < list.arraySize; i++)
                {
                    var p = list.GetArrayElementAtIndex(i);
                    DrawSection(road, p, i, out bool del, out bool dup);
                    if (del) remove = i;
                    if (dup) duplicate = i;
                }
                if (remove >= 0) { list.DeleteArrayElementAtIndex(remove); if (selKind == SelKind.Section) selIndex = -1; }
                if (duplicate >= 0) list.InsertArrayElementAtIndex(duplicate);

                if (GUILayout.Button("+ Ajouter une section"))
                {
                    int idx = list.arraySize;
                    list.InsertArrayElementAtIndex(idx);
                    var p = list.GetArrayElementAtIndex(idx);
                    p.FindPropertyRelative("name").stringValue = $"Section {idx + 1}";
                    p.FindPropertyRelative("enabled").boolValue = true;
                    p.FindPropertyRelative("start").floatValue = 0.25f;
                    p.FindPropertyRelative("end").floatValue = 0.75f;
                    p.FindPropertyRelative("overrideWidth").boolValue = true;
                    p.FindPropertyRelative("leftWidth").floatValue = road.leftWidth;
                    p.FindPropertyRelative("rightWidth").floatValue = road.rightWidth;
                    p.FindPropertyRelative("sidewalkLeft").boolValue = true;
                    p.FindPropertyRelative("sidewalkRight").boolValue = true;
                    p.isExpanded = true;
                    selKind = SelKind.Section; selIndex = idx;
                    SceneView.RepaintAll();
                }
            }
        }

        private void DrawSection(Road road, SerializedProperty p, int i, out bool del, out bool dup)
        {
            del = dup = false;
            Color col = Palette[i % Palette.Length];
            bool selected = selKind == SelKind.Section && selIndex == i;

            var name = p.FindPropertyRelative("name");
            var enabled = p.FindPropertyRelative("enabled");
            var start = p.FindPropertyRelative("start");
            var end = p.FindPropertyRelative("end");

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            Rect bar = GUILayoutUtility.GetRect(0, 0, GUILayout.ExpandWidth(true));
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
                if (GUILayout.Button(new GUIContent("✎", "Éditer dans la vue Scène"), GUILayout.Width(24)))
                {
                    selKind = selected ? SelKind.None : SelKind.Section;
                    selIndex = selected ? -1 : i;
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

                var ow = p.FindPropertyRelative("overrideWidth");
                EditorGUILayout.PropertyField(ow, new GUIContent("Dimensions de la chaussée"));
                if (ow.boolValue)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.PropertyField(p.FindPropertyRelative("leftWidth"), new GUIContent("Gauche"));
                        EditorGUILayout.PropertyField(p.FindPropertyRelative("rightWidth"), new GUIContent("Droite"));
                    }
                }

                EditorGUILayout.LabelField("Trottoirs", EditorStyles.miniBoldLabel);
                DrawSidewalkRow(p, "sidewalkLeft", "sidewalkLeftWidth", "Gauche");
                DrawSidewalkRow(p, "sidewalkRight", "sidewalkRightWidth", "Droite");
                EditorGUILayout.PropertyField(p.FindPropertyRelative("sidewalkHeight"), new GUIContent("Hauteur de bordure"));
            }
            EditorGUILayout.EndVertical();
        }

        private static void DrawSidewalkRow(SerializedProperty p, string toggle, string width, string label)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                var t = p.FindPropertyRelative(toggle);
                t.boolValue = EditorGUILayout.ToggleLeft(label, t.boolValue, GUILayout.Width(90));
                using (new EditorGUI.DisabledScope(!t.boolValue))
                    EditorGUILayout.PropertyField(p.FindPropertyRelative(width), new GUIContent("Largeur"));
            }
        }

        // ------------------------------------------------------------------ Objets latéraux
        private void DrawSideObjectRules(Road road)
        {
            var list = serializedObject.FindProperty("sideObjects");
            showRules = EditorGUILayout.Foldout(showRules, $"Objets latéraux ({list.arraySize})", true, EditorStyles.foldoutHeader);
            if (showRules)
            {
                EditorGUILayout.HelpBox("Chaque règle place des prefabs (tirés au hasard) le long d'une portion de la route, côté gauche, droit ou les deux.", MessageType.None);

                int remove = -1, duplicate = -1;
                for (int i = 0; i < list.arraySize; i++)
                {
                    DrawRule(road, list.GetArrayElementAtIndex(i), i, out bool del, out bool dup);
                    if (del) remove = i;
                    if (dup) duplicate = i;
                }
                if (remove >= 0) { list.DeleteArrayElementAtIndex(remove); if (selKind == SelKind.Rule) selIndex = -1; }
                if (duplicate >= 0)
                {
                    list.InsertArrayElementAtIndex(duplicate);
                    list.GetArrayElementAtIndex(duplicate + 1).FindPropertyRelative("seed").intValue = UnityEngine.Random.Range(1, 99999);
                }

                if (GUILayout.Button("+ Ajouter une règle"))
                {
                    int idx = list.arraySize;
                    list.InsertArrayElementAtIndex(idx);
                    var p = list.GetArrayElementAtIndex(idx);
                    p.FindPropertyRelative("name").stringValue = $"Objets {idx + 1}";
                    p.FindPropertyRelative("enabled").boolValue = true;
                    p.FindPropertyRelative("prefabs").ClearArray();
                    p.FindPropertyRelative("seed").intValue = UnityEngine.Random.Range(1, 99999);
                    p.isExpanded = true;
                    selKind = SelKind.Rule; selIndex = idx;
                    SceneView.RepaintAll();
                }
            }
        }

        private void DrawRule(Road road, SerializedProperty p, int i, out bool del, out bool dup)
        {
            del = dup = false;
            Color col = Palette[(i + 3) % Palette.Length];
            bool selected = selKind == SelKind.Rule && selIndex == i;

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
                if (GUILayout.Button(new GUIContent("✎", "Éditer la portion dans la vue Scène"), GUILayout.Width(24)))
                {
                    selKind = selected ? SelKind.None : SelKind.Rule;
                    selIndex = selected ? -1 : i;
                    SceneView.RepaintAll();
                }
                GUI.backgroundColor = old;

                if (GUILayout.Button("⧉", GUILayout.Width(24))) dup = true;
                if (GUILayout.Button("✕", GUILayout.Width(24))) del = true;
            }

            if (p.isExpanded)
            {
                SideObjectEditorUtil.DrawRuleBody(this, p, road.TotalLength, false);
            }
            EditorGUILayout.EndVertical();
        }

        // ------------------------------------------------------------------ Scène
        private void OnSceneGUI()
        {
            var road = (Road)target;
            var pts = road.Points;
            if (pts.Count < 2 || road.TotalLength < 0.1f) return;

            // Toutes les portions
            for (int i = 0; i < road.sections.Count; i++)
            {
                var s = road.sections[i];
                if (s == null || !s.enabled) continue;
                bool sel = selKind == SelKind.Section && selIndex == i;
                DrawRange(road, s.start, s.end, Palette[i % Palette.Length], s.name, 0.35f, sel);
            }
            for (int i = 0; i < road.sideObjects.Count; i++)
            {
                var r = road.sideObjects[i];
                if (r == null || !r.enabled) continue;
                bool sel = selKind == SelKind.Rule && selIndex == i;
                if (sel) DrawRange(road, r.start, r.end, Palette[(i + 3) % Palette.Length], r.name, 0.8f, true);
            }

            // Poignées sur la sélection
            if (selKind == SelKind.Section && selIndex >= 0 && selIndex < road.sections.Count)
                RangeHandles(road, "sections", selIndex);
            else if (selKind == SelKind.Rule && selIndex >= 0 && selIndex < road.sideObjects.Count)
                RangeHandles(road, "sideObjects", selIndex);
        }

        private static void DrawRange(Road road, float a, float b, Color col, string label, float lift, bool bold)
        {
            float total = road.TotalLength;
            float d0 = a * total, d1 = Mathf.Max(b * total, d0 + 0.1f);
            int steps = Mathf.Clamp(Mathf.CeilToInt((d1 - d0) / 2f), 2, 400);

            var line = new Vector3[steps + 1];
            for (int k = 0; k <= steps; k++)
                line[k] = road.SampleAt(Mathf.Lerp(d0, d1, k / (float)steps)).pos + Vector3.up * lift;

            Handles.color = new Color(col.r, col.g, col.b, bold ? 1f : 0.55f);
            Handles.DrawAAPolyLine(bold ? 6f : 3f, line);
            Handles.Label(line[0] + Vector3.up * 0.4f, label);
        }

        private void RangeHandles(Road road, string listName, int index)
        {
            serializedObject.Update();
            var p = serializedObject.FindProperty(listName).GetArrayElementAtIndex(index);
            var start = p.FindPropertyRelative("start");
            var end = p.FindPropertyRelative("end");
            float total = road.TotalLength;

            Handles.color = Color.white;
            DragEnd(road, start, end, true, total);
            DragEnd(road, end, start, false, total);
            serializedObject.ApplyModifiedProperties();
        }

        private void DragEnd(Road road, SerializedProperty prop, SerializedProperty other, bool isStart, float total)
        {
            Vector3 pos = road.SampleAt(prop.floatValue * total).pos + Vector3.up * 0.5f;
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
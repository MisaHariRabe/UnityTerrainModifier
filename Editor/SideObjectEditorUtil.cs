#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace RoadTools
{
    /// <summary>Interface d'inspecteur partagée par les routes et les intersections : règles de sideobjects, drag & drop, liste/grille, préréglages.</summary>
    public static class SideObjectEditorUtil
    {
        private static bool gridView = true;
        private static float cellSize = 76f;
        private static GUIStyle clipLabel;

        private static readonly Color[] Palette =
        {
            new Color(0.25f, 0.8f, 1f), new Color(1f, 0.6f, 0.2f), new Color(0.6f, 1f, 0.4f),
            new Color(1f, 0.4f, 0.7f), new Color(0.8f, 0.7f, 1f), new Color(1f, 0.95f, 0.3f)
        };

        // ------------------------------------------------------------------ Liste de règles (intersections)
        public static void DrawRuleList(Editor owner, SerializedProperty list, bool intersection, ref bool foldout)
        {
            foldout = EditorGUILayout.Foldout(foldout, $"Objets latéraux ({list.arraySize})", true, EditorStyles.foldoutHeader);
            if (!foldout) return;

            EditorGUILayout.HelpBox(
                "Chaque règle place des prefabs (tirés au hasard) le long des coins de l'intersection, ou sur l'îlot central d'un rond-point. " +
                "Les coins sans trottoir sont décorés au niveau de la chaussée.", MessageType.None);

            int remove = -1, duplicate = -1;
            for (int i = 0; i < list.arraySize; i++)
            {
                DrawRuleBox(owner, list.GetArrayElementAtIndex(i), i, 0f, intersection, out bool del, out bool dup);
                if (del) remove = i;
                if (dup) duplicate = i;
            }
            if (remove >= 0) list.DeleteArrayElementAtIndex(remove);
            if (duplicate >= 0)
            {
                list.InsertArrayElementAtIndex(duplicate);
                list.GetArrayElementAtIndex(duplicate + 1).FindPropertyRelative("seed").intValue = Random.Range(1, 99999);
            }

            if (GUILayout.Button("+ Ajouter une règle"))
            {
                int idx = list.arraySize;
                list.InsertArrayElementAtIndex(idx);
                var p = list.GetArrayElementAtIndex(idx);
                p.FindPropertyRelative("name").stringValue = $"Objets {idx + 1}";
                p.FindPropertyRelative("enabled").boolValue = true;
                p.FindPropertyRelative("prefabs").ClearArray();
                p.FindPropertyRelative("seed").intValue = Random.Range(1, 99999);
                p.isExpanded = true;
            }
        }

        private static void DrawRuleBox(Editor owner, SerializedProperty p, int i, float totalLength, bool intersection,
                                        out bool del, out bool dup)
        {
            del = dup = false;
            Color col = Palette[(i + 3) % Palette.Length];
            var name = p.FindPropertyRelative("name");
            var enabled = p.FindPropertyRelative("enabled");

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            using (new EditorGUILayout.HorizontalScope())
            {
                var r = GUILayoutUtility.GetRect(5, 18, GUILayout.Width(5));
                EditorGUI.DrawRect(r, col);
                p.isExpanded = EditorGUILayout.Foldout(p.isExpanded, name.stringValue, true);
                GUILayout.FlexibleSpace();
                enabled.boolValue = EditorGUILayout.Toggle(enabled.boolValue, GUILayout.Width(18));
                if (GUILayout.Button("⧉", GUILayout.Width(24))) dup = true;
                if (GUILayout.Button("✕", GUILayout.Width(24))) del = true;
            }
            if (p.isExpanded) DrawRuleBody(owner, p, totalLength, intersection);
            EditorGUILayout.EndVertical();
        }

        // ------------------------------------------------------------------ Corps d'une règle
        /// <param name="totalLength">Longueur de la route en mètres (0 pour une intersection).</param>
        public static void DrawRuleBody(Editor owner, SerializedProperty p, float totalLength, bool intersection)
        {
            EditorGUILayout.PropertyField(p.FindPropertyRelative("name"));
            DrawPresetBar(p);
            EditorGUILayout.Space(2);
            DrawPrefabs(owner, p.FindPropertyRelative("prefabs"));

            // Où
            EditorGUILayout.Space(2);
            bool island = false;
            if (intersection)
            {
                var zone = p.FindPropertyRelative("zone");
                EditorGUILayout.PropertyField(zone, new GUIContent("Zone"));
                island = zone.enumValueIndex == (int)IntersectionZone.Island;
            }
            else EditorGUILayout.PropertyField(p.FindPropertyRelative("side"), new GUIContent("Côté"));

            var start = p.FindPropertyRelative("start");
            var end = p.FindPropertyRelative("end");
            float a = start.floatValue, b = end.floatValue;
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.MinMaxSlider(new GUIContent(intersection ? (island ? "Portion de l'îlot" : "Portion de chaque coin") : "Portion de route"),
                                         ref a, ref b, 0f, 1f);
            if (EditorGUI.EndChangeCheck()) { start.floatValue = a; end.floatValue = b; }
            if (totalLength > 0f) EditorGUILayout.LabelField($"De {a * totalLength:0.#} m à {b * totalLength:0.#} m", EditorStyles.miniLabel);

            // Espacement
            EditorGUILayout.LabelField("Espacement", EditorStyles.miniBoldLabel);
            var useBounds = p.FindPropertyRelative("useBoundsSpacing");
            EditorGUILayout.PropertyField(useBounds, new GUIContent("Bout à bout (longueur du prefab)"));
            if (useBounds.boolValue)
            {
                EditorGUILayout.PropertyField(p.FindPropertyRelative("spacingPadding"), new GUIContent("Marge (m)"));
                EditorGUILayout.LabelField($"Pas calculé : {ComputeStep(p):0.##} m", EditorStyles.miniLabel);
            }
            else EditorGUILayout.PropertyField(p.FindPropertyRelative("spacing"));
            EditorGUILayout.PropertyField(p.FindPropertyRelative("spacingJitter"));
            EditorGUILayout.PropertyField(p.FindPropertyRelative("probability"));

            // Placement
            EditorGUILayout.LabelField("Placement", EditorStyles.miniBoldLabel);
            if (!island)
                EditorGUILayout.PropertyField(p.FindPropertyRelative("anchor"), new GUIContent("Ancre"));
            EditorGUILayout.PropertyField(p.FindPropertyRelative("offset"),
                new GUIContent(island ? "Offset (vers le centre)" : "Offset latéral"));
            EditorGUILayout.PropertyField(p.FindPropertyRelative("lateralJitter"), new GUIContent("Variation latérale ±"));
            EditorGUILayout.PropertyField(p.FindPropertyRelative("forwardOffset"), new GUIContent("Offset le long du tracé"));
            EditorGUILayout.PropertyField(p.FindPropertyRelative("forwardJitter"), new GUIContent("Variation longitudinale ±"));
            EditorGUILayout.PropertyField(p.FindPropertyRelative("heightMode"), new GUIContent("Hauteur"));
            EditorGUILayout.PropertyField(p.FindPropertyRelative("heightOffset"), new GUIContent("Offset vertical"));

            // Orientation / échelle
            EditorGUILayout.LabelField("Orientation & échelle", EditorStyles.miniBoldLabel);
            var orient = p.FindPropertyRelative("orientation");
            EditorGUILayout.PropertyField(orient, new GUIContent("Orientation"));
            bool along = orient.enumValueIndex == (int)SideObjectOrientation.AlongRoad;
            if (along || useBounds.boolValue)
                EditorGUILayout.PropertyField(p.FindPropertyRelative("lengthAxis"),
                    new GUIContent("Axe du prefab le long de la route", "Choisissez X si votre clôture / glissière est modélisée en largeur."));
            if (along && !intersection)
                EditorGUILayout.PropertyField(p.FindPropertyRelative("alignToRoadSlope"), new GUIContent("Suivre la pente de la route"));
            EditorGUILayout.PropertyField(p.FindPropertyRelative("yawOffset"), new GUIContent("Rotation Y ajoutée"));
            EditorGUILayout.PropertyField(p.FindPropertyRelative("randomYaw"), new GUIContent("Variation Y ±"));

            var scale = p.FindPropertyRelative("scaleRange");
            Vector2 sv = scale.vector2Value;
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.MinMaxSlider(new GUIContent($"Échelle {sv.x:0.00} – {sv.y:0.00}"), ref sv.x, ref sv.y, 0.1f, 3f);
            if (EditorGUI.EndChangeCheck()) scale.vector2Value = sv;

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PropertyField(p.FindPropertyRelative("seed"));
                if (GUILayout.Button("🎲", GUILayout.Width(30)))
                    p.FindPropertyRelative("seed").intValue = Random.Range(1, 99999);
            }
        }

        private static float ComputeStep(SerializedProperty p)
        {
            var tmp = new SideObjectRule
            {
                useBoundsSpacing = true,
                spacing = p.FindPropertyRelative("spacing").floatValue,
                spacingPadding = p.FindPropertyRelative("spacingPadding").floatValue,
                lengthAxis = (SideObjectAxis)p.FindPropertyRelative("lengthAxis").enumValueIndex,
                scaleRange = p.FindPropertyRelative("scaleRange").vector2Value
            };
            var list = p.FindPropertyRelative("prefabs");
            for (int i = 0; i < list.arraySize; i++)
            {
                var e = list.GetArrayElementAtIndex(i);
                tmp.prefabs.Add(new SideObjectEntry
                {
                    prefab = e.FindPropertyRelative("prefab").objectReferenceValue as GameObject,
                    weight = e.FindPropertyRelative("weight").floatValue,
                    enabled = e.FindPropertyRelative("enabled").boolValue
                });
            }
            return SideObjectPlacer.Step(tmp);
        }

        // ------------------------------------------------------------------ Préréglages
        public static void DrawPresetBar(SerializedProperty rule)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(new GUIContent("Préréglage", "Remplace les réglages de placement (pas les prefabs). Tout reste modifiable ensuite."),
                                           GUILayout.Width(70));
                if (EditorGUILayout.DropdownButton(new GUIContent("Choisir un préréglage…"), FocusType.Passive))
                {
                    SerializedObject so = rule.serializedObject;
                    string path = rule.propertyPath;
                    var menu = new GenericMenu();
                    foreach (var preset in SideObjectPresets.All)
                    {
                        var captured = preset;
                        menu.AddItem(new GUIContent(SideObjectPresets.Label(preset)), false, () => ApplyPreset(so, path, captured));
                    }
                    menu.ShowAsContext();
                }
            }
        }

        private static void ApplyPreset(SerializedObject so, string path, SideObjectPreset preset)
        {
            so.Update();
            var p = so.FindProperty(path);
            if (p == null) return;

            var t = new SideObjectRule();
            SideObjectPresets.Apply(preset, t);

            p.FindPropertyRelative("name").stringValue = t.name;
            p.FindPropertyRelative("side").enumValueIndex = (int)t.side;
            p.FindPropertyRelative("spacing").floatValue = t.spacing;
            p.FindPropertyRelative("useBoundsSpacing").boolValue = t.useBoundsSpacing;
            p.FindPropertyRelative("spacingPadding").floatValue = t.spacingPadding;
            p.FindPropertyRelative("lengthAxis").enumValueIndex = (int)t.lengthAxis;
            p.FindPropertyRelative("alignToRoadSlope").boolValue = t.alignToRoadSlope;
            p.FindPropertyRelative("spacingJitter").floatValue = t.spacingJitter;
            p.FindPropertyRelative("probability").floatValue = t.probability;
            p.FindPropertyRelative("anchor").enumValueIndex = (int)t.anchor;
            p.FindPropertyRelative("offset").floatValue = t.offset;
            p.FindPropertyRelative("lateralJitter").floatValue = t.lateralJitter;
            p.FindPropertyRelative("forwardOffset").floatValue = t.forwardOffset;
            p.FindPropertyRelative("forwardJitter").floatValue = t.forwardJitter;
            p.FindPropertyRelative("heightOffset").floatValue = t.heightOffset;
            p.FindPropertyRelative("heightMode").enumValueIndex = (int)t.heightMode;
            p.FindPropertyRelative("orientation").enumValueIndex = (int)t.orientation;
            p.FindPropertyRelative("yawOffset").floatValue = t.yawOffset;
            p.FindPropertyRelative("randomYaw").floatValue = t.randomYaw;
            p.FindPropertyRelative("scaleRange").vector2Value = t.scaleRange;
            p.FindPropertyRelative("zone").enumValueIndex = (int)t.zone;

            so.ApplyModifiedProperties();
        }

        // ------------------------------------------------------------------ Prefabs : drag & drop + liste / grille
        public static void DrawPrefabs(Editor owner, SerializedProperty prefabs)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label($"Prefabs ({prefabs.arraySize})", EditorStyles.miniBoldLabel);
                GUILayout.FlexibleSpace();
                if (gridView) cellSize = GUILayout.HorizontalSlider(cellSize, 56f, 120f, GUILayout.Width(70));
                if (GUILayout.Button(new GUIContent("Tout afficher", "Coche tous les prefabs"), EditorStyles.miniButtonLeft, GUILayout.Width(78)))
                    SetAllEnabled(prefabs, true);
                if (GUILayout.Button(new GUIContent("Tout cacher", "Décoche tous les prefabs"), EditorStyles.miniButtonRight, GUILayout.Width(74)))
                    SetAllEnabled(prefabs, false);
                gridView = GUILayout.Toolbar(gridView ? 1 : 0, new[] { "Liste", "Grille" }, EditorStyles.miniButton, GUILayout.Width(100)) == 1;
            }

            // Zone de dépôt
            Rect drop = GUILayoutUtility.GetRect(0, 34, GUILayout.ExpandWidth(true));
            GUI.Box(drop, GUIContent.none, EditorStyles.helpBox);
            GUI.Label(drop, "Glissez des prefabs ici depuis la fenêtre Project", new GUIStyle(EditorStyles.centeredGreyMiniLabel) { alignment = TextAnchor.MiddleCenter });
            HandleDrop(drop, prefabs);

            Rect listRect = EditorGUILayout.BeginVertical();
            int remove = -1;
            if (prefabs.arraySize == 0)
                EditorGUILayout.LabelField("Aucun prefab.", EditorStyles.miniLabel);
            else if (gridView) remove = DrawGrid(owner, prefabs);
            else remove = DrawList(owner, prefabs);
            EditorGUILayout.EndVertical();

            if (remove >= 0) prefabs.DeleteArrayElementAtIndex(remove);

            // On peut aussi déposer directement sur la liste.
            HandleDrop(listRect, prefabs);

            if (GUILayout.Button("Ajouter les prefabs sélectionnés (Project)", EditorStyles.miniButton))
                foreach (var go in Selection.gameObjects) AddPrefab(prefabs, go);
        }

        private static void SetAllEnabled(SerializedProperty prefabs, bool value)
        {
            for (int i = 0; i < prefabs.arraySize; i++)
                prefabs.GetArrayElementAtIndex(i).FindPropertyRelative("enabled").boolValue = value;
        }

        private static void HandleDrop(Rect area, SerializedProperty prefabs)
        {
            Event e = Event.current;
            if (e.type != EventType.DragUpdated && e.type != EventType.DragPerform) return;
            if (!area.Contains(e.mousePosition)) return;

            var dropped = new List<GameObject>();
            foreach (var o in DragAndDrop.objectReferences)
                if (o is GameObject go && EditorUtility.IsPersistent(go)) dropped.Add(go);

            if (dropped.Count == 0)
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Rejected;
                return;
            }

            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            if (e.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                foreach (var go in dropped) AddPrefab(prefabs, go);
                GUI.changed = true;
            }
            e.Use();
        }

        private static void AddPrefab(SerializedProperty prefabs, GameObject go)
        {
            if (go == null || !EditorUtility.IsPersistent(go)) return;
            for (int i = 0; i < prefabs.arraySize; i++)
                if (prefabs.GetArrayElementAtIndex(i).FindPropertyRelative("prefab").objectReferenceValue == go) return;

            int idx = prefabs.arraySize;
            prefabs.InsertArrayElementAtIndex(idx);
            var e = prefabs.GetArrayElementAtIndex(idx);
            e.FindPropertyRelative("prefab").objectReferenceValue = go;
            e.FindPropertyRelative("weight").floatValue = 1f;
            e.FindPropertyRelative("enabled").boolValue = true;
        }

        private static Texture GetPreview(Editor owner, GameObject go)
        {
            if (go == null) return null;
            Texture tex = AssetPreview.GetAssetPreview(go);
            if (tex != null) return tex;
            if (AssetPreview.IsLoadingAssetPreview(go.GetEntityId()) && owner != null) owner.Repaint();
            return AssetPreview.GetMiniThumbnail(go);
        }

        private static int DrawList(Editor owner, SerializedProperty prefabs)
        {
            int remove = -1;
            for (int i = 0; i < prefabs.arraySize; i++)
            {
                var e = prefabs.GetArrayElementAtIndex(i);
                var en = e.FindPropertyRelative("enabled");
                var pf = e.FindPropertyRelative("prefab");
                var w = e.FindPropertyRelative("weight");

                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                {
                    en.boolValue = EditorGUILayout.Toggle(new GUIContent("", "Afficher / cacher dans la scène"), en.boolValue, GUILayout.Width(16));

                    Color old = GUI.color;
                    if (!en.boolValue) GUI.color = new Color(1f, 1f, 1f, 0.45f);

                    Rect r = GUILayoutUtility.GetRect(30, 30, GUILayout.Width(30), GUILayout.Height(30));
                    Texture tex = GetPreview(owner, pf.objectReferenceValue as GameObject);
                    if (tex != null) GUI.DrawTexture(r, tex, ScaleMode.ScaleToFit);

                    EditorGUILayout.PropertyField(pf, GUIContent.none);
                    GUILayout.Label("×", GUILayout.Width(12));
                    EditorGUILayout.PropertyField(w, GUIContent.none, GUILayout.Width(40));
                    GUI.color = old;

                    if (GUILayout.Button("✕", GUILayout.Width(22))) remove = i;
                }
            }
            return remove;
        }

        private static int DrawGrid(Editor owner, SerializedProperty prefabs)
        {
            if (clipLabel == null)
                clipLabel = new GUIStyle(EditorStyles.miniLabel) { clipping = TextClipping.Clip, alignment = TextAnchor.MiddleCenter };

            int remove = -1;
            int n = prefabs.arraySize;
            float avail = EditorGUIUtility.currentViewWidth - 44f;
            int cols = Mathf.Max(1, Mathf.FloorToInt(avail / (cellSize + 6f)));
            float inner = cellSize - 8f;

            for (int row = 0; row < n; row += cols)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    for (int i = row; i < Mathf.Min(n, row + cols); i++)
                    {
                        var e = prefabs.GetArrayElementAtIndex(i);
                        var en = e.FindPropertyRelative("enabled");
                        var pf = e.FindPropertyRelative("prefab");
                        var w = e.FindPropertyRelative("weight");
                        var go = pf.objectReferenceValue as GameObject;

                        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox, GUILayout.Width(cellSize)))
                        {
                            using (new EditorGUILayout.HorizontalScope())
                            {
                                en.boolValue = EditorGUILayout.Toggle(new GUIContent("", "Afficher / cacher dans la scène"), en.boolValue, GUILayout.Width(16));
                                GUILayout.FlexibleSpace();
                                if (GUILayout.Button("✕", EditorStyles.miniButton, GUILayout.Width(20))) remove = i;
                            }

                            Color old = GUI.color;
                            if (!en.boolValue) GUI.color = new Color(1f, 1f, 1f, 0.4f);

                            Rect r = GUILayoutUtility.GetRect(inner, inner, GUILayout.Width(inner), GUILayout.Height(inner));
                            if (go != null)
                            {
                                Texture tex = GetPreview(owner, go);
                                if (tex != null) GUI.DrawTexture(r, tex, ScaleMode.ScaleToFit);
                                if (Event.current.type == EventType.MouseDown && r.Contains(Event.current.mousePosition))
                                {
                                    EditorGUIUtility.PingObject(go);
                                    Event.current.Use();
                                }
                                GUILayout.Label(new GUIContent(go.name, go.name), clipLabel, GUILayout.Width(inner));
                            }
                            else
                            {
                                EditorGUI.PropertyField(new Rect(r.x, r.y + r.height * 0.5f - 9f, r.width, 18f), pf, GUIContent.none);
                                GUILayout.Label("(vide)", clipLabel, GUILayout.Width(inner));
                            }

                            EditorGUILayout.PropertyField(w, GUIContent.none, GUILayout.Width(inner));
                            GUI.color = old;
                        }
                    }
                    GUILayout.FlexibleSpace();
                }
            }
            return remove;
        }
    }
}
#endif
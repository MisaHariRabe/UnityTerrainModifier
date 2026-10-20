#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Splines;

namespace RoadTools
{
    [CustomEditor(typeof(RoadIntersection))]
    public class RoadIntersectionEditor : Editor
    {
        private static int exits = 5;
        private static bool showArms = true;
        private static bool showRules = true;

        public override void OnInspectorGUI()
        {
            var hub = (RoadIntersection)target;
            serializedObject.Update();

            // ---- En-tête ----
            Rect header = GUILayoutUtility.GetRect(0, 22, GUILayout.ExpandWidth(true));
            Color col = hub.shape != IntersectionShape.Junction ? new Color(1f, 0.55f, 0.1f) : new Color(0.3f, 0.8f, 1f);
            EditorGUI.DrawRect(header, new Color(col.r, col.g, col.b, 0.2f));
            EditorGUI.DrawRect(new Rect(header.x, header.y, 4, header.height), col);
            GUI.Label(new Rect(header.x + 10, header.y + 2, header.width - 12, 18),
                $"Intersection  •  {hub.shape}  •  {hub.arms.Count} branche(s)", EditorStyles.boldLabel);

            if (hub.conformTerrain) RoadEditorUtil.DrawTerrainStatus(hub.transform.position);

            // ---- Presets ----
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Préréglages", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Y")) Preset(hub, IntersectionPreset.Y);
                if (GUILayout.Button("T")) Preset(hub, IntersectionPreset.T);
                if (GUILayout.Button("X")) Preset(hub, IntersectionPreset.X);
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Rond-point Y")) Preset(hub, IntersectionPreset.RoundaboutY);
                if (GUILayout.Button("Rond-point T")) Preset(hub, IntersectionPreset.RoundaboutT);
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("Bout de route (cercle)", "Cul-de-sac : un disque avec une seule entrée, pour terminer une route proprement.")))
                    Preset(hub, IntersectionPreset.EndCap);
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                exits = EditorGUILayout.IntSlider(new GUIContent("Sorties", "Nombre de sorties du rond-point."), exits, 1, 12);
                if (GUILayout.Button("Rond-point N sorties", GUILayout.Width(150))) Preset(hub, IntersectionPreset.Roundabout, exits);
            }
            EditorGUILayout.HelpBox("Un préréglage remplace les angles des branches (les routes déjà raccordées sont conservées par index). Vous pouvez ensuite ajuster chaque branche librement.", MessageType.None);

            // ---- Propriétés générales ----
            var exclude = new List<string> { "m_Script", "arms", "sideObjects" };
            switch (hub.shape)
            {
                case IntersectionShape.Junction:
                    exclude.AddRange(new[] { "outerRadius", "ringWidth", "islandHeight", "flare", "flareLength", "centerIsland", "islandRadius" });
                    break;
                case IntersectionShape.Roundabout:
                    exclude.AddRange(new[] { "cornerRadius", "centerIsland", "islandRadius" });
                    break;
                default: // EndCap
                    exclude.AddRange(new[] { "cornerRadius", "ringWidth" });
                    if (!hub.centerIsland) exclude.AddRange(new[] { "islandHeight", "islandRadius" });
                    break;
            }
            DrawPropertiesExcluding(serializedObject, exclude.ToArray());

            // ---- Branches ----
            EditorGUILayout.Space(6);
            var list = serializedObject.FindProperty("arms");
            showArms = EditorGUILayout.Foldout(showArms, $"Branches ({list.arraySize})", true, EditorStyles.foldoutHeader);
            if (showArms)
            {
                int remove = -1;
                for (int i = 0; i < list.arraySize; i++)
                    if (DrawArm(hub, list.GetArrayElementAtIndex(i), i)) remove = i;
                if (remove >= 0) list.DeleteArrayElementAtIndex(remove);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(new GUIContent("Trottoirs partout", "Active le trottoir dans tous les coins.")))
                        SetAllSidewalks(list, true);
                    if (GUILayout.Button(new GUIContent("Aucun trottoir", "Retire le trottoir de tous les coins.")))
                        SetAllSidewalks(list, false);
                }
                EditorGUILayout.HelpBox("Astuce : en vue Scène, cliquez sur les cubes verts / rouges placés dans chaque coin pour ajouter ou retirer son trottoir.", MessageType.None);

                if (GUILayout.Button("+ Ajouter une branche"))
                {
                    int idx = list.arraySize;
                    list.InsertArrayElementAtIndex(idx);
                    var p = list.GetArrayElementAtIndex(idx);
                    p.FindPropertyRelative("yaw").floatValue = Mathf.Repeat((idx > 0 ? list.GetArrayElementAtIndex(idx - 1).FindPropertyRelative("yaw").floatValue : 0f) + 60f, 360f);
                    p.FindPropertyRelative("road").objectReferenceValue = null;
                    p.isExpanded = true;
                }
            }

            // ---- Objets latéraux ----
            EditorGUILayout.Space(6);
            SideObjectEditorUtil.DrawRuleList(this, serializedObject.FindProperty("sideObjects"), true, ref showRules);

            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space(6);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Reconstruire", GUILayout.Height(24))) hub.Rebuild(true);
                if (GUILayout.Button(new GUIContent("Raccorder les routes", "Replace les extrémités des routes liées sur leur branche."), GUILayout.Height(24)))
                {
                    hub.SnapRoads();
                    hub.Rebuild(false);
                }
                if (GUILayout.Button(new GUIContent("Poser au sol", "Place le centre à la hauteur du terrain de base."), GUILayout.Height(24)))
                {
                    Vector3 p = hub.transform.position;
                    if (TerrainModifierManager.TrySampleBaseHeight(p.x, p.z, out float y))
                    {
                        Undo.RecordObject(hub.transform, "Poser au sol");
                        hub.transform.position = new Vector3(p.x, y, p.z);
                    }
                    else
                        EditorUtility.DisplayDialog("Poser au sol", "Aucun terrain actif sous cette intersection.", "OK");
                }
            }
        }

        private static void SetAllSidewalks(SerializedProperty arms, bool value)
        {
            for (int i = 0; i < arms.arraySize; i++)
                arms.GetArrayElementAtIndex(i).FindPropertyRelative("sidewalkToNext").boolValue = value;
        }

        private static void Preset(RoadIntersection hub, IntersectionPreset preset, int n = 4)
        {
            Undo.RecordObject(hub, "Préréglage d'intersection");
            hub.ApplyPreset(preset, n);
            EditorUtility.SetDirty(hub);
        }

        private bool DrawArm(RoadIntersection hub, SerializedProperty p, int i)
        {
            bool del = false;
            var road = p.FindPropertyRelative("road");
            var yaw = p.FindPropertyRelative("yaw");

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            using (new EditorGUILayout.HorizontalScope())
            {
                string link = road.objectReferenceValue != null ? $"  →  {road.objectReferenceValue.name}" : "";
                p.isExpanded = EditorGUILayout.Foldout(p.isExpanded, $"Branche {i}   {yaw.floatValue:0}°{link}", true);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("✕", GUILayout.Width(24))) del = true;
            }

            if (p.isExpanded)
            {
                EditorGUILayout.Slider(yaw, 0f, 360f, new GUIContent("Direction (°)"));
                EditorGUILayout.PropertyField(p.FindPropertyRelative("length"), new GUIContent("Longueur"));

                bool fromRoad = hub.useRoadWidths && road.objectReferenceValue != null;
                using (new EditorGUI.DisabledScope(fromRoad))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.PropertyField(p.FindPropertyRelative("leftWidth"), new GUIContent("Gauche"));
                        EditorGUILayout.PropertyField(p.FindPropertyRelative("rightWidth"), new GUIContent("Droite"));
                    }
                }
                if (fromRoad) EditorGUILayout.LabelField("Largeurs reprises de la route raccordée.", EditorStyles.miniLabel);

                EditorGUILayout.PropertyField(p.FindPropertyRelative("sidewalkToNext"),
                    new GUIContent("Trottoir vers la branche suivante", "Trottoir dans le coin qui suit cette branche (sens horaire, vu du dessus)."));

                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.PropertyField(road, new GUIContent("Route"));
                    EditorGUILayout.PropertyField(p.FindPropertyRelative("roadEnd"), GUIContent.none, GUILayout.Width(70));
                }
            }
            EditorGUILayout.EndVertical();
            return del;
        }

        // ------------------------------------------------------------------ Créer une route depuis une branche
        private static void CreateRoadFromArm(RoadIntersection hub, IntersectionArm arm)
        {
            Vector3 far = hub.ArmFar(arm);
            far.y = hub.transform.position.y;
            Vector3 dir = hub.ArmDir(arm);

            var go = new GameObject("Road");
            Undo.RegisterCreatedObjectUndo(go, "Créer une route depuis la branche");
            go.transform.SetParent(hub.transform.parent, false);
            go.transform.SetPositionAndRotation(far, Quaternion.identity);

            var road = Undo.AddComponent<Road>(go);   // ajoute aussi le SplineContainer

            // Reprend les dimensions de la branche et l'apparence de l'intersection.
            hub.GetArmWidths(arm, out float wl, out float wr);
            road.leftWidth = wl;
            road.rightWidth = wr;
            road.roadMaterial = hub.roadMaterial;
            road.sidewalkMaterial = hub.sidewalkMaterial;
            road.heightOffset = hub.heightOffset;
            road.uvTileLength = hub.uvTileLength;
            road.terrainMargin = hub.terrainMargin;
            if (hub.sidewalks)
            {
                road.sections.Add(new RoadSection
                {
                    name = "Trottoirs",
                    overrideWidth = false,
                    sidewalkLeft = true,
                    sidewalkRight = true,
                    sidewalkLeftWidth = hub.sidewalkWidth,
                    sidewalkRightWidth = hub.sidewalkWidth,
                    sidewalkHeight = hub.sidewalkHeight,
                    start = 0f,
                    end = 1f
                });
            }

            // Spline de départ : 3 nœuds dans le prolongement de la branche (le premier est raccordé à la branche).
            var container = go.GetComponent<SplineContainer>();
            Spline sp = container.Spline;
            sp.Clear();
            foreach (float d in new[] { 0f, 20f, 40f })
            {
                Vector3 local = go.transform.InverseTransformPoint(far + dir * d);
                sp.Add(new BezierKnot(new Unity.Mathematics.float3(local.x, local.y, local.z)), TangentMode.AutoSmooth);
            }
            sp.Closed = false;

            // Raccord : la route part de la branche (son DÉBUT est à l'extrémité de la branche).
            Undo.RecordObject(hub, "Raccorder la route à la branche");
            arm.road = road;
            arm.roadEnd = RoadEnd.Start;
            EditorUtility.SetDirty(hub);

            hub.RequestRebuild(true);
            road.RequestRebuild();
            Selection.activeGameObject = go;
        }

        // ------------------------------------------------------------------ Scène
        private void OnSceneGUI()
        {
            var hub = (RoadIntersection)target;
            Vector3 c = hub.transform.position;

            // Double-clic sur une branche : crée une route (ou sélectionne celle qui est déjà raccordée).
            // Traité AVANT les poignées, sinon la poignée libre de la branche consomme le clic.
            Event ev = Event.current;
            if (ev.type == EventType.MouseDown && ev.button == 0 && ev.clickCount == 2 && !ev.alt)
            {
                foreach (var arm in hub.arms)
                {
                    if (arm == null) continue;
                    Vector2 gp = HandleUtility.WorldToGUIPoint(hub.ArmFar(arm));
                    if (Vector2.Distance(gp, ev.mousePosition) > 22f) continue;

                    ev.Use();
                    if (arm.road != null) Selection.activeGameObject = arm.road.gameObject;
                    else CreateRoadFromArm(hub, arm);
                    return;
                }
            }

            // Un bouton par coin : vert = trottoir, rouge = sans trottoir.
            var corners = new List<KeyValuePair<IntersectionArm, Vector3>>();
            hub.GetCornerAnchors(corners);
            foreach (var kv in corners)
            {
                if (kv.Key == null) continue;
                bool on = kv.Key.sidewalkToNext;
                float cs = HandleUtility.GetHandleSize(kv.Value) * 0.12f;
                Handles.color = on ? new Color(0.3f, 1f, 0.4f) : new Color(1f, 0.35f, 0.3f);
                if (Handles.Button(kv.Value + Vector3.up * 0.4f, Quaternion.identity, cs, cs * 1.5f, Handles.CubeHandleCap))
                {
                    Undo.RecordObject(hub, "Trottoir du coin");
                    kv.Key.sidewalkToNext = !on;
                    EditorUtility.SetDirty(hub);
                    hub.RequestRebuild(false);
                }
                Handles.Label(kv.Value + Vector3.up * (0.4f + cs * 2.5f), on ? "Trottoir" : "Sans trottoir");
            }

            for (int i = 0; i < hub.arms.Count; i++)
            {
                var arm = hub.arms[i];
                if (arm == null) continue;

                Vector3 far = hub.ArmFar(arm);
                float size = HandleUtility.GetHandleSize(far) * 0.13f;
                Handles.color = arm.road != null ? new Color(0.3f, 1f, 0.4f) : new Color(1f, 0.75f, 0.2f);

                // Poignée libre : règle direction ET longueur d'un coup.
                EditorGUI.BeginChangeCheck();
                Vector3 np = Handles.FreeMoveHandle(far, size, Vector3.zero, Handles.SphereHandleCap);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(hub, "Modifier la branche");
                    Vector3 delta = np - c; delta.y = 0f;
                    if (delta.sqrMagnitude > 0.25f)
                    {
                        Vector3 local = hub.transform.InverseTransformDirection(delta);
                        float yaw = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
                        arm.yaw = Mathf.Repeat(yaw, 360f);
                        arm.length = Mathf.Max(1f, delta.magnitude);
                        EditorUtility.SetDirty(hub);
                        hub.RequestRebuild(true);
                    }
                }
                Handles.Label(far + Vector3.up * size * 2.5f,
                    $"Branche {i}  {arm.yaw:0}°  {hub.EffectiveLength(arm):0.#} m" +
                    (arm.road != null ? "  [route]  (double-clic : sélectionner)" : "  (double-clic : créer une route)"));
            }

            if (hub.shape != IntersectionShape.Junction)
            {
                Handles.color = new Color(1f, 0.55f, 0.1f);
                Vector3 hp = c + hub.transform.right * hub.outerRadius;
                float s = HandleUtility.GetHandleSize(hp) * 0.1f;
                EditorGUI.BeginChangeCheck();
                Vector3 np = Handles.Slider(hp, hub.transform.right, s, Handles.CubeHandleCap, 0f);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(hub, "Rayon du rond-point");
                    hub.outerRadius = Mathf.Max(hub.shape == IntersectionShape.EndCap ? 3f : hub.ringWidth + 1f, Vector3.Dot(np - c, hub.transform.right));
                    EditorUtility.SetDirty(hub);
                    hub.RequestRebuild(true);
                }
                Handles.Label(hp + Vector3.up * s * 2.5f, $"Rayon {hub.outerRadius:0.#} m");
            }
        }
    }
}
#endif
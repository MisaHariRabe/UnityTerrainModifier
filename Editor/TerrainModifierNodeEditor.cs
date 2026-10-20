#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(TerrainModifierNode))]
[CanEditMultipleObjects]
public class TerrainModifierNodeEditor : Editor
{
    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        var node = (TerrainModifierNode)target;
        Color col = ModifierMath.ColorOf(node.operation);

        // ---- Bandeau d'en-tête ----
        Rect header = GUILayoutUtility.GetRect(0, 22, GUILayout.ExpandWidth(true));
        EditorGUI.DrawRect(header, new Color(col.r, col.g, col.b, 0.25f));
        EditorGUI.DrawRect(new Rect(header.x, header.y, 4, header.height), col);
        GUI.Label(new Rect(header.x + 10, header.y + 2, header.width - 12, 18),
                  $"Point  •  {node.operation}  •  R {node.radius:0.#} m  •  force {node.strength:0.00}", EditorStyles.boldLabel);

        if (TerrainModifierManager.Managers.Count == 0)
        {
            EditorGUILayout.HelpBox("Aucun TerrainModifierManager dans la scène : ce nœud n'aura aucun effet.", MessageType.Warning);
            if (Terrain.activeTerrain != null && GUILayout.Button("Ajouter un Manager au terrain actif"))
                Undo.AddComponent<TerrainModifierManager>(Terrain.activeTerrain.gameObject);
        }

        // ---- Opération ----
        EditorGUILayout.PropertyField(serializedObject.FindProperty("operation"));
        EditorGUILayout.HelpBox(ModifierMath.Describe(node.operation), MessageType.None);

        // ---- Paramètres ----
        EditorGUILayout.Space(2);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("radius"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("strength"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("smoothness"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("layerOrder"));

        // ---- Falloff ----
        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Décroissance", EditorStyles.boldLabel);
        var curveProp = serializedObject.FindProperty("falloffCurve");
        EditorGUILayout.PropertyField(curveProp, GUIContent.none, GUILayout.Height(48));

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Linéaire")) curveProp.animationCurveValue = TerrainModifierNode.CurveLinear();
            if (GUILayout.Button("Lisse")) curveProp.animationCurveValue = TerrainModifierNode.CurveSmooth();
            if (GUILayout.Button("Doux")) curveProp.animationCurveValue = TerrainModifierNode.CurveSoft();
            if (GUILayout.Button("Plateau")) curveProp.animationCurveValue = TerrainModifierNode.CurvePlateau();
            if (GUILayout.Button("Pointe")) curveProp.animationCurveValue = TerrainModifierNode.CurveSpike();
        }

        EditorGUILayout.PropertyField(serializedObject.FindProperty("showGizmos"));
        serializedObject.ApplyModifiedProperties();

        // ---- Outils ----
        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Outils", EditorStyles.boldLabel);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button(new GUIContent("Poser au sol", "Place le nœud à la hauteur du terrain de base.")))
            {
                foreach (var o in targets)
                {
                    var n = (TerrainModifierNode)o;
                    Vector3 p = n.transform.position;
                    if (TerrainModifierManager.TrySampleBaseHeight(p.x, p.z, out float y))
                    {
                        Undo.RecordObject(n.transform, "Poser au sol");
                        n.transform.position = new Vector3(p.x, y, p.z);
                    }
                }
            }
            if (GUILayout.Button(new GUIContent("Monter +5 m", "Raccourci pour surélever le nœud."))) Nudge(5f);
            if (GUILayout.Button(new GUIContent("Descendre -5 m", "Raccourci pour abaisser le nœud."))) Nudge(-5f);
        }
    }

    private void Nudge(float dy)
    {
        foreach (var o in targets)
        {
            var n = (TerrainModifierNode)o;
            Undo.RecordObject(n.transform, "Déplacer le nœud");
            n.transform.position += Vector3.up * dy;
        }
    }

    private void OnSceneGUI()
    {
        var n = (TerrainModifierNode)target;
        Vector3 p = n.transform.position;
        Color col = ModifierMath.ColorOf(n.operation);
        Handles.color = col;

        // Poignée de rayon : glisse le long de l'axe X, posée sur le terrain.
        Vector3 hp = new Vector3(p.x + n.radius, ModifierMath.GroundY(p.x + n.radius, p.z, p.y) + 0.2f, p.z);
        float size = HandleUtility.GetHandleSize(hp) * 0.1f;

        EditorGUI.BeginChangeCheck();
        Vector3 np = Handles.Slider(hp, Vector3.right, size, Handles.CubeHandleCap, 0f);
        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(n, "Modifier le rayon");
            n.radius = Mathf.Max(0.5f, np.x - p.x);
            EditorUtility.SetDirty(n);
            TerrainModifierManager.RequestUpdate();
        }

        Handles.Label(hp + Vector3.up * size * 3f, $"{n.name}\nR = {n.radius:0.#} m  •  {n.operation}\nHauteur {p.y:0.#} m");
    }
}
#endif

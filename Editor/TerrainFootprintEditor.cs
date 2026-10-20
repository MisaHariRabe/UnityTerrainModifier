#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

[CustomEditor(typeof(TerrainFootprint))]
[CanEditMultipleObjects]
public class TerrainFootprintEditor : Editor
{
    private FootprintGroundMode groundMode = FootprintGroundMode.Average;
    private readonly BoxBoundsHandle boxHandle = new BoxBoundsHandle();

    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        var fp = (TerrainFootprint)target;
        Color col = ModifierMath.ColorOf(fp.operation);

        Rect header = GUILayoutUtility.GetRect(0, 22, GUILayout.ExpandWidth(true));
        EditorGUI.DrawRect(header, new Color(col.r, col.g, col.b, 0.25f));
        EditorGUI.DrawRect(new Rect(header.x, header.y, 4, header.height), col);
        GUI.Label(new Rect(header.x + 10, header.y + 2, header.width - 12, 18),
                  $"Empreinte  •  {fp.size.x:0.#} × {fp.size.y:0.#} m  •  sol à {fp.TargetY:0.##} m", EditorStyles.boldLabel);

        if (TerrainModifierManager.Managers.Count == 0)
        {
            EditorGUILayout.HelpBox("Aucun TerrainModifierManager dans la scène : l'empreinte n'aura aucun effet.", MessageType.Warning);
            if (Terrain.activeTerrain != null && GUILayout.Button("Ajouter un Manager au terrain actif"))
                Undo.AddComponent<TerrainModifierManager>(Terrain.activeTerrain.gameObject);
        }

        // ---- Raccourcis ----
        EditorGUILayout.Space(2);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button(new GUIContent("Ajuster à la géométrie", "Mesure les meshes de l'objet : taille, centre et bas du bâtiment.")))
            {
                foreach (var t in targets)
                {
                    var f = (TerrainFootprint)t;
                    Undo.RecordObject(f, "Ajuster l'empreinte");
                    if (!f.FitToGeometry()) Debug.LogWarning($"[Footprint] {f.name} : aucun MeshRenderer trouvé.");
                    EditorUtility.SetDirty(f);
                }
            }
        }
        using (new EditorGUILayout.HorizontalScope())
        {
            groundMode = (FootprintGroundMode)EditorGUILayout.EnumPopup(groundMode, GUILayout.Width(110));
            if (GUILayout.Button(new GUIContent("Poser sur le terrain", "Déplace l'objet en hauteur pour que son sol colle au terrain d'origine (moyenne, point haut, point bas ou centre).")))
            {
                foreach (var t in targets)
                {
                    var f = (TerrainFootprint)t;
                    Undo.RecordObject(f.transform, "Poser sur le terrain");
                    f.PlaceOnGround(groundMode);
                }
            }
        }
        EditorGUILayout.HelpBox(
            "Moyenne : autant de déblai que de remblai.  Point haut : on ne creuse presque pas, on comble.  Point bas : on creuse, on ne comble pas.\n" +
            "Tu peux ensuite monter/descendre l'objet à la main : le terrain suit.", MessageType.None);

        // ---- Empreinte ----
        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Empreinte", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("size"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("centerOffset"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("margin"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("blendDistance"));

        // ---- Hauteur ----
        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Hauteur", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("floorOffset"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("heightOffset"));

        // ---- Terrain ----
        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Terrain", EditorStyles.boldLabel);
        var opProp = serializedObject.FindProperty("operation");
        EditorGUI.BeginChangeCheck();
        // Seulement Blend / Raise / Lower.
        ModifierOperation cur = (ModifierOperation)opProp.intValue;
        string[] labels = { "Aplanir (Blend)", "Combler seulement (Raise)", "Creuser seulement (Lower)" };
        ModifierOperation[] ops = { ModifierOperation.Blend, ModifierOperation.Raise, ModifierOperation.Lower };
        int idx = System.Array.IndexOf(ops, cur); if (idx < 0) idx = 0;
        idx = EditorGUILayout.Popup("Opération", idx, labels);
        if (EditorGUI.EndChangeCheck()) opProp.intValue = (int)ops[idx];
        EditorGUILayout.PropertyField(serializedObject.FindProperty("strength"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("smoothness"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("falloffCurve"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("layerOrder"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("showGizmos"));

        if (serializedObject.ApplyModifiedProperties())
            TerrainModifierManager.RequestUpdate();
    }

    // Poignées de la boîte dans la scène.
    private void OnSceneGUI()
    {
        var fp = (TerrainFootprint)target;
        float yaw = fp.transform.eulerAngles.y;
        Vector3 pos = fp.transform.position;
        pos.y = fp.TargetY;

        using (new Handles.DrawingScope(Matrix4x4.TRS(pos, Quaternion.Euler(0f, yaw, 0f), Vector3.one)))
        {
            boxHandle.axes = PrimitiveBoundsHandle.Axes.X | PrimitiveBoundsHandle.Axes.Z;
            boxHandle.center = new Vector3(fp.centerOffset.x, 0f, fp.centerOffset.y);
            boxHandle.size = new Vector3(fp.size.x, 0f, fp.size.y);
            var c = ModifierMath.ColorOf(fp.operation);
            boxHandle.SetColor(c);

            EditorGUI.BeginChangeCheck();
            boxHandle.DrawHandle();
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(fp, "Redimensionner l'empreinte");
                fp.centerOffset = new Vector2(boxHandle.center.x, boxHandle.center.z);
                fp.size = new Vector2(Mathf.Max(0.5f, boxHandle.size.x), Mathf.Max(0.5f, boxHandle.size.z));
                TerrainModifierManager.RequestUpdate();
            }
        }
    }
}
#endif
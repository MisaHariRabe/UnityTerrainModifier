#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

// ======================================================================
//  Aperçu (miniature ombrée de la heightmap)
// ======================================================================
public static class StampPreview
{
    private static readonly Dictionary<TerrainStamp, Texture2D> cache = new Dictionary<TerrainStamp, Texture2D>();

    public static Texture2D Get(TerrainStamp s)
    {
        if (s == null || !s.IsValid) return null;
        if (cache.TryGetValue(s, out var t) && t != null) return t;
        t = Build(s, 128, 128);
        cache[s] = t;
        return t;
    }

    public static Texture2D Build(TerrainStamp s, int w, int h)
    {
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
        float mn = s.MinHeight, range = Mathf.Max(0.01f, s.MaxHeight - s.MinHeight);
        float eu = 1f / w, ev = 1f / h;
        var px = new Color[w * h];

        for (int y = 0; y < h; y++)
        {
            float v = y / (float)(h - 1);
            for (int x = 0; x < w; x++)
            {
                float u = x / (float)(w - 1);
                float t = (s.Sample(u, v) - mn) / range;
                float hx = s.Sample(u + eu, v) - s.Sample(u - eu, v);
                float hz = s.Sample(u, v + ev) - s.Sample(u, v - ev);
                float shade = Mathf.Clamp(0.85f - (hx + hz) / range * w * 0.12f, 0.45f, 1.25f);
                float g = Mathf.Clamp01((0.15f + t * 0.7f) * shade);
                px[y * w + x] = new Color(g * 0.95f, g, g * 0.9f, 1f);
            }
        }
        tex.SetPixels(px);
        tex.Apply();
        return tex;
    }
}

// ======================================================================
//  Inspecteur de l'asset TerrainStamp
// ======================================================================
[CustomEditor(typeof(TerrainStamp))]
public class TerrainStampAssetEditor : Editor
{
    public override void OnInspectorGUI()
    {
        var s = (TerrainStamp)target;
        if (!s.IsValid)
        {
            EditorGUILayout.HelpBox("Stamp vide. Crée-le depuis Window > Terrain > Stamp Creator.", MessageType.Warning);
            return;
        }

        Rect r = GUILayoutUtility.GetRect(0, 170, GUILayout.ExpandWidth(true));
        EditorGUI.DrawRect(r, new Color(0.14f, 0.14f, 0.14f));
        GUI.DrawTexture(r, StampPreview.Get(s), ScaleMode.ScaleToFit);

        EditorGUILayout.HelpBox(
            $"{s.Resolution} × {s.Resolution} px  •  taille native {s.nativeSize.x:0.#} × {s.nativeSize.y:0.#} m\n" +
            $"Relief : {s.MinHeight:0.##} m → {s.MaxHeight:0.##} m  (amplitude {s.MaxHeight - s.MinHeight:0.##} m)", MessageType.None);

        serializedObject.Update();
        EditorGUILayout.PropertyField(serializedObject.FindProperty("nativeSize"));
        serializedObject.ApplyModifiedProperties();

        EditorGUILayout.Space(4);
        if (GUILayout.Button("Placer dans la scène", GUILayout.Height(24))) StampMenu.PlaceInScene(s);
    }

    public override Texture2D RenderStaticPreview(string assetPath, Object[] subAssets, int width, int height)
    {
        var s = (TerrainStamp)target;
        return s != null && s.IsValid ? StampPreview.Build(s, width, height) : null;
    }
}

// ======================================================================
//  Inspecteur du modificateur
// ======================================================================
[CustomEditor(typeof(TerrainStampModifier))]
[CanEditMultipleObjects]
public class TerrainStampModifierEditor : Editor
{
    private readonly BoxBoundsHandle boxHandle = new BoxBoundsHandle();
    private float uniformScale = 1f;

    private static readonly ModifierOperation[] Ops =
        { ModifierOperation.Add, ModifierOperation.Subtract, ModifierOperation.Blend, ModifierOperation.Raise, ModifierOperation.Lower };
    private static readonly string[] OpLabels =
        { "Ajouter (relief posé sur le terrain)", "Soustraire (creuse le relief)", "Remplacer (hauteur absolue)", "Combler seulement (Raise)", "Creuser seulement (Lower)" };

    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        var m = (TerrainStampModifier)target;
        Color col = ModifierMath.ColorOf(m.operation);

        Rect header = GUILayoutUtility.GetRect(0, 22, GUILayout.ExpandWidth(true));
        EditorGUI.DrawRect(header, new Color(col.r, col.g, col.b, 0.25f));
        EditorGUI.DrawRect(new Rect(header.x, header.y, 4, header.height), col);
        string stampName = m.stamp != null ? m.stamp.name : "aucun stamp";
        GUI.Label(new Rect(header.x + 10, header.y + 2, header.width - 12, 18),
                  $"Stamp  •  {stampName}  •  {m.size.x:0.#} × {m.size.y:0.#} m", EditorStyles.boldLabel);

        if (TerrainModifierManager.Managers.Count == 0)
        {
            EditorGUILayout.HelpBox("Aucun TerrainModifierManager dans la scène : le stamp n'aura aucun effet.", MessageType.Warning);
            if (Terrain.activeTerrain != null && GUILayout.Button("Ajouter un Manager au terrain actif"))
                Undo.AddComponent<TerrainModifierManager>(Terrain.activeTerrain.gameObject);
        }

        // ---- Stamp ----
        var stampProp = serializedObject.FindProperty("stamp");
        EditorGUI.BeginChangeCheck();
        EditorGUILayout.PropertyField(stampProp);
        bool stampChanged = EditorGUI.EndChangeCheck();

        var tex = m.stamp != null ? StampPreview.Get(m.stamp) : null;
        if (tex != null)
        {
            Rect r = GUILayoutUtility.GetRect(0, 96, GUILayout.ExpandWidth(true));
            GUI.DrawTexture(r, tex, ScaleMode.ScaleToFit);
        }
        else if (m.stamp == null)
            EditorGUILayout.HelpBox("Glisse un TerrainStamp ici (Window > Terrain > Stamp Creator pour en créer).", MessageType.Info);

        EditorGUILayout.PropertyField(serializedObject.FindProperty("size"));
        using (new EditorGUILayout.HorizontalScope())
        {
            uniformScale = EditorGUILayout.FloatField(new GUIContent("Échelle", "Multiple de la taille native."), uniformScale);
            if (GUILayout.Button("Appliquer", GUILayout.Width(70)) && m.stamp != null)
                foreach (var t in targets)
                {
                    var x = (TerrainStampModifier)t;
                    if (x.stamp == null) continue;
                    Undo.RecordObject(x, "Échelle du stamp");
                    x.size = x.stamp.nativeSize * Mathf.Max(0.01f, uniformScale);
                    TerrainModifierManager.RequestUpdate();
                }
            if (GUILayout.Button("Taille native", GUILayout.Width(90)))
                foreach (var t in targets)
                {
                    var x = (TerrainStampModifier)t;
                    Undo.RecordObject(x, "Taille native");
                    x.ResetSizeToNative();
                    uniformScale = 1f;
                }
        }
        EditorGUILayout.PropertyField(serializedObject.FindProperty("heightScale"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("heightOffset"));
        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.PropertyField(serializedObject.FindProperty("flipX"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("flipZ"));
        }

        // ---- Terrain ----
        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Terrain", EditorStyles.boldLabel);
        var opProp = serializedObject.FindProperty("operation");
        int idx = System.Array.IndexOf(Ops, (ModifierOperation)opProp.intValue);
        if (idx < 0) idx = 0;
        EditorGUI.BeginChangeCheck();
        idx = EditorGUILayout.Popup("Opération", idx, OpLabels);
        if (EditorGUI.EndChangeCheck()) opProp.intValue = (int)Ops[idx];

        bool absolute = Ops[idx] == ModifierOperation.Blend || Ops[idx] == ModifierOperation.Raise || Ops[idx] == ModifierOperation.Lower;
        EditorGUILayout.HelpBox(absolute
            ? "Mode absolu : la hauteur Y de l'objet fixe la base du stamp. Monte / descends l'objet, le terrain suit."
            : "Mode relatif : le relief s'ajoute au terrain existant, la hauteur Y de l'objet est ignorée.", MessageType.None);

        EditorGUILayout.PropertyField(serializedObject.FindProperty("strength"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("edgeFade"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("roundMask"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("smoothness"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("layerOrder"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("showGizmos"));

        if (serializedObject.ApplyModifiedProperties())
        {
            if (stampChanged)
                foreach (var t in targets)
                {
                    var x = (TerrainStampModifier)t;
                    if (x.stamp == null) continue;
                    Undo.RecordObject(x, "Assigner le stamp");
                    x.size = x.stamp.nativeSize;
                    EditorUtility.SetDirty(x);
                }
            TerrainModifierManager.RequestUpdate();
        }

        EditorGUILayout.Space(4);
        if (GUILayout.Button(new GUIContent("Poser sur le terrain", "Place la base du stamp à la hauteur du terrain d'origine, au centre."), GUILayout.Height(22)))
            foreach (var t in targets)
            {
                var x = (TerrainStampModifier)t;
                Undo.RecordObject(x.transform, "Poser sur le terrain");
                x.PlaceOnGround();
            }
    }

    private void OnSceneGUI()
    {
        var m = (TerrainStampModifier)target;
        Vector3 pos = m.transform.position;
        pos.y = m.BaseY;

        using (new Handles.DrawingScope(Matrix4x4.TRS(pos, Quaternion.Euler(0f, m.transform.eulerAngles.y, 0f), Vector3.one)))
        {
            boxHandle.axes = PrimitiveBoundsHandle.Axes.X | PrimitiveBoundsHandle.Axes.Z;
            boxHandle.center = Vector3.zero;
            boxHandle.size = new Vector3(m.size.x, 0f, m.size.y);
            boxHandle.SetColor(ModifierMath.ColorOf(m.operation));

            EditorGUI.BeginChangeCheck();
            boxHandle.DrawHandle();
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(m, "Redimensionner le stamp");
                m.size = new Vector2(Mathf.Max(0.5f, boxHandle.size.x), Mathf.Max(0.5f, boxHandle.size.z));
                TerrainModifierManager.RequestUpdate();
            }
        }
    }
}

// ======================================================================
//  Menus
// ======================================================================
public static class StampMenu
{
    [MenuItem("GameObject/Terrain Modifiers/Stamp (vide)", false, 23)]
    private static void CreateEmpty(MenuCommand cmd)
    {
        var go = new GameObject("Terrain Stamp");
        Undo.RegisterCreatedObjectUndo(go, "Créer un Stamp");
        GameObjectUtility.SetParentAndAlign(go, cmd.context as GameObject);
        go.transform.position = SpawnPoint();
        var m = Undo.AddComponent<TerrainStampModifier>(go);
        TerrainModifierManager.EnsureManagersForBounds(m.WorldBounds());
        Selection.activeGameObject = go;
    }

    [MenuItem("Assets/Terrain/Placer le stamp dans la scène")]
    private static void PlaceSelected()
    {
        foreach (var o in Selection.objects)
            if (o is TerrainStamp s) PlaceInScene(s);
    }

    [MenuItem("Assets/Terrain/Placer le stamp dans la scène", true)]
    private static bool PlaceSelectedValidate() => Selection.activeObject is TerrainStamp;

    [MenuItem("Assets/Terrain/Créer un stamp depuis ce TerrainData")]
    private static void CreateFromSelectedTerrainData()
    {
        TerrainStamp last = null;
        foreach (var o in Selection.objects)
        {
            if (!(o is TerrainData td)) continue;
            string dir = System.IO.Path.GetDirectoryName(AssetDatabase.GetAssetPath(td)).Replace('\\', '/');
            last = StampBuilder.Create(td, StampBaseline.Minimum, 0, dir, null);
        }
        if (last != null) { Selection.activeObject = last; EditorGUIUtility.PingObject(last); }
    }

    [MenuItem("Assets/Terrain/Créer un stamp depuis ce TerrainData", true)]
    private static bool CreateFromSelectedTerrainDataValidate() => Selection.activeObject is TerrainData;

    public static TerrainStampModifier PlaceInScene(TerrainStamp stamp)
    {
        if (stamp == null) return null;
        var go = new GameObject(stamp.name);
        Undo.RegisterCreatedObjectUndo(go, "Placer un stamp");
        go.transform.position = SpawnPoint();

        var m = Undo.AddComponent<TerrainStampModifier>(go);
        m.stamp = stamp;
        m.size = stamp.nativeSize;
        TerrainModifierManager.EnsureManagersForBounds(m.WorldBounds());
        m.PlaceOnGround();
        Selection.activeGameObject = go;
        return m;
    }

    private static Vector3 SpawnPoint()
    {
        Vector3 p = Vector3.zero;
        var sv = SceneView.lastActiveSceneView;
        if (sv != null) p = sv.pivot;
        if (TerrainModifierManager.TrySampleBaseHeight(p.x, p.z, out float y)) p.y = y;
        return p;
    }
}
#endif
#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>Référence de hauteur 0 du stamp.</summary>
public enum StampBaseline
{
    Minimum,        // le point le plus bas devient 0 (stamp = "bosse" pure)
    EdgeAverage,    // la moyenne du pourtour devient 0 (le stamp se raccorde naturellement au terrain)
    None            // hauteurs conservées telles quelles (depuis le bas du terrain source)
}

public static class StampBuilder
{
    /// <summary>Convertit un TerrainData en asset TerrainStamp. maxResolution = 0 : résolution d'origine.</summary>
    public static TerrainStamp Create(TerrainData td, StampBaseline baseline, int maxResolution, string folder, string nameOverride)
    {
        if (td == null) return null;
        td.SyncHeightmap();

        int res = td.heightmapResolution;
        float[,] src = td.GetHeights(0, 0, res, res);       // [z, x], 0..1
        float sy = td.size.y;

        int nr = (maxResolution > 1 && maxResolution < res) ? maxResolution : res;
        var m = new float[nr * nr];
        for (int z = 0; z < nr; z++)
        {
            float fz = z / (float)(nr - 1) * (res - 1);
            int iz = Mathf.Min((int)fz, res - 2); float tz = fz - iz;
            for (int x = 0; x < nr; x++)
            {
                float fx = x / (float)(nr - 1) * (res - 1);
                int ix = Mathf.Min((int)fx, res - 2); float tx = fx - ix;
                float a = Mathf.Lerp(src[iz, ix], src[iz, ix + 1], tx);
                float b = Mathf.Lerp(src[iz + 1, ix], src[iz + 1, ix + 1], tx);
                m[z * nr + x] = Mathf.Lerp(a, b, tz) * sy;
            }
        }

        float offset = 0f;
        if (baseline == StampBaseline.Minimum)
        {
            offset = float.MaxValue;
            foreach (float v in m) offset = Mathf.Min(offset, v);
        }
        else if (baseline == StampBaseline.EdgeAverage)
        {
            float sum = 0f; int n = 0;
            for (int i = 0; i < nr; i++)
            {
                sum += m[i] + m[(nr - 1) * nr + i] + m[i * nr] + m[i * nr + nr - 1];
                n += 4;
            }
            offset = sum / n;
        }
        if (offset != 0f) for (int i = 0; i < m.Length; i++) m[i] -= offset;

        var stamp = ScriptableObject.CreateInstance<TerrainStamp>();
        stamp.SetData(m, nr, new Vector2(td.size.x, td.size.z));

        EnsureFolder(folder);
        string name = string.IsNullOrEmpty(nameOverride) ? td.name : nameOverride;
        string path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{name}_Stamp.asset");
        AssetDatabase.CreateAsset(stamp, path);
        AssetDatabase.SaveAssets();
        return stamp;
    }

    private static void EnsureFolder(string folder)
    {
        if (AssetDatabase.IsValidFolder(folder)) return;
        string[] parts = folder.Split('/');
        string cur = parts[0];
        for (int i = 1; i < parts.Length; i++)
        {
            string next = cur + "/" + parts[i];
            if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(cur, parts[i]);
            cur = next;
        }
    }
}

/// <summary>Fenêtre : glisse des terrains (issus de ton outil mesh -> terrain) et convertis-les en stamps.</summary>
public class StampCreatorWindow : EditorWindow
{
    [SerializeField] private List<TerrainData> sources = new List<TerrainData>();
    [SerializeField] private StampBaseline baseline = StampBaseline.Minimum;
    [SerializeField] private int resolutionIndex = 0;
    [SerializeField] private string folder = "Assets/Stamps";

    private static readonly int[] ResValues = { 0, 129, 257, 513, 1025 };
    private static readonly string[] ResLabels = { "Originale", "129", "257", "513", "1025" };

    private Vector2 scroll;

    [MenuItem("Window/Terrain/Stamp Creator")]
    public static void Open() => GetWindow<StampCreatorWindow>("Stamp Creator");

    private void OnGUI()
    {
        EditorGUILayout.Space(4);
        EditorGUILayout.HelpBox(
            "1. Glisse ici les Terrains (scène) ou TerrainData (Project) générés par ton outil mesh → terrain.\n" +
            "2. Choisis la référence de hauteur et la résolution.\n" +
            "3. « Créer » génère un asset TerrainStamp par source. Place-le ensuite avec un clic droit sur l'asset > Terrain > Placer le stamp.",
            MessageType.Info);

        DrawDropZone();

        if (GUILayout.Button("Ajouter la sélection")) AddRange(Selection.objects);

        scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.MaxHeight(180));
        int remove = -1;
        for (int i = 0; i < sources.Count; i++)
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                sources[i] = (TerrainData)EditorGUILayout.ObjectField(sources[i], typeof(TerrainData), false);
                if (sources[i] != null)
                    GUILayout.Label($"{sources[i].heightmapResolution}px  {sources[i].size.x:0}×{sources[i].size.z:0} m", EditorStyles.miniLabel, GUILayout.Width(120));
                if (GUILayout.Button("✕", GUILayout.Width(22))) remove = i;
            }
        }
        EditorGUILayout.EndScrollView();
        if (remove >= 0) sources.RemoveAt(remove);

        EditorGUILayout.Space(4);
        baseline = (StampBaseline)EditorGUILayout.EnumPopup(new GUIContent("Hauteur 0 du stamp",
            "Minimum : point le plus bas = 0.\nEdgeAverage : moyenne du pourtour = 0 (raccord naturel).\nNone : conserve les hauteurs d'origine."), baseline);
        resolutionIndex = EditorGUILayout.Popup(new GUIContent("Résolution max", "Réduire allège l'asset (le stamp est rééchantillonné)."), resolutionIndex, ResLabels);
        folder = EditorGUILayout.TextField("Dossier de sortie", folder);

        EditorGUILayout.Space(4);
        using (new EditorGUI.DisabledScope(sources.TrueForAll(s => s == null)))
        {
            if (GUILayout.Button("Créer les stamps", GUILayout.Height(28))) CreateAll();
        }
    }

    private void DrawDropZone()
    {
        Rect r = GUILayoutUtility.GetRect(0, 44, GUILayout.ExpandWidth(true));
        GUI.Box(r, "Glisse des Terrains / TerrainData ici", EditorStyles.helpBox);

        Event e = Event.current;
        if (!r.Contains(e.mousePosition)) return;
        if (e.type == EventType.DragUpdated) { DragAndDrop.visualMode = DragAndDropVisualMode.Copy; e.Use(); }
        else if (e.type == EventType.DragPerform)
        {
            DragAndDrop.AcceptDrag();
            AddRange(DragAndDrop.objectReferences);
            e.Use();
        }
    }

    private void AddRange(IEnumerable<Object> objs)
    {
        foreach (var o in objs)
        {
            TerrainData td = null;
            if (o is TerrainData d) td = d;
            else if (o is Terrain t) td = t.terrainData;
            else if (o is GameObject go && go.TryGetComponent(out Terrain gt)) td = gt.terrainData;
            if (td != null && !sources.Contains(td)) sources.Add(td);
        }
    }

    private void CreateAll()
    {
        TerrainStamp last = null;
        int count = 0;
        foreach (var td in sources)
        {
            if (td == null) continue;
            last = StampBuilder.Create(td, baseline, ResValues[resolutionIndex], folder.TrimEnd('/'), null);
            if (last != null) count++;
        }
        if (last != null)
        {
            Selection.activeObject = last;
            EditorGUIUtility.PingObject(last);
            Debug.Log($"[Stamp Creator] {count} stamp(s) créé(s) dans {folder}.");
        }
    }
}
#endif
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>Type de bâtiment : valeurs par défaut de marge et de raccord.</summary>
public enum BuildingPreset { House, BusStop, GasStation, Shop, Garage, Custom }

public static class BuildingPresets
{
    public static string Label(BuildingPreset p) => p switch
    {
        BuildingPreset.House => "Maison",
        BuildingPreset.BusStop => "Arrêt de bus",
        BuildingPreset.GasStation => "Station-service",
        BuildingPreset.Shop => "Commerce",
        BuildingPreset.Garage => "Garage / entrepôt",
        _ => "Personnalisé"
    };

    // margin = sol plat autour, blend = pente de raccord
    public static void Defaults(BuildingPreset p, out float margin, out float blend)
    {
        switch (p)
        {
            case BuildingPreset.House: margin = 2f; blend = 6f; break;
            case BuildingPreset.BusStop: margin = 0.5f; blend = 2f; break;
            case BuildingPreset.GasStation: margin = 4f; blend = 8f; break;
            case BuildingPreset.Shop: margin = 3f; blend = 7f; break;
            case BuildingPreset.Garage: margin = 3f; blend = 8f; break;
            default: margin = 1.5f; blend = 6f; break;
        }
    }
}

[Serializable]
public class BuildingEntry
{
    public GameObject prefab;
    public BuildingPreset preset = BuildingPreset.House;
    public float margin = 2f;
    public float blend = 6f;
    public float heightOffset = 0f;
}

/// <summary>
/// Fenêtre "Building Placer" : glisse des prefabs, choisis-en un, clique dans la scène pour le poser.
/// Un TerrainFootprint est ajouté automatiquement : le terrain s'aplanit dessous, non destructivement.
/// </summary>
public class BuildingPlacerWindow : EditorWindow
{
    [SerializeField] private List<BuildingEntry> entries = new List<BuildingEntry>();
    [SerializeField] private int selected = -1;
    [SerializeField] private bool placing;
    [SerializeField] private FootprintGroundMode groundMode = FootprintGroundMode.Average;
    [SerializeField] private float yaw;
    [SerializeField] private float rotationStep = 15f;
    [SerializeField] private bool randomYaw;
    [SerializeField] private bool keepPlacing = true;
    [SerializeField] private string parentName = "Buildings";

    private Vector2 scroll;

    [MenuItem("Window/Terrain/Building Placer")]
    [MenuItem("GameObject/Terrain Modifiers/Building Placer…", false, 30)]
    public static void Open() => GetWindow<BuildingPlacerWindow>("Building Placer");

    private void OnEnable() => SceneView.duringSceneGui += OnSceneGUI;
    private void OnDisable() { SceneView.duringSceneGui -= OnSceneGUI; placing = false; }

    // ------------------------------------------------------------------
    //  Fenêtre
    // ------------------------------------------------------------------
    private void OnGUI()
    {
        EditorGUILayout.Space(4);
        EditorGUILayout.HelpBox(
            "1. Glisse tes prefabs (maisons, arrêts de bus, stations...) dans la zone.\n" +
            "2. Clique sur une ligne pour la choisir, puis sur « Placer ».\n" +
            "3. Clique dans la Scene : le bâtiment se pose et le terrain s'aplanit dessous (Q / E pour tourner, Échap pour arrêter).", MessageType.Info);

        DrawDropZone();

        scroll = EditorGUILayout.BeginScrollView(scroll);
        for (int i = 0; i < entries.Count; i++) DrawEntry(i);
        EditorGUILayout.EndScrollView();

        EditorGUILayout.Space(4);
        groundMode = (FootprintGroundMode)EditorGUILayout.EnumPopup(
            new GUIContent("Hauteur du sol", "Moyenne = autant de déblai que de remblai ; Haut = on comble ; Bas = on creuse."), groundMode);
        rotationStep = EditorGUILayout.Slider("Pas de rotation (Q/E)", rotationStep, 1f, 90f);
        randomYaw = EditorGUILayout.Toggle(new GUIContent("Rotation aléatoire", "Chaque pose tire un angle au hasard (multiple du pas)."), randomYaw);
        keepPlacing = EditorGUILayout.Toggle(new GUIContent("Continuer après un clic", "Reste en mode placement pour en poser plusieurs."), keepPlacing);
        parentName = EditorGUILayout.TextField(new GUIContent("Parent (vide = racine)"), parentName);

        EditorGUILayout.Space(4);
        using (new EditorGUI.DisabledScope(selected < 0 || selected >= entries.Count || entries[selected].prefab == null))
        {
            var prev = GUI.backgroundColor;
            if (placing) GUI.backgroundColor = new Color(1f, 0.6f, 0.3f);
            if (GUILayout.Button(placing ? "Arrêter le placement (Échap)" : "Placer dans la scène", GUILayout.Height(28)))
            {
                placing = !placing;
                SceneView.RepaintAll();
            }
            GUI.backgroundColor = prev;
        }

        if (TerrainModifierManager.Managers.Count == 0)
            EditorGUILayout.HelpBox("Aucun TerrainModifierManager : le terrain ne bougera pas. Il sera ajouté automatiquement au premier bâtiment posé.", MessageType.Warning);
    }

    private void DrawDropZone()
    {
        Rect r = GUILayoutUtility.GetRect(0, 44, GUILayout.ExpandWidth(true));
        GUI.Box(r, "Glisse des prefabs ici", EditorStyles.helpBox);

        Event e = Event.current;
        if (!r.Contains(e.mousePosition)) return;

        if (e.type == EventType.DragUpdated)
        {
            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            e.Use();
        }
        else if (e.type == EventType.DragPerform)
        {
            DragAndDrop.AcceptDrag();
            foreach (var o in DragAndDrop.objectReferences)
            {
                if (o is GameObject go && !entries.Exists(x => x.prefab == go))
                {
                    var en = new BuildingEntry { prefab = go, preset = GuessPreset(go.name) };
                    BuildingPresets.Defaults(en.preset, out en.margin, out en.blend);
                    entries.Add(en);
                }
            }
            if (selected < 0 && entries.Count > 0) selected = 0;
            e.Use();
        }
    }

    private static BuildingPreset GuessPreset(string n)
    {
        n = n.ToLowerInvariant();
        if (n.Contains("bus") || n.Contains("stop") || n.Contains("arret") || n.Contains("arrêt")) return BuildingPreset.BusStop;
        if (n.Contains("gas") || n.Contains("fuel") || n.Contains("station") || n.Contains("petrol")) return BuildingPreset.GasStation;
        if (n.Contains("shop") || n.Contains("store") || n.Contains("market")) return BuildingPreset.Shop;
        if (n.Contains("garage") || n.Contains("warehouse") || n.Contains("hangar")) return BuildingPreset.Garage;
        return BuildingPreset.House;
    }

    private void DrawEntry(int i)
    {
        var en = entries[i];
        bool isSel = i == selected;

        var rect = EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        if (isSel) EditorGUI.DrawRect(rect, new Color(0.25f, 0.5f, 1f, 0.15f));

        using (new EditorGUILayout.HorizontalScope())
        {
            Texture2D icon = en.prefab != null ? AssetPreview.GetAssetPreview(en.prefab) : null;
            Rect ir = GUILayoutUtility.GetRect(48, 48, GUILayout.Width(48));
            if (icon != null) GUI.DrawTexture(ir, icon, ScaleMode.ScaleToFit);
            else
            {
                GUI.Box(ir, GUIContent.none);
                if (en.prefab != null && AssetPreview.IsLoadingAssetPreview(en.prefab.GetEntityId())) Repaint();
            }
            if (Event.current.type == EventType.MouseDown && ir.Contains(Event.current.mousePosition))
            { selected = i; Event.current.Use(); }

            using (new EditorGUILayout.VerticalScope())
            {
                en.prefab = (GameObject)EditorGUILayout.ObjectField(en.prefab, typeof(GameObject), false);

                EditorGUI.BeginChangeCheck();
                var np = (BuildingPreset)EditorGUILayout.EnumPopup(en.preset);
                if (EditorGUI.EndChangeCheck())
                {
                    en.preset = np;
                    if (np != BuildingPreset.Custom) BuildingPresets.Defaults(np, out en.margin, out en.blend);
                }
            }

            if (GUILayout.Button("✕", GUILayout.Width(22), GUILayout.Height(22)))
            {
                entries.RemoveAt(i);
                if (selected >= entries.Count) selected = entries.Count - 1;
                GUIUtility.ExitGUI();
            }
        }

        EditorGUI.BeginChangeCheck();
        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUIUtility.labelWidth = 60;
            en.margin = Mathf.Max(0f, EditorGUILayout.FloatField(new GUIContent("Marge", "Sol plat autour du bâtiment (m)."), en.margin));
            en.blend = Mathf.Max(0f, EditorGUILayout.FloatField(new GUIContent("Raccord", "Largeur de la pente douce (m)."), en.blend));
            en.heightOffset = EditorGUILayout.FloatField(new GUIContent("Hauteur", "Décalage du sol (négatif = enterré)."), en.heightOffset);
            EditorGUIUtility.labelWidth = 0;
        }
        if (EditorGUI.EndChangeCheck() && en.preset != BuildingPreset.Custom) { /* valeurs modifiées à la main */ en.preset = BuildingPreset.Custom; }

        EditorGUILayout.EndVertical();

        if (Event.current.type == EventType.MouseDown && rect.Contains(Event.current.mousePosition))
        { selected = i; Repaint(); }
    }

    // ------------------------------------------------------------------
    //  Scene view
    // ------------------------------------------------------------------
    private void OnSceneGUI(SceneView sv)
    {
        if (!placing) return;
        if (selected < 0 || selected >= entries.Count || entries[selected].prefab == null) { placing = false; return; }

        var en = entries[selected];
        Event e = Event.current;
        int id = GUIUtility.GetControlID(FocusType.Passive);

        if (e.type == EventType.Layout) HandleUtility.AddDefaultControl(id);

        if (e.type == EventType.KeyDown)
        {
            if (e.keyCode == KeyCode.Escape) { placing = false; e.Use(); Repaint(); return; }
            if (e.keyCode == KeyCode.Q) { yaw -= rotationStep; e.Use(); }
            if (e.keyCode == KeyCode.E) { yaw += rotationStep; e.Use(); }
        }

        if (!TryGetGroundHit(e.mousePosition, out Vector3 hit)) return;

        // Mesure du prefab pour l'aperçu.
        TerrainFootprint.MeasureGeometry(en.prefab.transform, out var ctr, out var sz, out float floorOff);
        float yawNow = yaw;
        Quaternion rot = Quaternion.Euler(0f, yawNow, 0f);

        // Hauteur du sol : on échantillonne le terrain d'origine sous l'empreinte tournée.
        float gy = SampleGround(hit, rot, ctr, sz, groundMode);
        Vector3 pos = new Vector3(hit.x, gy - floorOff - en.heightOffset, hit.z);

        if (e.type == EventType.Repaint)
        {
            DrawGhost(en.prefab, pos, rot, sv);
            DrawFootprintPreview(pos, yawNow, ctr, sz, en, gy + en.heightOffset);
        }

        if (e.type == EventType.MouseMove || e.type == EventType.MouseDrag || e.type == EventType.KeyDown) sv.Repaint();

        if (e.type == EventType.MouseDown && e.button == 0 && !e.alt)
        {
            Place(en, pos, yawNow);
            e.Use();
            if (randomYaw) yaw = Mathf.Round(UnityEngine.Random.Range(0f, 360f) / rotationStep) * rotationStep;
            if (!keepPlacing) { placing = false; Repaint(); }
        }
    }

    private static bool TryGetGroundHit(Vector2 mouse, out Vector3 hit)
    {
        Ray ray = HandleUtility.GUIPointToWorldRay(mouse);
        if (Physics.Raycast(ray, out RaycastHit rh, 100000f) && rh.collider is TerrainCollider)
        { hit = rh.point; return true; }

        // Sans collider : on marche le long du rayon contre le terrain d'origine.
        const float step = 2f;
        for (float d = 0f; d < 6000f; d += step)
        {
            Vector3 p = ray.origin + ray.direction * d;
            if (TerrainModifierManager.TrySampleBaseHeight(p.x, p.z, out float gy) && p.y <= gy)
            { hit = new Vector3(p.x, gy, p.z); return true; }
        }
        hit = default;
        return false;
    }

    private static float SampleGround(Vector3 pos, Quaternion rot, Vector2 center, Vector2 size, FootprintGroundMode mode)
    {
        Vector3 right = rot * Vector3.right, fwd = rot * Vector3.forward;
        Vector3 c = pos + right * center.x + fwd * center.y;
        if (mode == FootprintGroundMode.Center)
            return TerrainModifierManager.TrySampleBaseHeight(c.x, c.z, out float cy) ? cy : pos.y;

        const int n = 7;
        float sum = 0f, lo = float.MaxValue, hi = float.MinValue; int count = 0;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                Vector3 p = c + right * Mathf.Lerp(-size.x * 0.5f, size.x * 0.5f, i / (n - 1f))
                              + fwd * Mathf.Lerp(-size.y * 0.5f, size.y * 0.5f, j / (n - 1f));
                if (!TerrainModifierManager.TrySampleBaseHeight(p.x, p.z, out float y)) continue;
                sum += y; lo = Mathf.Min(lo, y); hi = Mathf.Max(hi, y); count++;
            }
        if (count == 0) return pos.y;
        return mode == FootprintGroundMode.Average ? sum / count : mode == FootprintGroundMode.Highest ? hi : lo;
    }

    private static void DrawGhost(GameObject prefab, Vector3 pos, Quaternion rot, SceneView sv)
    {
        Matrix4x4 root = Matrix4x4.TRS(pos, rot, Vector3.one) * prefab.transform.worldToLocalMatrix;
        foreach (var mf in prefab.GetComponentsInChildren<MeshFilter>())
        {
            var mr = mf.GetComponent<MeshRenderer>();
            if (mf.sharedMesh == null || mr == null) continue;
            Matrix4x4 m = root * mf.transform.localToWorldMatrix;
            var mats = mr.sharedMaterials;
            for (int s = 0; s < mf.sharedMesh.subMeshCount; s++)
            {
                if (mats.Length == 0 || mats[Mathf.Min(s, mats.Length - 1)] == null) continue;
                Graphics.DrawMesh(mf.sharedMesh, m, mats[Mathf.Min(s, mats.Length - 1)], 0, sv.camera, s);
            }
        }
    }

    private static void DrawFootprintPreview(Vector3 pos, float yaw, Vector2 ctr, Vector2 size, BuildingEntry en, float targetY)
    {
        Quaternion rot = Quaternion.Euler(0f, yaw, 0f);
        Vector3 c = pos + rot * new Vector3(ctr.x, 0f, ctr.y);
        Vector3 hx = rot * Vector3.right * size.x * 0.5f, hz = rot * Vector3.forward * size.y * 0.5f;
        Handles.color = new Color(0.7f, 0.2f, 0.85f);
        DrawRect(c, hx, hz, targetY, 0f);
        Handles.color = new Color(0.7f, 0.2f, 0.85f, 0.5f);
        if (en.margin > 0.01f) DrawRect(c, hx, hz, targetY, en.margin);
        Handles.color = new Color(0.7f, 0.2f, 0.85f, 0.3f);
        if (en.blend > 0.01f) DrawRect(c, hx, hz, targetY, en.margin + en.blend);
    }

    private static void DrawRect(Vector3 c, Vector3 hx, Vector3 hz, float y, float expand)
    {
        Vector3 ex = hx.normalized * expand, ez = hz.normalized * expand;
        Vector3 a = c + hx + ex + hz + ez, b = c - hx - ex + hz + ez, d = c - hx - ex - hz - ez, f = c + hx + ex - hz - ez;
        a.y = b.y = d.y = f.y = y;
        Handles.DrawAAPolyLine(3f, a, b, d, f, a);
    }

    private void Place(BuildingEntry en, Vector3 pos, float yawDeg)
    {
        // Le terrain doit avoir un manager.
        Bounds b = new Bounds(pos, new Vector3(200f, 100000f, 200f));
        TerrainModifierManager.EnsureManagersForBounds(b);

        var inst = (GameObject)PrefabUtility.InstantiatePrefab(en.prefab);
        Undo.RegisterCreatedObjectUndo(inst, "Poser un bâtiment");

        if (!string.IsNullOrWhiteSpace(parentName))
        {
            var parent = GameObject.Find(parentName);
            if (parent == null) { parent = new GameObject(parentName); Undo.RegisterCreatedObjectUndo(parent, "Créer le parent"); }
            Undo.SetTransformParent(inst.transform, parent.transform, "Parent");
        }

        inst.transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, yawDeg, 0f));

        var fp = inst.GetComponent<TerrainFootprint>();
        if (fp == null) fp = Undo.AddComponent<TerrainFootprint>(inst);
        fp.FitToGeometry();
        fp.margin = en.margin;
        fp.blendDistance = en.blend;
        fp.heightOffset = en.heightOffset;
        // Recalage précis après mesure (le bas réel du mesh est connu maintenant).
        fp.PlaceOnGround(groundMode);

        Selection.activeGameObject = inst;
        TerrainModifierManager.RequestUpdate();
    }
}

public static class TerrainFootprintMenu
{
    [MenuItem("GameObject/Terrain Modifiers/Footprint (sur la sélection)", false, 31)]
    private static void AddToSelection()
    {
        foreach (var go in Selection.gameObjects)
        {
            if (go.GetComponent<TerrainFootprint>() != null) continue;
            var fp = Undo.AddComponent<TerrainFootprint>(go);
            fp.FitToGeometry();
            TerrainModifierManager.EnsureManagersForBounds(fp.WorldBounds());
            fp.PlaceOnGround(FootprintGroundMode.Average);
        }
    }

    [MenuItem("GameObject/Terrain Modifiers/Footprint (sur la sélection)", true)]
    private static bool AddToSelectionValidate() => Selection.gameObjects.Length > 0;
}
#endif
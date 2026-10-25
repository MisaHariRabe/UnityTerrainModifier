#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

public enum PaintToolKind { Brush, Lasso, Move, Rotate, Scale }

/// <summary>
/// Fenêtre "Paint Layers" (style Photoshop) : une pile de calques, chacun avec sa texture (TerrainLayer) et son masque peint.
/// Outils : Pinceau, Lasso libre (peindre / gommer), et Déplacer / Rotation / Échelle pour transformer le masque du calque.
/// </summary>
public class TerrainPaintWindow : EditorWindow
{
    [SerializeField] private TerrainPaintManager manager;
    [SerializeField] private int selected = -1;
    [SerializeField] private PaintToolKind tool = PaintToolKind.Brush;
    [SerializeField] private bool active;
    [SerializeField] private bool erase;
    [SerializeField] private float radius = 15f;
    [SerializeField] private float hardness = 0.5f;
    [SerializeField] private float strength = 0.6f;
    [SerializeField] private float feather = 3f;
    [SerializeField] private bool uniformScale = true;
    [SerializeField] private Object newSource;

    private Vector2 scroll;

    // état de la scène
    private readonly List<Vector2> lasso = new List<Vector2>();
    private readonly List<Vector3> lassoDraw = new List<Vector3>();
    private bool stroking;
    private Vector2 lastStamp;
    private bool hoverValid;
    private Vector3 hoverPoint;
    private bool dirtyAny;
    private Vector2 dirtyMin, dirtyMax;

    [MenuItem("Window/Terrain/Paint Layers")]
    [MenuItem("GameObject/Terrain Modifiers/Paint Layers…", false, 40)]
    public static void Open() => GetWindow<TerrainPaintWindow>("Paint Layers");

    private void OnEnable()
    {
        SceneView.duringSceneGui += OnSceneGUI;
        Undo.undoRedoPerformed += Repaint;
    }

    private void OnDisable()
    {
        SceneView.duringSceneGui -= OnSceneGUI;
        Undo.undoRedoPerformed -= Repaint;
        active = false;
    }

    // ------------------------------------------------------------------
    //  Fenêtre
    // ------------------------------------------------------------------
    private void OnGUI()
    {
        ResolveManager();

        EditorGUILayout.Space(4);
        EditorGUILayout.HelpBox(
            "Chaque calque = une texture (TerrainLayer) + un masque que tu peins. Les calques du haut recouvrent ceux du bas.\n" +
            "Tout est non destructif : cacher, réordonner, déplacer, tourner ou agrandir un calque ne touche jamais à la peinture d'origine du terrain.",
            MessageType.Info);

        DrawManagerHeader();
        if (manager == null) return;

        if (manager.TerrainChannelCount == 0)
            EditorGUILayout.HelpBox("Ce terrain n'a aucune texture de base. Ajoute-en une avec l'outil Paint Terrain d'Unity (Paint Texture), sinon les zones non peintes seront remplies par la texture du premier calque.", MessageType.Warning);

        scroll = EditorGUILayout.BeginScrollView(scroll);
        DrawLayerList();
        DrawAddLayer();
        DrawSelectedLayer();
        DrawToolPanel();
        EditorGUILayout.EndScrollView();
    }

    private void ResolveManager()
    {
        if (manager != null) return;
        manager = FindFirstObjectByType<TerrainPaintManager>();
    }

    private static Terrain TargetTerrain()
    {
        if (Selection.activeGameObject != null)
        {
            var t = Selection.activeGameObject.GetComponent<Terrain>();
            if (t != null) return t;
        }
        return Terrain.activeTerrain;
    }

    private void DrawManagerHeader()
    {
        manager = (TerrainPaintManager)EditorGUILayout.ObjectField("Terrain (manager)", manager, typeof(TerrainPaintManager), true);

        if (GUILayout.Button("Utiliser le terrain sélectionné / actif (ajoute le manager si besoin)"))
        {
            Terrain t = TargetTerrain();
            if (t == null) { Debug.LogWarning("[Paint Layers] Aucun terrain sélectionné ou actif."); return; }
            var m = t.GetComponent<TerrainPaintManager>();
            if (m == null) m = Undo.AddComponent<TerrainPaintManager>(t.gameObject);
            manager = m;
            selected = manager.layers.Count - 1;
            SceneView.RepaintAll();
        }
    }

    // ---------- Liste (le calque du haut est affiché en premier) ----------
    private void DrawLayerList()
    {
        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField($"Calques ({manager.layers.Count})", EditorStyles.boldLabel);
        if (manager.layers.Count == 0)
        {
            EditorGUILayout.HelpBox("Aucun calque. Ajoute une texture ci-dessous.", MessageType.None);
            return;
        }

        int moveUp = -1, moveDown = -1, remove = -1, dup = -1;
        for (int i = manager.layers.Count - 1; i >= 0; i--)
        {
            PaintLayer L = manager.layers[i];
            bool isSel = i == selected;

            Rect row = EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            if (isSel) EditorGUI.DrawRect(row, new Color(0.25f, 0.5f, 1f, 0.18f));

            EditorGUI.BeginChangeCheck();
            bool vis = EditorGUILayout.Toggle(new GUIContent("", "Afficher / cacher le calque"), L.visible, GUILayout.Width(16));
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(manager, vis ? "Afficher le calque" : "Cacher le calque");
                L.visible = vis;
                Commit();
            }

            Rect tr = GUILayoutUtility.GetRect(36, 36, GUILayout.Width(36), GUILayout.Height(36));
            Texture2D tex = L.terrainLayer != null ? L.terrainLayer.diffuseTexture : null;
            if (tex != null) GUI.DrawTexture(tr, tex, ScaleMode.ScaleToFit); else GUI.Box(tr, GUIContent.none);

            using (new EditorGUILayout.VerticalScope())
            {
                GUILayout.Label(L.name, isSel ? EditorStyles.boldLabel : EditorStyles.label);
                GUILayout.Label($"opacité {L.opacity:0.00}" + (L.terrainLayer == null ? "  •  sans texture" : ""), EditorStyles.miniLabel);
            }
            GUILayout.FlexibleSpace();

            if (GUILayout.Button(new GUIContent("▲", "Monter (passe au-dessus)"), GUILayout.Width(24))) moveUp = i;
            if (GUILayout.Button(new GUIContent("▼", "Descendre (passe en dessous)"), GUILayout.Width(24))) moveDown = i;
            if (GUILayout.Button(new GUIContent("⧉", "Dupliquer"), GUILayout.Width(24))) dup = i;
            if (GUILayout.Button(new GUIContent("✕", "Supprimer"), GUILayout.Width(24))) remove = i;
            EditorGUILayout.EndHorizontal();

            if (Event.current.type == EventType.MouseDown && row.Contains(Event.current.mousePosition))
            {
                selected = i;
                SceneView.RepaintAll();
                Repaint();
            }
        }

        bool structural = false;
        if (moveUp >= 0 && moveUp < manager.layers.Count - 1)
        {
            Undo.RecordObject(manager, "Monter le calque");
            Swap(moveUp, moveUp + 1);
            if (selected == moveUp) selected = moveUp + 1; else if (selected == moveUp + 1) selected = moveUp;
            structural = true;
        }
        else if (moveDown > 0)
        {
            Undo.RecordObject(manager, "Descendre le calque");
            Swap(moveDown, moveDown - 1);
            if (selected == moveDown) selected = moveDown - 1; else if (selected == moveDown - 1) selected = moveDown;
            structural = true;
        }
        else if (dup >= 0)
        {
            Undo.RegisterCompleteObjectUndo(manager, "Dupliquer le calque");
            manager.layers.Insert(dup + 1, manager.layers[dup].Clone());
            selected = dup + 1;
            structural = true;
        }
        else if (remove >= 0)
        {
            Undo.RegisterCompleteObjectUndo(manager, "Supprimer le calque");
            manager.layers.RemoveAt(remove);
            if (selected >= manager.layers.Count) selected = manager.layers.Count - 1;
            structural = true;
        }

        if (structural)
        {
            Commit();
            GUIUtility.ExitGUI();
        }
    }

    private void Swap(int a, int b)
    {
        (manager.layers[a], manager.layers[b]) = (manager.layers[b], manager.layers[a]);
    }

    // ---------- Ajouter un calque ----------
    private void DrawAddLayer()
    {
        EditorGUILayout.Space(2);
        newSource = EditorGUILayout.ObjectField(new GUIContent("Texture / TerrainLayer",
            "Glisse un TerrainLayer, ou une simple Texture2D (un TerrainLayer sera créé à côté d'elle)."), newSource, typeof(Object), false);

        bool ok = newSource is TerrainLayer || newSource is Texture2D;
        using (new EditorGUI.DisabledScope(!ok))
        {
            if (GUILayout.Button("+ Nouveau calque", GUILayout.Height(24)))
            {
                TerrainLayer tl = newSource as TerrainLayer;
                if (tl == null) tl = CreateTerrainLayerFrom((Texture2D)newSource);

                Undo.RegisterCompleteObjectUndo(manager, "Ajouter un calque");
                PaintLayer l = manager.AddLayer(tl, newSource.name);
                if (l != null) selected = manager.layers.Count - 1;
                Commit();
            }
        }
    }

    private static TerrainLayer CreateTerrainLayerFrom(Texture2D tex)
    {
        string path = AssetDatabase.GetAssetPath(tex);
        string dir = string.IsNullOrEmpty(path) ? "Assets" : Path.GetDirectoryName(path).Replace('\\', '/');
        var tl = new TerrainLayer { diffuseTexture = tex, tileSize = new Vector2(15f, 15f) };
        string assetPath = AssetDatabase.GenerateUniqueAssetPath($"{dir}/{tex.name}_Layer.terrainlayer");
        AssetDatabase.CreateAsset(tl, assetPath);
        AssetDatabase.SaveAssets();
        return tl;
    }

    // ---------- Réglages du calque sélectionné ----------
    private void DrawSelectedLayer()
    {
        if (selected < 0 || selected >= manager.layers.Count) return;
        PaintLayer L = manager.layers[selected];

        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField($"Calque : {L.name}", EditorStyles.boldLabel);

        EditorGUI.BeginChangeCheck();
        string nm = EditorGUILayout.TextField("Nom", L.name);
        TerrainLayer tl = (TerrainLayer)EditorGUILayout.ObjectField(
            new GUIContent("Texture (TerrainLayer)", "La texture elle-même (tuilage, normal map...) se règle dans l'asset TerrainLayer, partagé par tout ce qui l'utilise."),
            L.terrainLayer, typeof(TerrainLayer), false);
        float op = EditorGUILayout.Slider("Opacité", L.opacity, 0f, 1f);

        EditorGUILayout.LabelField("Transformation (déplace la projection peinte)", EditorStyles.miniBoldLabel);
        Vector2 off = EditorGUILayout.Vector2Field(new GUIContent("Décalage X / Z (m)"), L.offset);
        float rot = EditorGUILayout.Slider("Rotation (°)", L.rotation, -180f, 180f);
        uniformScale = EditorGUILayout.Toggle("Échelle uniforme", uniformScale);
        Vector2 sc = L.scale;
        if (uniformScale)
        {
            float u = EditorGUILayout.Slider("Échelle", sc.x, 0.1f, 5f);
            sc = new Vector2(u, u);
        }
        else
        {
            sc.x = EditorGUILayout.Slider("Échelle X", sc.x, 0.1f, 5f);
            sc.y = EditorGUILayout.Slider("Échelle Z", sc.y, 0.1f, 5f);
        }
        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(manager, "Modifier le calque");
            L.name = nm; L.terrainLayer = tl; L.opacity = op; L.offset = off; L.rotation = rot; L.scale = sc;
            Commit();
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Tout remplir"))
            {
                Undo.RegisterCompleteObjectUndo(manager, "Remplir le calque");
                L.SetAll(255);
                Commit();
            }
            if (GUILayout.Button("Vider le masque"))
            {
                Undo.RegisterCompleteObjectUndo(manager, "Vider le calque");
                L.SetAll(0);
                Commit();
            }
            if (GUILayout.Button("Reset transfo"))
            {
                Undo.RecordObject(manager, "Réinitialiser la transformation");
                L.offset = Vector2.zero; L.rotation = 0f; L.scale = Vector2.one;
                Commit();
            }
        }
    }

    // ---------- Outils ----------
    private void DrawToolPanel()
    {
        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Outils", EditorStyles.boldLabel);

        EditorGUI.BeginChangeCheck();
        int t = GUILayout.Toolbar((int)tool, new[] { "Pinceau", "Lasso", "Déplacer", "Rotation", "Échelle" });
        if (EditorGUI.EndChangeCheck()) { tool = (PaintToolKind)t; ResetStroke(); SceneView.RepaintAll(); }

        if (tool == PaintToolKind.Brush || tool == PaintToolKind.Lasso)
        {
            erase = GUILayout.Toolbar(erase ? 1 : 0, new[] { "Peindre", "Gommer" }) == 1;
            if (tool == PaintToolKind.Brush)
            {
                radius = EditorGUILayout.Slider(new GUIContent("Rayon (m)", "Shift + molette dans la Scene."), radius, 1f, 200f);
                hardness = EditorGUILayout.Slider(new GUIContent("Dureté", "1 = bord net, 0 = bord très doux."), hardness, 0f, 1f);
            }
            else
            {
                feather = EditorGUILayout.Slider(new GUIContent("Adoucissement du bord (m)", "Bord flou du contour."), feather, 0f, 40f);
            }
            strength = EditorGUILayout.Slider(new GUIContent("Intensité", "Pinceau : quantité déposée à chaque passage. Lasso : opacité du remplissage."), strength, 0.05f, 1f);
        }

        EditorGUILayout.HelpBox(tool switch
        {
            PaintToolKind.Brush => "Glisse sur le terrain pour peindre le masque du calque sélectionné. Shift + molette : rayon. Échap : quitter.",
            PaintToolKind.Lasso => "Dessine un contour à main levée : au relâchement, l'intérieur est peint (ou gommé). Échap : quitter.",
            PaintToolKind.Move => "Glisse les flèches pour décaler le calque sur X / Z : tout ce que tu as peint suit.",
            PaintToolKind.Rotate => "Tourne le disque pour faire pivoter le calque autour de son centre.",
            _ => "Tire sur les poignées pour agrandir / réduire le calque (cube central = uniforme)."
        }, MessageType.None);

        var prev = GUI.backgroundColor;
        if (active) GUI.backgroundColor = new Color(1f, 0.6f, 0.3f);
        using (new EditorGUI.DisabledScope(selected < 0 || selected >= manager.layers.Count))
        {
            if (GUILayout.Button(active ? "Désactiver l'outil (Échap)" : "Activer l'outil dans la Scene", GUILayout.Height(28)))
            {
                active = !active;
                ResetStroke();
                SceneView.RepaintAll();
            }
        }
        GUI.backgroundColor = prev;
    }

    private void Commit()
    {
        EditorUtility.SetDirty(manager);
        manager.MarkDirty();
        SceneView.RepaintAll();
        Repaint();
    }

    private void ResetStroke()
    {
        stroking = false;
        lasso.Clear();
        lassoDraw.Clear();
        dirtyAny = false;
    }

    // ------------------------------------------------------------------
    //  Scene
    // ------------------------------------------------------------------
    private void OnSceneGUI(SceneView sv)
    {
        if (!active || manager == null || selected < 0 || selected >= manager.layers.Count) return;
        PaintLayer L = manager.layers[selected];

        DrawLayerBounds(L);

        if (tool == PaintToolKind.Brush || tool == PaintToolKind.Lasso) HandlePaint(sv, L);
        else HandleTransform(L);
    }

    private void DrawLayerBounds(PaintLayer L)
    {
        if (Event.current.type != EventType.Repaint) return;

        var f = L.GetFrame(manager.Origin, manager.Size);
        float last = f.res - 1;
        Vector2[] c =
        {
            PaintLayer.PixelToWorld(in f, 0, 0), PaintLayer.PixelToWorld(in f, last, 0),
            PaintLayer.PixelToWorld(in f, last, last), PaintLayer.PixelToWorld(in f, 0, last)
        };

        Handles.color = new Color(0.3f, 0.9f, 1f, 0.8f);
        for (int e = 0; e < 4; e++)
        {
            Vector2 a = c[e], b = c[(e + 1) % 4];
            const int seg = 40;
            var pts = new Vector3[seg + 1];
            for (int i = 0; i <= seg; i++)
            {
                Vector2 p = Vector2.Lerp(a, b, i / (float)seg);
                pts[i] = new Vector3(p.x, ModifierMath.GroundY(p.x, p.y, manager.Origin.y) + 0.3f, p.y);
            }
            Handles.DrawAAPolyLine(3f, pts);
        }
    }

    // ---------- Pinceau et lasso ----------
    private void HandlePaint(SceneView sv, PaintLayer L)
    {
        Event e = Event.current;
        int id = GUIUtility.GetControlID(FocusType.Passive);
        bool isLasso = tool == PaintToolKind.Lasso;
        Color col = erase ? new Color(1f, 0.3f, 0.25f) : new Color(0.3f, 1f, 0.4f);

        if (e.type == EventType.MouseMove || e.type == EventType.MouseDrag || e.type == EventType.MouseDown)
        {
            hoverValid = TerrainRay.TryHit(e.mousePosition, out hoverPoint) && manager.ContainsXZ(hoverPoint);
            if (e.type == EventType.MouseMove) sv.Repaint();
        }

        switch (e.GetTypeForControl(id))
        {
            case EventType.Layout:
                HandleUtility.AddDefaultControl(id);
                break;

            case EventType.MouseDown:
                if (e.button == 0 && !e.alt && hoverValid && HandleUtility.nearestControl == id)
                {
                    Undo.RegisterCompleteObjectUndo(manager, isLasso ? "Lasso" : (erase ? "Gommer" : "Peindre"));
                    stroking = true;
                    GUIUtility.hotControl = id;
                    var p = new Vector2(hoverPoint.x, hoverPoint.z);
                    if (isLasso)
                    {
                        lasso.Clear(); lassoDraw.Clear();
                        lasso.Add(p); lassoDraw.Add(hoverPoint);
                    }
                    else
                    {
                        lastStamp = p;
                        dirtyAny = false;
                        Stamp(L, p);
                        FlushDirty();
                    }
                    e.Use();
                }
                break;

            case EventType.MouseDrag:
                if (stroking && GUIUtility.hotControl == id)
                {
                    if (hoverValid)
                    {
                        var p = new Vector2(hoverPoint.x, hoverPoint.z);
                        if (isLasso)
                        {
                            if (Vector2.Distance(lasso[lasso.Count - 1], p) >= 0.75f) { lasso.Add(p); lassoDraw.Add(hoverPoint); }
                        }
                        else
                        {
                            // Tampons régulièrement espacés le long du trait.
                            float spacing = Mathf.Max(0.5f, radius * 0.25f);
                            float dist = Vector2.Distance(lastStamp, p);
                            if (dist >= spacing)
                            {
                                Vector2 dir = (p - lastStamp) / dist;
                                for (float d = spacing; d <= dist; d += spacing)
                                {
                                    lastStamp += dir * spacing;
                                    Stamp(L, lastStamp);
                                }
                                FlushDirty();
                            }
                        }
                    }
                    e.Use();
                    SceneView.RepaintAll();
                }
                break;

            case EventType.MouseUp:
                if (stroking && GUIUtility.hotControl == id)
                {
                    GUIUtility.hotControl = 0;
                    stroking = false;
                    if (isLasso && e.button == 0 && lasso.Count >= 3)
                    {
                        var f = L.GetFrame(manager.Origin, manager.Size);
                        L.FillPolygon(in f, lasso, feather, strength, erase, out Vector2 mn, out Vector2 mx);
                        manager.RecomposeWorldRect(mn, mx);
                    }
                    lasso.Clear(); lassoDraw.Clear();
                    EditorUtility.SetDirty(manager);
                    e.Use();
                    SceneView.RepaintAll();
                    Repaint();
                }
                break;

            case EventType.KeyDown:
                if (e.keyCode == KeyCode.Escape)
                {
                    if (stroking) { GUIUtility.hotControl = 0; ResetStroke(); }
                    else active = false;
                    e.Use();
                    SceneView.RepaintAll();
                    Repaint();
                }
                break;

            case EventType.ScrollWheel:
                if (e.shift && !isLasso)
                {
                    radius = Mathf.Clamp(radius * (e.delta.y > 0 ? 0.9f : 1.1f), 1f, 200f);
                    e.Use();
                    Repaint();
                    SceneView.RepaintAll();
                }
                break;

            case EventType.Repaint:
                DrawPaintPreview(isLasso, col);
                break;
        }
    }

    private void Stamp(PaintLayer L, Vector2 p)
    {
        var f = L.GetFrame(manager.Origin, manager.Size);
        // 0.35 : chaque tampon ne dépose qu'une partie, les tampons se recouvrent le long du trait.
        L.Stamp(in f, p, radius, hardness, strength * 0.35f, erase, out Vector2 mn, out Vector2 mx);
        if (!dirtyAny) { dirtyMin = mn; dirtyMax = mx; dirtyAny = true; }
        else { dirtyMin = Vector2.Min(dirtyMin, mn); dirtyMax = Vector2.Max(dirtyMax, mx); }
    }

    private void FlushDirty()
    {
        if (!dirtyAny) return;
        manager.RecomposeWorldRect(dirtyMin, dirtyMax);
        dirtyAny = false;
    }

    private void DrawPaintPreview(bool isLasso, Color col)
    {
        if (!isLasso && hoverValid)
        {
            Vector3 p = hoverPoint + Vector3.up * 0.25f;
            Handles.color = new Color(col.r, col.g, col.b, 0.9f);
            Handles.DrawWireDisc(p, Vector3.up, radius);
            Handles.color = new Color(col.r, col.g, col.b, 0.35f);
            Handles.DrawWireDisc(p, Vector3.up, radius * hardness);
        }

        if (isLasso && lassoDraw.Count >= 2)
        {
            var pts = new Vector3[lassoDraw.Count];
            for (int i = 0; i < pts.Length; i++) pts[i] = lassoDraw[i] + Vector3.up * 0.3f;
            Handles.color = col;
            Handles.DrawAAPolyLine(4f, pts);
            Handles.color = new Color(col.r, col.g, col.b, 0.6f);
            Handles.DrawDottedLine(pts[pts.Length - 1], pts[0], 5f);
        }
    }

    // ---------- Déplacer / tourner / agrandir ----------
    private void HandleTransform(PaintLayer L)
    {
        var f = L.GetFrame(manager.Origin, manager.Size);
        Vector3 pos = new Vector3(f.pivot.x, ModifierMath.GroundY(f.pivot.x, f.pivot.y, manager.Origin.y) + 0.3f, f.pivot.y);
        float size = HandleUtility.GetHandleSize(pos);

        if (tool == PaintToolKind.Move)
        {
            EditorGUI.BeginChangeCheck();
            Vector3 np = Handles.PositionHandle(pos, Quaternion.identity);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(manager, "Déplacer le calque");
                L.offset += new Vector2(np.x - pos.x, np.z - pos.z);
                Commit();
            }
        }
        else if (tool == PaintToolKind.Rotate)
        {
            // Rotation du calque > 0 : de +X vers +Z ; la rotation Y d'Unity va dans l'autre sens.
            float yaw = -L.rotation;
            EditorGUI.BeginChangeCheck();
            Quaternion q = Handles.Disc(Quaternion.Euler(0f, yaw, 0f), pos, Vector3.up, size * 1.2f, false, 0f);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(manager, "Tourner le calque");
                float dy = Mathf.DeltaAngle(yaw, q.eulerAngles.y);
                L.rotation = Mathf.Repeat(L.rotation - dy + 180f, 360f) - 180f;
                Commit();
            }
        }
        else
        {
            EditorGUI.BeginChangeCheck();
            Vector3 ns = Handles.ScaleHandle(new Vector3(L.scale.x, 1f, L.scale.y), pos, Quaternion.Euler(0f, -L.rotation, 0f), size * 1.2f);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(manager, "Redimensionner le calque");
                L.scale = new Vector2(Mathf.Clamp(ns.x, 0.05f, 10f), Mathf.Clamp(ns.z, 0.05f, 10f));
                Commit();
            }
        }

        Handles.Label(pos + Vector3.up * size * 0.6f,
            $"{L.name}\nX/Z {L.offset.x:0.#} / {L.offset.y:0.#} m   rot {L.rotation:0.#}°   éch. {L.scale.x:0.00} × {L.scale.y:0.00}");

        if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape)
        {
            active = false;
            Event.current.Use();
            Repaint();
        }
    }
}

[CustomEditor(typeof(TerrainPaintManager))]
public class TerrainPaintManagerEditor : Editor
{
    public override void OnInspectorGUI()
    {
        var m = (TerrainPaintManager)target;
        serializedObject.Update();

        EditorGUILayout.HelpBox($"{m.layers.Count} calque(s)  •  base {m.BaseMemoryMB:0.0} Mo  •  {m.TerrainChannelCount} texture(s) sur le terrain", MessageType.None);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("autoUpdate"));
        serializedObject.ApplyModifiedProperties();

        EditorGUILayout.Space(4);
        if (GUILayout.Button("Ouvrir la fenêtre Paint Layers", GUILayout.Height(26))) TerrainPaintWindow.Open();

        EditorGUILayout.Space(4);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button(new GUIContent("Appliquer", "Recompose le terrain maintenant."))) m.RecomposeAll();
            if (GUILayout.Button(new GUIContent("Restaurer la base", "Affiche la peinture d'origine sans calques (jusqu'au prochain changement)."))) m.RestoreBase();
        }
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button(new GUIContent("Recapturer la base",
                    "À utiliser APRÈS avoir repeint le terrain avec les outils Unity : le terrain actuel devient la nouvelle base.\nFais d'abord « Restaurer la base » pour ne pas y inclure tes calques.")))
                m.CaptureBase();

            if (GUILayout.Button(new GUIContent("Figer (Bake)", "Intègre tous les calques dans la base puis les supprime.")))
            {
                if (EditorUtility.DisplayDialog("Figer la peinture",
                        "Le résultat actuel devient la nouvelle peinture de base et tous les calques seront supprimés.\n\nContinuer ?", "Figer", "Annuler"))
                {
                    Undo.RegisterCompleteObjectUndo(m, "Figer la peinture");
                    m.Bake();
                    EditorUtility.SetDirty(m);
                }
            }
        }

        EditorGUILayout.HelpBox("Pour repeindre à la main avec les outils Unity : « Restaurer la base », peins, puis « Recapturer la base ». Sinon ta peinture serait écrasée au prochain recalcul.", MessageType.None);
    }
}
#endif
#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Splines;
using Unity.Mathematics;

public enum SplinePaintMode { Edit, Brush, Lasso }

/// <summary>Rayon souris → terrain. Utilise le collider du terrain, sinon marche le long du rayon contre la heightmap d'origine.</summary>
public static class TerrainRay
{
    public static bool TryHit(Vector2 guiMouse, out Vector3 hit)
    {
        Ray ray = HandleUtility.GUIPointToWorldRay(guiMouse);

        // Le premier TerrainCollider touché (on ignore routes, bâtiments, etc.).
        var hits = Physics.RaycastAll(ray, 100000f);
        float best = float.MaxValue; bool found = false; hit = default;
        foreach (var h in hits)
        {
            if (!(h.collider is TerrainCollider) || h.distance >= best) continue;
            best = h.distance; hit = h.point; found = true;
        }
        if (found) return true;

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
}

/// <summary>
/// Peinture de splines dans la scène.
/// Pinceau : tu traces un trait sur le terrain → spline OUVERTE en mode Path (route, rivière, crête).
/// Lasso   : tu traces un contour → spline FERMÉE en mode Shape (plateforme, lac, zone aplatie).
/// </summary>
[InitializeOnLoad]
public static class SplinePaintTool
{
    public static SplinePaintMode Mode = SplinePaintMode.Edit;

    private static float width = 8f;
    private static float peak = 0f;
    private static float tolerance = 1.5f;
    private static bool smoothContour = true;
    private static bool newObjectPerStroke = true;

    private static readonly List<Vector3> stroke = new List<Vector3>();
    private static bool painting;
    private static bool hoverValid;
    private static Vector3 hoverPoint;

    static SplinePaintTool()
    {
        // Changer de sélection quitte le mode peinture (évite de peindre sur un autre objet par surprise).
        Selection.selectionChanged += () => { Mode = SplinePaintMode.Edit; painting = false; stroke.Clear(); };
    }

    // ------------------------------------------------------------------ UI partagée (inspecteur + scène)
    public static void DrawInspector(TerrainSplineProfileNode node)
    {
        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Peinture", EditorStyles.boldLabel);
        DrawPanel(node);
    }

    public static void DrawScenePanel(TerrainSplineProfileNode node)
    {
        Handles.BeginGUI();
        GUILayout.BeginArea(new Rect(10, 130, 270, Mode == SplinePaintMode.Edit ? 52 : 230), EditorStyles.helpBox);
        DrawPanel(node);
        GUILayout.EndArea();
        Handles.EndGUI();
    }

    private static void DrawPanel(TerrainSplineProfileNode node)
    {
        EditorGUI.BeginChangeCheck();
        int m = GUILayout.Toolbar((int)Mode, new[] { "Édition", "Pinceau (Path)", "Lasso (Shape)" });
        if (EditorGUI.EndChangeCheck())
        {
            Mode = (SplinePaintMode)m;
            painting = false; stroke.Clear();
            SceneView.RepaintAll();
        }
        if (Mode == SplinePaintMode.Edit) return;

        bool isRoad = node.GetComponent<RoadTools.Road>() != null;

        width = EditorGUILayout.Slider(new GUIContent("Largeur (rayon)", "Largeur de base de chaque point (Shift + molette dans la scène)."), width, 1f, 80f);
        peak = EditorGUILayout.FloatField(new GUIContent("Hauteur du sommet (m)", "Décalage vertical du profil de chaque point."), peak);
        tolerance = EditorGUILayout.Slider(new GUIContent("Simplification (m)", "Plus c'est grand, moins il y a de nœuds sur le tracé."), tolerance, 0.3f, 8f);
        smoothContour = EditorGUILayout.Toggle(new GUIContent("Courbes lissées", "Décoché : segments droits entre les nœuds."), smoothContour);

        using (new EditorGUI.DisabledScope(isRoad))
        {
            bool v = EditorGUILayout.Toggle(new GUIContent("Un objet par tracé", "Coché : chaque tracé crée un nouvel objet avec les réglages de celui-ci. Décoché : le tracé remplace la spline de cet objet."),
                                            isRoad || newObjectPerStroke);
            if (!isRoad) newObjectPerStroke = v;
        }
        if (isRoad)
            EditorGUILayout.HelpBox("Ce node appartient à une route : chaque tracé crée un nouvel objet.", MessageType.None);

        EditorGUILayout.HelpBox(Mode == SplinePaintMode.Brush
            ? "Glisse sur le terrain pour tracer un chemin. Shift+molette : largeur. Échap : quitter."
            : "Dessine un contour autour de la zone : il est fermé automatiquement en Shape. Échap : quitter.", MessageType.None);
    }

    // ------------------------------------------------------------------ Scène
    /// <summary>Retourne true quand l'outil est actif (l'éditeur n'affiche alors pas ses poignées habituelles).</summary>
    public static bool HandleScene(TerrainSplineProfileNode node)
    {
        if (Mode == SplinePaintMode.Edit) return false;

        Event e = Event.current;
        int id = GUIUtility.GetControlID(FocusType.Passive);
        bool lasso = Mode == SplinePaintMode.Lasso;

        if (e.type == EventType.MouseMove || e.type == EventType.MouseDrag || e.type == EventType.MouseDown)
            hoverValid = TerrainRay.TryHit(e.mousePosition, out hoverPoint);

        switch (e.GetTypeForControl(id))
        {
            case EventType.Layout:
                HandleUtility.AddDefaultControl(id);
                break;

            case EventType.MouseDown:
                if (e.button == 0 && !e.alt && hoverValid && HandleUtility.nearestControl == id)
                {
                    stroke.Clear();
                    stroke.Add(hoverPoint);
                    painting = true;
                    GUIUtility.hotControl = id;
                    e.Use();
                }
                break;

            case EventType.MouseDrag:
                if (painting && GUIUtility.hotControl == id)
                {
                    if (hoverValid && HorizontalDistance(stroke[stroke.Count - 1], hoverPoint) >= 0.75f)
                        stroke.Add(hoverPoint);
                    e.Use();
                    SceneView.RepaintAll();
                }
                break;

            case EventType.MouseUp:
                if (painting && GUIUtility.hotControl == id)
                {
                    GUIUtility.hotControl = 0;
                    painting = false;
                    if (e.button == 0) Commit(node, lasso);
                    stroke.Clear();
                    e.Use();
                    SceneView.RepaintAll();
                }
                break;

            case EventType.KeyDown:
                if (e.keyCode == KeyCode.Escape)
                {
                    if (painting) { GUIUtility.hotControl = 0; painting = false; stroke.Clear(); }
                    else Mode = SplinePaintMode.Edit;
                    e.Use();
                    SceneView.RepaintAll();
                }
                break;

            case EventType.ScrollWheel:
                if (e.shift)
                {
                    width = Mathf.Clamp(width * (e.delta.y > 0 ? 0.9f : 1.1f), 1f, 80f);
                    e.Use();
                    SceneView.RepaintAll();
                }
                break;

            case EventType.Repaint:
                DrawPreview(node, lasso);
                break;
        }

        if (e.type == EventType.MouseMove) SceneView.RepaintAll();
        return true;
    }

    private static void DrawPreview(TerrainSplineProfileNode node, bool lasso)
    {
        Color col = ModifierMath.ColorOf(node.operation);

        if (hoverValid)
        {
            Handles.color = new Color(col.r, col.g, col.b, 0.9f);
            Handles.DrawWireDisc(hoverPoint + Vector3.up * 0.2f, Vector3.up, width);
            Handles.color = new Color(col.r, col.g, col.b, 0.35f);
            Handles.DrawWireDisc(hoverPoint + Vector3.up * 0.2f, Vector3.up, 0.3f);
        }

        if (stroke.Count < 2) return;
        var pts = new Vector3[stroke.Count];
        for (int i = 0; i < pts.Length; i++) pts[i] = stroke[i] + Vector3.up * 0.3f;

        Handles.color = col;
        Handles.DrawAAPolyLine(4f, pts);
        if (lasso)
        {
            Handles.color = new Color(col.r, col.g, col.b, 0.6f);
            Handles.DrawDottedLine(pts[pts.Length - 1], pts[0], 5f);
        }
    }

    // ------------------------------------------------------------------ Création de la spline
    private static void Commit(TerrainSplineProfileNode source, bool lasso)
    {
        List<Vector3> pts = Simplify(stroke, tolerance);

        if (lasso)
        {
            // Le dernier point revient près du premier : on évite le doublon avant de fermer.
            if (pts.Count > 3 && HorizontalDistance(pts[0], pts[pts.Count - 1]) < Mathf.Max(tolerance, 1f))
                pts.RemoveAt(pts.Count - 1);
            if (pts.Count < 3) { Debug.Log("[Spline Paint] Contour trop petit pour un Shape."); return; }
        }
        else if (pts.Count < 2) return;

        bool isRoad = source.GetComponent<RoadTools.Road>() != null;
        bool makeNew = newObjectPerStroke || isRoad;

        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName(lasso ? "Lasso : créer un Shape" : "Pinceau : créer un Path");
        int group = Undo.GetCurrentGroup();

        TerrainSplineProfileNode dst;
        if (makeNew)
        {
            var go = new GameObject(lasso ? "Terrain Spline Shape" : "Terrain Spline Path");
            Undo.RegisterCreatedObjectUndo(go, "Créer la spline");
            go.transform.SetParent(source.transform.parent, false);
            go.transform.SetPositionAndRotation(new Vector3(pts[0].x, 0f, pts[0].z), Quaternion.identity);

            dst = Undo.AddComponent<TerrainSplineProfileNode>(go);   // ajoute aussi le SplineContainer
            EditorUtility.CopySerialized(source, dst);               // mêmes réglages (opération, force, lissage, couche...)
        }
        else
        {
            dst = source;
            Undo.RecordObject(dst, "Peindre la spline");
        }

        dst.mode = lasso ? SplineModifierMode.Shape : SplineModifierMode.Path;

        var container = dst.GetSplineContainer();
        if (!makeNew) Undo.RecordObject(container, "Peindre la spline");
        Spline sp = container.Spline;
        sp.Clear();
        foreach (var p in pts)
        {
            Vector3 local = dst.transform.InverseTransformPoint(p);
            sp.Add(new BezierKnot(new float3(local.x, local.y, local.z)), smoothContour ? TangentMode.AutoSmooth : TangentMode.Linear);
        }
        sp.Closed = lasso;

        // Profil de chaque point : largeur et hauteur du pinceau, bord repris du modèle.
        dst.SyncHandlesWithSpline();
        PointProfileHandle template = source.pointHandles.Count > 0 ? source.pointHandles[0] : new PointProfileHandle();
        foreach (var h in dst.pointHandles)
        {
            h.baseWidth = width;
            h.peakOffsetY = peak;
            h.edgePositionT = template.edgePositionT;
            h.edgeOffsetY = template.edgeOffsetY;
        }

        EditorUtility.SetDirty(dst);
        EditorUtility.SetDirty(container);

        // Un manager doit couvrir la zone peinte.
        var b = new Bounds(pts[0], Vector3.zero);
        foreach (var p in pts) b.Encapsulate(p);
        b.Expand(new Vector3(width * 2f, 100000f, width * 2f));
        TerrainModifierManager.EnsureManagersForBounds(b);
        TerrainModifierManager.RequestUpdate();

        Undo.CollapseUndoOperations(group);
        if (makeNew) EditorGUIUtility.PingObject(dst.gameObject);
    }

    // ------------------------------------------------------------------ Simplification (Ramer-Douglas-Peucker, plan XZ)
    private static List<Vector3> Simplify(List<Vector3> pts, float tol)
    {
        int n = pts.Count;
        if (n < 3) return new List<Vector3>(pts);

        var keep = new bool[n];
        keep[0] = keep[n - 1] = true;
        var stack = new Stack<(int a, int b)>();
        stack.Push((0, n - 1));

        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            float maxD = 0f; int idx = -1;
            for (int i = a + 1; i < b; i++)
            {
                float d = DistanceToSegment(pts[i], pts[a], pts[b]);
                if (d > maxD) { maxD = d; idx = i; }
            }
            if (idx >= 0 && maxD > tol)
            {
                keep[idx] = true;
                stack.Push((a, idx));
                stack.Push((idx, b));
            }
        }

        var result = new List<Vector3>();
        for (int i = 0; i < n; i++) if (keep[i]) result.Add(pts[i]);
        return result;
    }

    private static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        Vector2 P = new Vector2(p.x, p.z), A = new Vector2(a.x, a.z), B = new Vector2(b.x, b.z);
        Vector2 ab = B - A;
        float len2 = ab.sqrMagnitude;
        if (len2 < 1e-6f) return Vector2.Distance(P, A);
        float t = Mathf.Clamp01(Vector2.Dot(P - A, ab) / len2);
        return Vector2.Distance(P, A + ab * t);
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b) =>
        Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));
}
#endif

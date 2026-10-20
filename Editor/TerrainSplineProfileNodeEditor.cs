#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.Splines;
using Unity.Mathematics;

[CustomEditor(typeof(TerrainSplineProfileNode))]
public class TerrainSplineProfileNodeEditor : Editor
{
    private int selected = 0;

    // ------------------------------------------------------------------ Inspecteur
    public override void OnInspectorGUI()
    {
        var node = (TerrainSplineProfileNode)target;
        node.SyncHandlesWithSpline();
        Color col = ModifierMath.ColorOf(node.operation);

        // En-tête
        Rect header = GUILayoutUtility.GetRect(0, 22, GUILayout.ExpandWidth(true));
        EditorGUI.DrawRect(header, new Color(col.r, col.g, col.b, 0.25f));
        EditorGUI.DrawRect(new Rect(header.x, header.y, 4, header.height), col);
        GUI.Label(new Rect(header.x + 10, header.y + 2, header.width - 12, 18),
                  $"Spline  •  {node.mode}  •  {node.operation}  •  {node.pointHandles.Count} points", EditorStyles.boldLabel);

        if (TerrainModifierManager.Managers.Count == 0)
        {
            EditorGUILayout.HelpBox("Aucun TerrainModifierManager dans la scène : ce nœud n'aura aucun effet.", MessageType.Warning);
            if (Terrain.activeTerrain != null && GUILayout.Button("Ajouter un Manager au terrain actif"))
                Undo.AddComponent<TerrainModifierManager>(Terrain.activeTerrain.gameObject);
        }

        // Peinture : pinceau (Path) et lasso (Shape)
        SplinePaintTool.DrawInspector(node);

        // Propriétés générales
        EditorGUI.BeginChangeCheck();
        DrawDefaultInspectorExceptHandles();
        EditorGUILayout.HelpBox(ModifierMath.Describe(node.operation), MessageType.None);
        if (node.mode == SplineModifierMode.Shape && node.GetSplineContainer() != null && node.GetSplineContainer().Spline.Count < 3)
            EditorGUILayout.HelpBox("Le mode Shape nécessite au moins 3 nœuds.", MessageType.Warning);
        if (EditorGUI.EndChangeCheck()) TerrainModifierManager.RequestUpdate();

        // ---- Point sélectionné ----
        int count = node.pointHandles.Count;
        if (count == 0)
        {
            EditorGUILayout.HelpBox("Ajoutez des nœuds à la spline pour commencer.", MessageType.Info);
            return;
        }
        selected = Mathf.Clamp(selected, 0, count - 1);

        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Profil du point", EditorStyles.boldLabel);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("◀", GUILayout.Width(28))) { selected = (selected - 1 + count) % count; SceneView.RepaintAll(); }
            selected = EditorGUILayout.IntSlider(selected, 0, count - 1);
            if (GUILayout.Button("▶", GUILayout.Width(28))) { selected = (selected + 1) % count; SceneView.RepaintAll(); }
        }

        PointProfileHandle h = node.pointHandles[selected];
        EditorGUI.BeginChangeCheck();
        float peak = EditorGUILayout.FloatField(new GUIContent("Hauteur du sommet (m)", "Décalage vertical au centre."), h.peakOffsetY);
        float width = EditorGUILayout.FloatField(new GUIContent("Largeur de base (m)", "Rayon d'influence."), h.baseWidth);
        float edgeT = EditorGUILayout.Slider(new GUIContent("Position du bord", "Position relative du point intermédiaire."), h.edgePositionT, 0f, 1f);
        float edgeY = EditorGUILayout.FloatField(new GUIContent("Hauteur du bord (m)", "Décalage vertical du point intermédiaire."), h.edgeOffsetY);
        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(node, "Modifier le profil");
            h.peakOffsetY = peak;
            h.baseWidth = Mathf.Max(0.5f, width);
            h.edgePositionT = edgeT;
            h.edgeOffsetY = edgeY;
            EditorUtility.SetDirty(node);
            TerrainModifierManager.RequestUpdate();
        }

        DrawProfilePreview(h, node.smoothProfile, col);

        // Actions sur les profils
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button(new GUIContent("Copier vers tous", "Applique ce profil à tous les points.")))
                ForAll(node, "Copier le profil vers tous", p => p.CopyFrom(h));
            if (GUILayout.Button(new GUIContent("Réinitialiser le point")))
            {
                Undo.RecordObject(node, "Réinitialiser le point");
                h.CopyFrom(new PointProfileHandle());
                TerrainModifierManager.RequestUpdate();
            }
            if (GUILayout.Button(new GUIContent("Réinit. tous")))
                ForAll(node, "Réinitialiser tous les profils", p => p.CopyFrom(new PointProfileHandle()));
        }

        // Outils spline
        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Outils", EditorStyles.boldLabel);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button(new GUIContent("Poser les nœuds au sol", "Aligne la hauteur de chaque nœud sur le terrain de base.")))
                SnapKnotsToGround(node);
            if (GUILayout.Button(new GUIContent("Lisser les hauteurs", "Moyenne la hauteur de chaque nœud avec ses voisins.")))
                SmoothKnotHeights(node);
        }
    }

    private void DrawDefaultInspectorExceptHandles()
    {
        serializedObject.Update();
        var it = serializedObject.GetIterator();
        bool enter = true;
        while (it.NextVisible(enter))
        {
            enter = false;
            if (it.propertyPath == "m_Script" || it.propertyPath == "pointHandles") continue;
            var node = (TerrainSplineProfileNode)target;
            // flatInterior n'a de sens qu'en mode Shape
            if (it.propertyPath == "flatInterior" && node.mode != SplineModifierMode.Shape) continue;
            EditorGUILayout.PropertyField(it, true);
        }
        serializedObject.ApplyModifiedProperties();
    }

    private static void ForAll(TerrainSplineProfileNode node, string undoName, System.Action<PointProfileHandle> action)
    {
        Undo.RecordObject(node, undoName);
        foreach (var p in node.pointHandles) action(p);
        EditorUtility.SetDirty(node);
        TerrainModifierManager.RequestUpdate();
    }

    private static void DrawProfilePreview(PointProfileHandle h, bool smooth, Color col)
    {
        Rect r = GUILayoutUtility.GetRect(10, 110, GUILayout.ExpandWidth(true));
        if (Event.current.type != EventType.Repaint) return;

        EditorGUI.DrawRect(r, new Color(0.14f, 0.14f, 0.14f, 1f));

        float range = Mathf.Max(1f, Mathf.Abs(h.peakOffsetY), Mathf.Abs(h.edgeOffsetY));
        float midY = r.center.y;
        float scaleY = r.height * 0.38f / range;

        Handles.BeginGUI();
        Handles.color = new Color(1, 1, 1, 0.2f);
        Handles.DrawLine(new Vector3(r.x, midY), new Vector3(r.xMax, midY));          // niveau 0
        Handles.DrawLine(new Vector3(r.center.x, r.y), new Vector3(r.center.x, r.yMax)); // axe

        const int steps = 48;
        var pts = new Vector3[steps + 1];
        for (int i = 0; i <= steps; i++)
        {
            float s = -1f + 2f * i / steps;
            float y = h.Evaluate(Mathf.Abs(s), smooth);
            pts[i] = new Vector3(r.x + (s * 0.5f + 0.5f) * r.width, midY - y * scaleY);
        }
        Handles.color = col;
        Handles.DrawAAPolyLine(3f, pts);

        // Point de bord (magenta)
        float ey = h.edgeOffsetY;
        Handles.color = Color.magenta;
        foreach (float sign in new[] { -1f, 1f })
        {
            Vector3 ep = new Vector3(r.x + (sign * h.edgePositionT * 0.5f + 0.5f) * r.width, midY - ey * scaleY);
            Handles.DrawSolidDisc(ep, Vector3.forward, 3f);
        }
        Handles.EndGUI();

        GUI.Label(new Rect(r.x + 4, r.y + 2, 200, 16), "Coupe transversale", EditorStyles.miniLabel);
    }

    // ------------------------------------------------------------------ Outils spline
    private static void SnapKnotsToGround(TerrainSplineProfileNode node)
    {
        var c = node.GetSplineContainer();
        if (c == null) return;
        Undo.RecordObject(c, "Poser les nœuds au sol");

        Spline sp = c.Spline;
        for (int i = 0; i < sp.Count; i++)
        {
            BezierKnot k = sp[i];
            Vector3 w = node.transform.TransformPoint((Vector3)k.Position);
            if (!TerrainModifierManager.TrySampleBaseHeight(w.x, w.z, out float y)) continue;
            Vector3 local = node.transform.InverseTransformPoint(new Vector3(w.x, y, w.z));
            k.Position = new float3(local.x, local.y, local.z);
            sp.SetKnot(i, k);
        }
        EditorUtility.SetDirty(c);
        TerrainModifierManager.RequestUpdate();
    }

    private static void SmoothKnotHeights(TerrainSplineProfileNode node)
    {
        var c = node.GetSplineContainer();
        if (c == null) return;
        Spline sp = c.Spline;
        int n = sp.Count;
        if (n < 3) return;

        Undo.RecordObject(c, "Lisser les hauteurs");
        var ys = new float[n];
        for (int i = 0; i < n; i++)
        {
            float prev = sp[Mathf.Max(i - 1, 0)].Position.y;
            float next = sp[Mathf.Min(i + 1, n - 1)].Position.y;
            ys[i] = (prev + sp[i].Position.y * 2f + next) * 0.25f;
        }
        for (int i = 0; i < n; i++)
        {
            BezierKnot k = sp[i];
            k.Position = new float3(k.Position.x, ys[i], k.Position.z);
            sp.SetKnot(i, k);
        }
        EditorUtility.SetDirty(c);
        TerrainModifierManager.RequestUpdate();
    }

    // ------------------------------------------------------------------ Scène
    private void OnSceneGUI()
    {
        var node = (TerrainSplineProfileNode)target;
        var container = node.GetSplineContainer();
        if (container == null || container.Spline == null) return;

        // Panneau + outils de peinture (avant le test "count == 0" : on peut peindre sur une spline vide).
        SplinePaintTool.DrawScenePanel(node);
        if (SplinePaintTool.HandleScene(node)) return;

        node.SyncHandlesWithSpline();
        int count = Mathf.Min(container.Spline.Count, node.pointHandles.Count);
        if (count == 0) return;
        selected = Mathf.Clamp(selected, 0, count - 1);

        DrawSceneToolbar(node, count);

        for (int i = 0; i < count; i++)
        {
            if (!node.TryGetKnotFrame(i, out Vector3 pos, out Vector3 right)) continue;
            PointProfileHandle h = node.pointHandles[i];

            if (i != selected)
            {
                // Point non sélectionné : simple bouton de sélection.
                float s = HandleUtility.GetHandleSize(pos) * 0.09f;
                Handles.color = new Color(1f, 1f, 1f, 0.6f);
                if (Handles.Button(pos + Vector3.up * 0.3f, Quaternion.identity, s, s * 1.6f, Handles.SphereHandleCap))
                {
                    selected = i;
                    Repaint();
                }
                continue;
            }

            DrawSelectedPointHandles(node, h, i, pos, right);
        }
    }

    private void DrawSelectedPointHandles(TerrainSplineProfileNode node, PointProfileHandle h, int i, Vector3 pos, Vector3 right)
    {
        float size = HandleUtility.GetHandleSize(pos);
        Vector3 up = Vector3.up;

        // Coupe transversale 3D (côtés gauche et droit).
        Handles.color = ModifierMath.ColorOf(node.operation);
        const int steps = 32;
        var line = new Vector3[steps + 1];
        for (int k = 0; k <= steps; k++)
        {
            float s = -1f + 2f * k / steps;
            line[k] = pos + right * (s * h.baseWidth) + up * h.Evaluate(Mathf.Abs(s), node.smoothProfile);
        }
        Handles.DrawAAPolyLine(3f, line);

        // 1) Sommet (cyan) : glisse en vertical
        Vector3 peakPos = pos + up * h.peakOffsetY;
        Handles.color = Color.cyan;
        Handles.DrawLine(pos, peakPos);
        EditorGUI.BeginChangeCheck();
        Vector3 newPeak = Handles.Slider(peakPos, up, size * 0.12f, Handles.SphereHandleCap, 0f);
        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(node, "Modifier la hauteur du sommet");
            h.peakOffsetY = newPeak.y - pos.y;
            Commit(node);
        }
        Handles.Label(peakPos + up * size * 0.2f, $"[{i}] Sommet {h.peakOffsetY:0.0} m");

        // 2) Largeur (jaune) : glisse sur l'axe droite, miroir côté gauche
        Vector3 basePos = pos + right * h.baseWidth;
        Handles.color = Color.yellow;
        Handles.DrawLine(pos, basePos);
        Handles.DrawDottedLine(pos, pos - right * h.baseWidth, 4f);
        EditorGUI.BeginChangeCheck();
        Vector3 newBase = Handles.Slider(basePos, right, size * 0.12f, Handles.CubeHandleCap, 0f);
        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(node, "Modifier la largeur");
            h.baseWidth = Mathf.Max(0.5f, Vector3.Dot(newBase - pos, right));
            Commit(node);
        }
        Handles.Label(basePos + up * size * 0.2f, $"[{i}] Largeur {h.baseWidth:0.0} m");

        // 3) Bord intermédiaire (magenta) : déplacement libre, projeté sur l'axe droite
        Vector3 edgePos = pos + right * (h.baseWidth * h.edgePositionT) + up * h.edgeOffsetY;
        Handles.color = Color.magenta;
        EditorGUI.BeginChangeCheck();
        Vector3 newEdge = Handles.FreeMoveHandle(edgePos, size * 0.1f, Vector3.zero, Handles.ConeHandleCap);
        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(node, "Modifier le bord");
            h.edgeOffsetY = newEdge.y - pos.y;
            h.edgePositionT = Mathf.Clamp01(Vector3.Dot(newEdge - pos, right) / Mathf.Max(0.001f, h.baseWidth));
            Commit(node);
        }
        Handles.Label(edgePos + up * size * 0.2f, $"[{i}] Bord t={h.edgePositionT:0.00}, {h.edgeOffsetY:0.0} m");
    }

    private static void Commit(TerrainSplineProfileNode node)
    {
        EditorUtility.SetDirty(node);
        TerrainModifierManager.RequestUpdate();
    }

    private void DrawSceneToolbar(TerrainSplineProfileNode node, int count)
    {
        Handles.BeginGUI();
        GUILayout.BeginArea(new Rect(10, 10, 250, 112), EditorStyles.helpBox);
        GUILayout.Label($"{node.name} — point {selected + 1}/{count}", EditorStyles.boldLabel);

        EditorGUI.BeginChangeCheck();
        var op = (ModifierOperation)EditorGUILayout.EnumPopup(node.operation);
        float strength = EditorGUILayout.Slider(node.strength, 0f, 1f);
        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(node, "Modifier le nœud spline");
            node.operation = op;
            node.strength = strength;
            Commit(node);
        }

        using (new GUILayout.HorizontalScope())
        {
            if (GUILayout.Button("◀ Précédent")) selected = (selected - 1 + count) % count;
            if (GUILayout.Button("Suivant ▶")) selected = (selected + 1) % count;
        }
        GUILayout.EndArea();
        Handles.EndGUI();
    }
}
#endif

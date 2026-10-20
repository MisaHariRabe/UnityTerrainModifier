#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(TerrainModifierManager))]
public class TerrainModifierManagerEditor : Editor
{
    private static bool showList = true;
    private readonly List<Behaviour> cache = new List<Behaviour>();
    private bool cacheDirty = true;

    private void OnEnable()
    {
        EditorApplication.hierarchyChanged += Invalidate;
        Undo.undoRedoPerformed += Invalidate;
    }

    private void OnDisable()
    {
        EditorApplication.hierarchyChanged -= Invalidate;
        Undo.undoRedoPerformed -= Invalidate;
    }

    private void Invalidate() { cacheDirty = true; Repaint(); }

    private void RefreshCache()
    {
        cache.Clear();
        cache.AddRange(FindObjectsByType<TerrainModifierNode>(FindObjectsInactive.Include));
        cache.AddRange(FindObjectsByType<TerrainSplineProfileNode>(FindObjectsInactive.Include));
        cache.Sort((a, b) => ((ITerrainModifier)a).LayerOrder.CompareTo(((ITerrainModifier)b).LayerOrder));
        cacheDirty = false;
    }

    public override void OnInspectorGUI()
    {
        var m = (TerrainModifierManager)target;
        serializedObject.Update();

        // ---- Statut ----
        string status = m.SculptMode
            ? "Mode sculpt actif : les modificateurs sont suspendus."
            : $"{m.LastModifierCount} modificateur(s) actif(s)  •  dernier calcul {m.LastApplyMs:0.0} ms  •  base {m.BaseMemoryMB:0.0} Mo";
        EditorGUILayout.HelpBox(status, m.SculptMode ? MessageType.Warning : MessageType.None);

        if (m.TryGetBaseRange(out float baseMin, out float baseMax))
        {
            bool flat = Mathf.Abs(baseMax - baseMin) < 0.01f;
            EditorGUILayout.HelpBox(
                $"Hauteur de la base capturée : {baseMin:0.#} m → {baseMax:0.#} m" +
                (flat ? "\nLa base est plate : normal pour un terrain vierge. Si votre terrain a du relief, il a été capturé avant sa génération." : ""),
                flat ? MessageType.Warning : MessageType.None);
        }

        if (m.SculptMode)
        {
            EditorGUILayout.HelpBox("Sculptez/peignez le terrain avec les outils Unity, puis cliquez sur « Terminer le sculpt » : le résultat devient la nouvelle base.", MessageType.Info);
            if (GUILayout.Button("Terminer le sculpt", GUILayout.Height(28)))
            {
                Undo.RecordObject(m, "Terminer le sculpt");
                m.EndSculptMode();
            }
            return;
        }

        // ---- Options ----
        EditorGUILayout.PropertyField(serializedObject.FindProperty("autoUpdate"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("restoreBaseOnDisable"));
        serializedObject.ApplyModifiedProperties();

        // ---- Actions ----
        EditorGUILayout.Space(4);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button(new GUIContent("Appliquer", "Recalcule le terrain maintenant."), GUILayout.Height(24)))
                m.ApplyModifications();

            if (GUILayout.Button(new GUIContent("Mode sculpt", "Retire les modificateurs pour sculpter le terrain de base."), GUILayout.Height(24)))
            {
                Undo.RecordObject(m, "Mode sculpt");
                m.BeginSculptMode();
            }
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button(new GUIContent("Restaurer la base", "Affiche le terrain de base sans modificateurs (jusqu'au prochain changement)."))) m.RestoreBase();

            if (GUILayout.Button(new GUIContent("Figer (Bake)", "Intègre le résultat dans la base et désactive tous les modificateurs.")))
            {
                if (EditorUtility.DisplayDialog("Figer le terrain",
                        "Le résultat actuel devient le nouveau terrain de base et tous les modificateurs seront désactivés.\n\nContinuer ?",
                        "Figer", "Annuler"))
                    Bake(m);
            }
        }

        // ---- Liste des modificateurs ----
        EditorGUILayout.Space(6);
        showList = EditorGUILayout.BeginFoldoutHeaderGroup(showList, "Modificateurs de la scène");
        if (showList)
        {
            if (cacheDirty) RefreshCache();

            if (cache.Count == 0)
                EditorGUILayout.HelpBox("Aucun modificateur. Utilisez le menu GameObject > Terrain Modifiers.", MessageType.Info);

            foreach (var b in cache.Where(x => x != null))
            {
                var mod = (ITerrainModifier)b;
                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                {
                    EditorGUI.BeginChangeCheck();
                    bool on = EditorGUILayout.Toggle(b.enabled && b.gameObject.activeInHierarchy, GUILayout.Width(18));
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(b, "Activer/Désactiver modificateur");
                        b.enabled = on;
                    }

                    var r = GUILayoutUtility.GetRect(6, 6, GUILayout.Width(6), GUILayout.Height(18));
                    EditorGUI.DrawRect(r, ModifierMath.ColorOf(mod.Operation));

                    if (GUILayout.Button(mod.Label, EditorStyles.label)) { Selection.activeGameObject = b.gameObject; EditorGUIUtility.PingObject(b); }
                    GUILayout.FlexibleSpace();
                    GUILayout.Label(mod.Operation.ToString(), EditorStyles.miniLabel, GUILayout.Width(60));
                    GUILayout.Label($"L{mod.LayerOrder}", EditorStyles.miniLabel, GUILayout.Width(30));
                }
            }
        }
        EditorGUILayout.EndFoldoutHeaderGroup();

        EditorGUILayout.HelpBox("Les modificateurs sont recalculés sur une copie de la base. Pour modifier le terrain manuellement, utilisez « Mode sculpt » afin de ne pas perdre vos changements.", MessageType.None);
    }

    private static void Bake(TerrainModifierManager m)
    {
        Undo.RecordObject(m, "Figer le terrain");
        var owners = TerrainModifierManager.Modifiers.Where(x => x.Owner != null).Select(x => x.Owner).ToList();
        m.BakeCurrent();
        foreach (var o in owners)
        {
            Undo.RecordObject(o, "Figer le terrain");
            o.enabled = false;
        }
    }
}
#endif
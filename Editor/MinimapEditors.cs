#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RoadTools
{
    [CustomEditor(typeof(MinimapMap))]
    public class MinimapMapEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            var m = (MinimapMap)target;

            Rect header = GUILayoutUtility.GetRect(0, 22, GUILayout.ExpandWidth(true));
            Color hc = new Color(0.75f, 0.75f, 0.75f);
            EditorGUI.DrawRect(header, new Color(hc.r, hc.g, hc.b, 0.2f));
            EditorGUI.DrawRect(new Rect(header.x, header.y, 4, header.height), hc);
            string info = m.HasMap ? $"{m.Texture.width}×{m.Texture.height}px  •  {m.BoundsSize.x:0}×{m.BoundsSize.y:0} m" : "pas encore générée";
            GUI.Label(new Rect(header.x + 10, header.y + 2, header.width - 12, 18), $"Minimap  •  {info}", EditorStyles.boldLabel);

            if (Terrain.activeTerrains.Length == 0 && m.autoBounds)
                EditorGUILayout.HelpBox("Aucun terrain actif : décochez « Auto Bounds » et renseignez l'emprise manuelle.", MessageType.Warning);

            DrawDefaultInspector();

            EditorGUILayout.Space(6);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Générer la minimap", GUILayout.Height(26)))
                {
                    EditorUtility.DisplayProgressBar("Minimap", "Rasterisation du monde…", 0.5f);
                    try { m.Bake(); }
                    finally { EditorUtility.ClearProgressBar(); }
                }
                using (new EditorGUI.DisabledScope(m.RuntimeTexture == null))
                    if (GUILayout.Button(new GUIContent("Exporter en PNG", "Enregistre dans Assets/Minimap et l'assigne : utilisé en jeu sans nouveau bake."), GUILayout.Height(26)))
                        SavePng(m);
            }

            Texture2D tex = m.Texture;
            if (tex != null)
            {
                float h = Mathf.Min(300f, (EditorGUIUtility.currentViewWidth - 30f) * tex.height / tex.width);
                Rect r = GUILayoutUtility.GetRect(0, h, GUILayout.ExpandWidth(true));
                EditorGUI.DrawRect(r, new Color(0.1f, 0.1f, 0.1f));
                GUI.DrawTexture(r, tex, ScaleMode.ScaleToFit);
            }
        }

        private static void SavePng(MinimapMap m)
        {
            string folder = "Assets/Minimap";
            if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder("Assets", "Minimap");

            string path = $"{folder}/{SceneManager.GetActiveScene().name}_{m.name}.png";
            File.WriteAllBytes(path, m.RuntimeTexture.EncodeToPNG());
            AssetDatabase.ImportAsset(path);

            var imp = (TextureImporter)AssetImporter.GetAtPath(path);
            if (imp != null)
            {
                imp.textureType = TextureImporterType.Default;
                imp.mipmapEnabled = false;
                imp.wrapMode = TextureWrapMode.Clamp;
                imp.filterMode = FilterMode.Bilinear;
                imp.npotScale = TextureImporterNPOTScale.None;
                imp.maxTextureSize = 4096;
                imp.textureCompression = TextureImporterCompression.Uncompressed;
                imp.isReadable = false;
                imp.SaveAndReimport();
            }

            Undo.RecordObject(m, "Assigner la minimap");
            m.bakedAsset = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            EditorUtility.SetDirty(m);
            EditorGUIUtility.PingObject(m.bakedAsset);
        }
    }

    [CustomEditor(typeof(MinimapView))]
    public class MinimapViewEditor : Editor
    {
        private static bool pick;

        public override void OnInspectorGUI()
        {
            var v = (MinimapView)target;
            DrawDefaultInspector();

            EditorGUILayout.Space(6);
            if (v.map == null)
                EditorGUILayout.HelpBox("Assignez un MinimapMap.", MessageType.Warning);

            string status = !v.TryGetDestination(out _) ? "Aucune destination."
                          : v.HasRoute ? $"Itinéraire trouvé : {v.RouteLength:0} m, {v.Route.Count} points."
                          : "Aucun itinéraire (point hors route, ou réseau non raccordé).";
            EditorGUILayout.HelpBox(status, MessageType.None);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Calculer l'itinéraire", GUILayout.Height(24)))
                    { v.InvalidateNetwork(); v.RecomputeRoute(); SceneView.RepaintAll(); }
                if (GUILayout.Button("Effacer", GUILayout.Height(24)))
                    { Undo.RecordObject(v, "Effacer la destination"); v.ClearDestination(); SceneView.RepaintAll(); }
            }

            var prev = GUI.backgroundColor;
            if (pick) GUI.backgroundColor = new Color(1f, 0.6f, 0.3f);
            if (GUILayout.Button(pick ? "Cliquez sur le terrain… (Échap pour annuler)" : "Choisir la destination dans la scène", GUILayout.Height(24)))
                pick = !pick;
            GUI.backgroundColor = prev;
        }

        private void OnSceneGUI()
        {
            var v = (MinimapView)target;

            if (pick)
            {
                Event e = Event.current;
                int id = GUIUtility.GetControlID(FocusType.Passive);
                if (e.type == EventType.Layout) HandleUtility.AddDefaultControl(id);

                if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape) { pick = false; e.Use(); }
                else if (e.GetTypeForControl(id) == EventType.MouseDown && e.button == 0 && !e.alt
                         && HandleUtility.nearestControl == id && TerrainRay.TryHit(e.mousePosition, out Vector3 hit))
                {
                    Undo.RecordObject(v, "Destination de la minimap");
                    v.SetDestination(hit);
                    EditorUtility.SetDirty(v);
                    pick = false;
                    e.Use();
                }
                Handles.BeginGUI();
                GUI.Label(new Rect(12, 10, 460, 22), "Minimap : clique sur le terrain pour fixer la destination", EditorStyles.boldLabel);
                Handles.EndGUI();
            }

            if (v.Route.Count >= 2)
            {
                var pts = new Vector3[v.Route.Count];
                for (int i = 0; i < pts.Length; i++) pts[i] = v.Route[i] + Vector3.up * 0.8f;
                Handles.color = v.routeColor;
                Handles.DrawAAPolyLine(6f, pts);
            }
        }
    }

    public static class MinimapMenu
    {
        [MenuItem("GameObject/Road System/Minimap", false, 50)]
        private static void Create()
        {
            var go = new GameObject("Minimap");
            Undo.RegisterCreatedObjectUndo(go, "Créer la minimap");
            var map = Undo.AddComponent<MinimapMap>(go);
            var view = Undo.AddComponent<MinimapView>(go);
            view.map = map;
            Selection.activeGameObject = go;
        }
    }
}
#endif
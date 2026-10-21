#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.Splines;

namespace RoadTools
{
    public static class RoadMenu
    {
        private const string Root = "GameObject/Road System/";

        [MenuItem(Root + "Route", false, 30)]
        private static void CreateRoad(MenuCommand cmd)
        {
            Vector3 spawn = SpawnPoint();
            EnsureManagerAt(spawn);
            var go = new GameObject("Road");
            Undo.RegisterCreatedObjectUndo(go, "Créer une route");
            GameObjectUtility.SetParentAndAlign(go, cmd.context as GameObject);
            go.transform.position = spawn;

            var road = Undo.AddComponent<Road>(go); // ajoute aussi le SplineContainer
            var container = go.GetComponent<SplineContainer>();
            Spline sp = container.Spline;
            sp.Clear();               // Route VIDE : les nœuds se posent ensuite avec Ctrl+clic (voir RoadDrawTool).
            sp.Closed = false;

            Selection.activeGameObject = go;
            road.RequestRebuild();
            Debug.Log("[Road System] Route créée. Ctrl+clic (Cmd+clic sur Mac) sur le terrain pour poser les nœuds.");
        }

        [MenuItem(Root + "Intersection Y", false, 40)]
        private static void CreateY(MenuCommand cmd) => CreateIntersection(cmd, IntersectionPreset.Y, "Intersection Y");

        [MenuItem(Root + "Intersection T", false, 41)]
        private static void CreateT(MenuCommand cmd) => CreateIntersection(cmd, IntersectionPreset.T, "Intersection T");

        [MenuItem(Root + "Intersection X", false, 42)]
        private static void CreateX(MenuCommand cmd) => CreateIntersection(cmd, IntersectionPreset.X, "Intersection X");

        [MenuItem(Root + "Rond-point (4 sorties)", false, 43)]
        private static void CreateRoundabout(MenuCommand cmd) => CreateIntersection(cmd, IntersectionPreset.Roundabout, "Rond-point", 4);

        [MenuItem(Root + "Rond-point (6 sorties)", false, 44)]
        private static void CreateRoundabout6(MenuCommand cmd) => CreateIntersection(cmd, IntersectionPreset.Roundabout, "Rond-point 6", 6);

        [MenuItem(Root + "Rond-point sorties en Y", false, 45)]
        private static void CreateRoundaboutY(MenuCommand cmd) => CreateIntersection(cmd, IntersectionPreset.RoundaboutY, "Rond-point Y");

        [MenuItem(Root + "Rond-point sorties en T", false, 46)]
        private static void CreateRoundaboutT(MenuCommand cmd) => CreateIntersection(cmd, IntersectionPreset.RoundaboutT, "Rond-point T");

        [MenuItem(Root + "Bout de route (cercle)", false, 47)]
        private static void CreateEndCap(MenuCommand cmd) => CreateIntersection(cmd, IntersectionPreset.EndCap, "Bout de route");

        private static void CreateIntersection(MenuCommand cmd, IntersectionPreset preset, string name, int exits = 4)
        {
            Vector3 spawn = SpawnPoint();
            EnsureManagerAt(spawn);
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "Créer " + name);
            GameObjectUtility.SetParentAndAlign(go, cmd.context as GameObject);
            go.transform.position = spawn;

            var hub = Undo.AddComponent<RoadIntersection>(go);
            hub.ApplyPreset(preset, exits);
            Selection.activeGameObject = go;
        }

        // Ajoute un manager au terrain qui se trouve sous le point de création (et non au "premier" terrain de la scène).
        private static void EnsureManagerAt(Vector3 world)
        {
            var t = TerrainModifierManager.FindTerrainAt(world);
            if (t == null)
            {
                Debug.LogWarning("[Road System] Le point de création est en dehors de tout terrain : déplacez la vue Scène au-dessus d'un terrain.");
                return;
            }
            if (t.GetComponent<TerrainModifierManager>() == null)
                Undo.AddComponent<TerrainModifierManager>(t.gameObject);
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

    /// <summary>Aide d'inspecteur : état du terrain sous un point (manager présent, actif, base, etc.).</summary>
    public static class RoadEditorUtil
    {
        public static void DrawTerrainStatus(Vector3 worldPos)
        {
            Terrain t = TerrainModifierManager.FindTerrainAt(worldPos);
            if (t == null)
            {
                EditorGUILayout.HelpBox("Ce point est en dehors de tous les terrains actifs de la scène : aucune déformation ni « poser au sol » possible ici.", MessageType.Warning);
                return;
            }

            var m = t.GetComponent<TerrainModifierManager>();
            if (m == null)
            {
                EditorGUILayout.HelpBox($"Le terrain « {t.name} » n'a pas de TerrainModifierManager : il ne se déformera pas.", MessageType.Warning);
                if (GUILayout.Button($"Ajouter un Manager à « {t.name} »"))
                    Undo.AddComponent<TerrainModifierManager>(t.gameObject);
                return;
            }

            if (!m.enabled) { EditorGUILayout.HelpBox($"Le TerrainModifierManager de « {t.name} » est désactivé.", MessageType.Warning); return; }
            if (m.SculptMode) { EditorGUILayout.HelpBox("Le manager est en Mode sculpt : les modificateurs sont suspendus.", MessageType.Warning); return; }
            if (!m.autoUpdate) { EditorGUILayout.HelpBox("Le manager a « Auto Update » désactivé : cliquez sur Appliquer dans son inspecteur.", MessageType.Warning); return; }

            if (m.TryGetBaseRange(out float mn, out float mx))
                EditorGUILayout.HelpBox($"Terrain « {t.name} » : base de {mn:0.#} m à {mx:0.#} m.", MessageType.None);
        }
    }
}
#endif
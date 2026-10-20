#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.Splines;
using Unity.Mathematics;

public static class TerrainModifierMenu
{
    [MenuItem("GameObject/Terrain Modifiers/Point Node", false, 20)]
    private static void CreatePointNode(MenuCommand cmd)
    {
        EnsureManager();
        var go = new GameObject("Terrain Point Node");
        Undo.RegisterCreatedObjectUndo(go, "Créer un Point Node");
        GameObjectUtility.SetParentAndAlign(go, cmd.context as GameObject);
        go.transform.position = SpawnPoint(5f);
        Undo.AddComponent<TerrainModifierNode>(go);
        Selection.activeGameObject = go;
    }

    [MenuItem("GameObject/Terrain Modifiers/Spline Path (route, rivière)", false, 21)]
    private static void CreatePath(MenuCommand cmd) => CreateSpline(cmd, SplineModifierMode.Path, ModifierOperation.Blend, "Terrain Spline Path");

    [MenuItem("GameObject/Terrain Modifiers/Spline Shape (plateforme, lac)", false, 22)]
    private static void CreateShape(MenuCommand cmd) => CreateSpline(cmd, SplineModifierMode.Shape, ModifierOperation.Blend, "Terrain Spline Shape");

    private static void CreateSpline(MenuCommand cmd, SplineModifierMode mode, ModifierOperation op, string name)
    {
        EnsureManager();
        var go = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(go, "Créer " + name);
        GameObjectUtility.SetParentAndAlign(go, cmd.context as GameObject);
        go.transform.position = SpawnPoint(0f);

        var node = Undo.AddComponent<TerrainSplineProfileNode>(go); // ajoute aussi le SplineContainer
        node.mode = mode;
        node.operation = op;

        var container = go.GetComponent<SplineContainer>();
        Spline sp = container.Spline;
        sp.Clear();
        if (mode == SplineModifierMode.Path)
        {
            sp.Add(new BezierKnot(new float3(-30f, 0f, 0f)), TangentMode.AutoSmooth);
            sp.Add(new BezierKnot(new float3(0f, 0f, 15f)), TangentMode.AutoSmooth);
            sp.Add(new BezierKnot(new float3(30f, 0f, 0f)), TangentMode.AutoSmooth);
            sp.Closed = false;
        }
        else
        {
            sp.Add(new BezierKnot(new float3(-20f, 0f, -20f)), TangentMode.Linear);
            sp.Add(new BezierKnot(new float3(20f, 0f, -20f)), TangentMode.Linear);
            sp.Add(new BezierKnot(new float3(20f, 0f, 20f)), TangentMode.Linear);
            sp.Add(new BezierKnot(new float3(-20f, 0f, 20f)), TangentMode.Linear);
            sp.Closed = true;
        }

        node.SyncHandlesWithSpline();
        foreach (var h in node.pointHandles) h.baseWidth = mode == SplineModifierMode.Path ? 8f : 10f;

        Selection.activeGameObject = go;
        TerrainModifierManager.RequestUpdate();
    }

    private static void EnsureManager()
    {
        if (TerrainModifierManager.Managers.Count > 0) return;
        var t = Terrain.activeTerrain;
        if (t == null)
        {
            Debug.LogWarning("[Terrain Modifiers] Aucun Terrain actif : ajoutez un TerrainModifierManager manuellement.");
            return;
        }
        Undo.AddComponent<TerrainModifierManager>(t.gameObject);
    }

    private static Vector3 SpawnPoint(float lift)
    {
        Vector3 p = Vector3.zero;
        var sv = SceneView.lastActiveSceneView;
        if (sv != null) p = sv.pivot;
        if (TerrainModifierManager.TrySampleBaseHeight(p.x, p.z, out float y)) p.y = y + lift;
        return p;
    }
}
#endif

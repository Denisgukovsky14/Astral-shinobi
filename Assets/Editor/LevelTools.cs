using System.Collections.Generic;
using System.IO;
using Pathfinding;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Инструменты для сборки уровней. Меню «Astral Shinobi»:
//   Подогнать уровень - после того как расставил стены/колонны: сетка A* и границы врагов подгоняются под
//       габариты геометрии уровня (слой Obstacles), сцена сохраняется;
//   Новый уровень из шаблона - копирует Level_Template.unity под новым именем и добавляет в Build Settings.
public static class LevelTools
{
    const string TemplatePath = "Assets/Scenes/Level_Template.unity";
    const string PlaceholderDir = "Assets/Sprites/Placeholders";

    // ---------- Подгонка уровня ----------

    [MenuItem("Astral Shinobi/Подогнать уровень (сетка A* и границы врагов)")]
    public static void FitLevelMenu()
    {
        Debug.Log(FitLevel());
    }

    public static string FitLevel(float margin = 1f)
    {
        int obstacleLayer = LayerMask.NameToLayer("Obstacles");
        Bounds b = default;
        bool any = false;
        foreach (var c in Object.FindObjectsByType<Collider2D>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (c.isTrigger || c.gameObject.layer != obstacleLayer) continue;
            // Сам композит пропускаем (в редакторе его геометрия может быть ещё не построена), берём его части.
            if (c is CompositeCollider2D) continue;
            Bounds cb = WorldBounds(c);
            if (!any) { b = cb; any = true; } else b.Encapsulate(cb);
        }
        if (!any) return "Подгонка: на сцене нет препятствий на слое Obstacles.";

        var astar = Object.FindFirstObjectByType<AstarPath>();
        if (astar == null) return "Подгонка: на сцене нет AstarPath.";

        var data = astar.data;
        data.DeserializeGraphs();
        var gg = data.gridGraph;
        Vector3 center = new Vector3(b.center.x, b.center.y, 0f);
        int w = Mathf.CeilToInt((b.size.x + margin * 2f) / gg.nodeSize);
        int h = Mathf.CeilToInt((b.size.y + margin * 2f) / gg.nodeSize);
        gg.center = center;
        gg.SetDimensions(w, h, gg.nodeSize);
        data.SetData(data.SerializeGraphs());
        data.data_cachedStartup = null;
        EditorUtility.SetDirty(astar);

        // Границы, в которых враги удерживаются и по которым кунайщик считает рикошеты: внутренние грани внешних стен.
        var injector = Object.FindFirstObjectByType<PlayerInjector>();
        string boundsInfo = "PlayerInjector не найден (границы врагов не заданы)";
        if (injector != null)
        {
            injector.overrideEnemyBounds = true;
            injector.levelBoundsMin = new Vector2(b.min.x + 0.2f, b.min.y + 0.2f);
            injector.levelBoundsMax = new Vector2(b.max.x - 0.2f, b.max.y - 0.2f);
            EditorUtility.SetDirty(injector);
            boundsInfo = $"границы врагов ({injector.levelBoundsMin.x:F1};{injector.levelBoundsMin.y:F1})..({injector.levelBoundsMax.x:F1};{injector.levelBoundsMax.y:F1})";
        }

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        EditorSceneManager.SaveOpenScenes();

        string warn = w * h > 120000 ? $" ВНИМАНИЕ: {w * h} узлов - много для телефона, увеличь Node Size сетки." : "";
        return $"Подгонка: размер уровня {b.size.x:F1}x{b.size.y:F1}, сетка A* {w}x{h} ({w * h} узлов), {boundsInfo}.{warn}";
    }

    // Габариты коллайдера в мире считаем по геометрии и трансформу, а не через Collider2D.bounds: в редакторе
    // (вне Play) физика ещё не обновила bounds, и они бывают нулевыми/устаревшими.
    static Bounds WorldBounds(Collider2D c)
    {
        var points = new List<Vector2>();
        if (c is BoxCollider2D box)
        {
            Vector2 h = box.size * 0.5f;
            foreach (var corner in new[] { new Vector2(-h.x, -h.y), new Vector2(h.x, -h.y), new Vector2(h.x, h.y), new Vector2(-h.x, h.y) })
                points.Add(c.transform.TransformPoint(box.offset + corner));
        }
        else if (c is PolygonCollider2D poly)
        {
            for (int p = 0; p < poly.pathCount; p++)
                foreach (var v in poly.GetPath(p)) points.Add(c.transform.TransformPoint(poly.offset + v));
        }
        else if (c is CircleCollider2D circle)
        {
            Vector2 center = c.transform.TransformPoint(circle.offset);
            float r = circle.radius * Mathf.Max(Mathf.Abs(c.transform.lossyScale.x), Mathf.Abs(c.transform.lossyScale.y));
            points.Add(center + new Vector2(-r, -r)); points.Add(center + new Vector2(r, r));
        }
        else
        {
            return c.bounds;
        }
        var b = new Bounds(points[0], Vector3.zero);
        foreach (var p in points) b.Encapsulate(p);
        return b;
    }

    // ---------- Новый уровень из шаблона ----------

    [MenuItem("Astral Shinobi/Новый уровень из шаблона")]
    public static void NewLevelMenu()
    {
        string path = EditorUtility.SaveFilePanelInProject("Новый уровень", "Level_01", "unity", "Куда сохранить копию шаблона", "Assets/Scenes");
        if (string.IsNullOrEmpty(path)) return;
        CreateLevelFromTemplate(path);
    }

    public static string CreateLevelFromTemplate(string path)
    {
        if (!File.Exists(TemplatePath)) return "Нет шаблона " + TemplatePath;
        if (!AssetDatabase.CopyAsset(TemplatePath, path)) return "Не удалось скопировать шаблон в " + path;
        AddToBuildSettings(path);
        EditorSceneManager.OpenScene(path);
        return "Создан уровень " + path + " (добавлен в Build Settings)";
    }

    public static void AddToBuildSettings(string scenePath)
    {
        var list = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
        if (list.Exists(s => s.path == scenePath)) return;
        list.Add(new EditorBuildSettingsScene(scenePath, true));
        EditorBuildSettings.scenes = list.ToArray();
    }

    // ---------- Заглушки двери и ключа (пока без арта) ----------

    [MenuItem("Astral Shinobi/Пересоздать заглушки ключа и двери")]
    public static void BuildPlaceholdersMenu()
    {
        Debug.Log(BuildPlaceholders());
    }

    public static string BuildPlaceholders()
    {
        Directory.CreateDirectory(PlaceholderDir);
        string key = SavePng("key.png", MakeKey(64));
        string doorClosed = SavePng("door_closed.png", MakeDoor(160, 32, true));
        string doorOpen = SavePng("door_open.png", MakeDoor(160, 32, false));
        AssetDatabase.Refresh();
        foreach (var p in new[] { key, doorClosed, doorOpen }) SetSpriteImport(p);

        var keySprite = AssetDatabase.LoadAssetAtPath<Sprite>(key);
        var closed = AssetDatabase.LoadAssetAtPath<Sprite>(doorClosed);
        var open = AssetDatabase.LoadAssetAtPath<Sprite>(doorOpen);
        int obstacles = LayerMask.NameToLayer("Obstacles");

        // Key.prefab
        var kgo = new GameObject("Key");
        var ksr = kgo.AddComponent<SpriteRenderer>(); ksr.sprite = keySprite; ksr.sortingOrder = 5;
        var kcol = kgo.AddComponent<CircleCollider2D>(); kcol.isTrigger = true; kcol.radius = 0.5f;
        kgo.AddComponent<KeyPickup>();
        PrefabUtility.SaveAsPrefabAsset(kgo, "Assets/Prefabs/Key.prefab");
        Object.DestroyImmediate(kgo);

        // ExitDoor.prefab: корень - сама дверь; +Y наружу.
        var dgo = new GameObject("ExitDoor");
        dgo.layer = obstacles;
        var dsr = dgo.AddComponent<SpriteRenderer>(); dsr.sprite = closed; dsr.sortingOrder = 2;
        var solid = dgo.AddComponent<BoxCollider2D>(); solid.size = new Vector2(1.6f, 0.19f);
        var door = dgo.AddComponent<ExitDoor>();
        door.solid = solid; door.sprite = dsr; door.closedSprite = closed; door.openSprite = open;
        door.openZone = MakeZone(dgo.transform, "OpenZone", new Vector2(0f, -0.9f), new Vector2(2.0f, 1.4f));
        door.exitZone = MakeZone(dgo.transform, "ExitZone", new Vector2(0f, 0.8f), new Vector2(1.6f, 1.0f));
        PrefabUtility.SaveAsPrefabAsset(dgo, "Assets/Prefabs/ExitDoor.prefab");
        Object.DestroyImmediate(dgo);

        return "Заглушки созданы: Assets/Prefabs/Key.prefab, Assets/Prefabs/ExitDoor.prefab (спрайты в " + PlaceholderDir + ")";
    }

    static ZoneRelay MakeZone(Transform parent, string name, Vector2 center, Vector2 size)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var col = go.AddComponent<BoxCollider2D>();
        col.isTrigger = true; col.offset = center; col.size = size;
        return go.AddComponent<ZoneRelay>();
    }

    static string SavePng(string file, Texture2D tex)
    {
        string path = PlaceholderDir + "/" + file;
        File.WriteAllBytes(path, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
        return path;
    }

    static void SetSpriteImport(string path)
    {
        var imp = (TextureImporter)AssetImporter.GetAtPath(path);
        imp.textureType = TextureImporterType.Sprite;
        imp.spriteImportMode = SpriteImportMode.Single;
        imp.spritePixelsPerUnit = 100f;
        imp.filterMode = FilterMode.Point;
        imp.mipmapEnabled = false;
        imp.alphaIsTransparency = true;
        imp.SaveAndReimport();
    }

    static Texture2D Blank(int w, int h)
    {
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        var clear = new Color[w * h];
        tex.SetPixels(clear);
        return tex;
    }

    static void Rect(Texture2D t, int x0, int y0, int x1, int y1, Color c)
    {
        for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) t.SetPixel(x, y, c);
    }

    static Texture2D MakeKey(int s)
    {
        var t = Blank(s, s);
        var gold = new Color(1f, 0.82f, 0.15f, 1f);
        // кольцо-головка
        for (int y = 0; y < s; y++)
            for (int x = 0; x < s; x++)
            {
                float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(s * 0.28f, s * 0.5f));
                if (d < s * 0.22f && d > s * 0.10f) t.SetPixel(x, y, gold);
            }
        Rect(t, (int)(s * 0.48f), (int)(s * 0.44f), (int)(s * 0.95f), (int)(s * 0.56f), gold); // стержень
        Rect(t, (int)(s * 0.72f), (int)(s * 0.26f), (int)(s * 0.80f), (int)(s * 0.44f), gold); // зубцы
        Rect(t, (int)(s * 0.86f), (int)(s * 0.30f), (int)(s * 0.94f), (int)(s * 0.44f), gold);
        t.Apply();
        return t;
    }

    static Texture2D MakeDoor(int w, int h, bool closed)
    {
        var t = Blank(w, h);
        if (closed)
        {
            Rect(t, 0, 0, w, h, new Color(0.45f, 0.28f, 0.12f, 1f));
            Rect(t, 3, 3, w - 3, h - 3, new Color(0.62f, 0.40f, 0.18f, 1f));
            Rect(t, w / 2 - 2, 0, w / 2 + 2, h, new Color(0.35f, 0.2f, 0.08f, 1f)); // створка по центру
        }
        else
        {
            // открытая: только рамка по бокам и прозрачный проём
            var frame = new Color(0.45f, 0.28f, 0.12f, 1f);
            Rect(t, 0, 0, 10, h, frame);
            Rect(t, w - 10, 0, w, h, frame);
        }
        t.Apply();
        return t;
    }
}

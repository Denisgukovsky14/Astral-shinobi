using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// Стартовое меню: чёрный фон, название сверху и одна кнопка-треугольник, которая запускает уровень.
// Нужно, чтобы игра не начиналась сразу при входе, а у каждого прогона был чёткий старт.
// Интерфейс строится в коде (обычный Text, кириллица не нужна, но шрифт тот же, что и на уровнях).
public class MainMenu : MonoBehaviour
{
    [Tooltip("Имя сцены уровня, которую запускает кнопка (она должна быть в Build Settings).")]
    public string levelScene = "Coordination_Testing_Scene";

    void Awake()
    {
        // Вернулись из уровня, где время могло стоять (победа/смерть).
        Time.timeScale = 1f;
        Time.fixedDeltaTime = 0.02f;
        BuildUI();
    }

    void BuildUI()
    {
        var canvasGo = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        var canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        var scaler = canvasGo.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0f;

        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        var titleGo = new GameObject("Title", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
        titleGo.transform.SetParent(canvasGo.transform, false);
        var rt = (RectTransform)titleGo.transform;
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 1f);
        rt.anchoredPosition = new Vector2(0f, -140f);
        rt.sizeDelta = new Vector2(1600f, 200f);
        var title = titleGo.GetComponent<Text>();
        title.font = font;
        title.fontSize = 140;
        title.alignment = TextAnchor.MiddleCenter;
        title.color = Color.white;
        title.text = "Astral Shinobi";
        title.raycastTarget = false;

        UIShapes.IconButton("Play", canvasGo.transform, UIShapes.Triangle(), new Vector2(0f, -60f), 260f, StartLevel);
    }

    void StartLevel()
    {
        SceneManager.LoadScene(levelScene);
    }
}

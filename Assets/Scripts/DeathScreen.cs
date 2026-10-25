using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// Экран смерти: затемнение, надпись «Вы погибли» и кнопка «Заново», которая перезапускает сцену.
// Висит на Canvas, интерфейс строится в коде. Показывается, когда PlayerHealth сообщает о смерти.
// Обычный (legacy) Text со встроенным шрифтом, потому что у шрифта TextMeshPro по умолчанию
// нет кириллицы.
public class DeathScreen : MonoBehaviour
{
    public PlayerHealth playerHealth;
    public string title = "Вы погибли";
    public string buttonLabel = "Заново";

    private GameObject root;

    void Awake()
    {
        BuildUI();
    }

    void Start()
    {
        if (playerHealth != null) playerHealth.Died += Show;
    }

    void OnDestroy()
    {
        if (playerHealth != null) playerHealth.Died -= Show;
    }

    void Show()
    {
        root.SetActive(true);
    }

    void Restart()
    {
        // Время при смерти было остановлено - возвращаем нормальное, иначе новая сцена стартует замороженной.
        Time.timeScale = 1f;
        Time.fixedDeltaTime = 0.02f;
        SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
    }

    void BuildUI()
    {
        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        root = new GameObject("DeathScreen", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        root.transform.SetParent(transform, false);
        var rootRt = (RectTransform)root.transform;
        rootRt.anchorMin = Vector2.zero;
        rootRt.anchorMax = Vector2.one;
        rootRt.offsetMin = rootRt.offsetMax = Vector2.zero;
        root.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.75f);

        MakeText("Title", rootRt, title, font, 120, new Vector2(0f, 120f), new Vector2(1400f, 200f));

        var btn = new GameObject("RestartButton", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
        btn.transform.SetParent(rootRt, false);
        var btnRt = (RectTransform)btn.transform;
        btnRt.anchoredPosition = new Vector2(0f, -100f);
        btnRt.sizeDelta = new Vector2(480f, 140f);
        btn.GetComponent<Image>().color = new Color(0.9f, 0.9f, 0.9f, 1f);
        btn.GetComponent<Button>().onClick.AddListener(Restart);
        MakeText("Label", btnRt, buttonLabel, font, 70, Vector2.zero, btnRt.sizeDelta).color = Color.black;

        root.SetActive(false);
    }

    static Text MakeText(string name, Transform parent, string content, Font font, int size, Vector2 pos, Vector2 dim)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        rt.anchoredPosition = pos;
        rt.sizeDelta = dim;
        var t = go.GetComponent<Text>();
        t.font = font;
        t.text = content;
        t.fontSize = size;
        t.alignment = TextAnchor.MiddleCenter;
        t.color = Color.white;
        t.raycastTarget = false;
        return t;
    }
}

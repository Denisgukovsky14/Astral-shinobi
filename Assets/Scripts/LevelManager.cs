using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// Ход уровня: последний убитый враг роняет ключ, ключ открывает выходную дверь (ExitDoor), за дверью
// следующий уровень. Висит на одном объекте в сцене. Пока на сцене есть живые враги (в том числе
// дремлющие), ключа нет. Если врагов на уровне изначально нет, дверь открыта сразу.
// Подсказки на экране и конечный экран строятся в коде под Canvas (обычный Text, кириллица работает).
public class LevelManager : MonoBehaviour
{
    public static LevelManager Instance { get; private set; }

    [Tooltip("Префаб ключа (Key.prefab). Его подбирает игрок, подойдя вплотную.")]
    public GameObject keyPrefab;

    [Tooltip("Выходная дверь уровня. Если не назначена, ищется ExitDoor на сцене.")]
    public ExitDoor door;

    [Tooltip("Имя сцены следующего уровня. Пусто - следующая сцена по порядку в Build Settings. Если следующей нет, показывается экран «конец».")]
    public string nextSceneName = "";

    [Tooltip("Сколько секунд пройти от входа в зону выхода до загрузки следующего уровня.")]
    public float exitDelay = 0.6f;

    public bool HasKey { get; private set; }
    public bool KeyDropped { get; private set; }
    // Врагов на уровне не было вообще: дверь открыта без ключа.
    public bool NoKeyNeeded { get; private set; }

    private Text hint;
    private Text keyLabel;
    private GameObject endScreen;
    private float hintTimer;
    private bool completing;

    void Awake()
    {
        Instance = this;
        BuildUI();
    }

    void OnEnable()
    {
        EnemyDeath.Died += OnEnemyDied;
    }

    void OnDisable()
    {
        EnemyDeath.Died -= OnEnemyDied;
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    void Start()
    {
        if (door == null) door = FindFirstObjectByType<ExitDoor>();
        if (EnemyDeath.Alive.Count == 0)
        {
            NoKeyNeeded = true;
            if (door != null) door.Open();
            Say("На уровне нет врагов - дверь открыта");
        }
        UpdateKeyLabel();
    }

    void Update()
    {
        if (hintTimer > 0f)
        {
            hintTimer -= Time.unscaledDeltaTime;
            if (hintTimer <= 0f) hint.text = "";
        }
    }

    void OnEnemyDied(Vector3 position)
    {
        if (KeyDropped || EnemyDeath.Alive.Count > 0) return;

        KeyDropped = true;
        GameObject key = keyPrefab != null ? Instantiate(keyPrefab, position, Quaternion.identity) : null;
        if (key == null) Debug.LogWarning("[LevelManager] keyPrefab не назначен - ключ не создан");
        Say("Последний враг пал - заберите ключ");
    }

    public void PickUpKey()
    {
        if (HasKey) return;
        HasKey = true;
        UpdateKeyLabel();
        Say("Ключ у вас - идите к двери");
    }

    public void NotifyDoorOpened()
    {
        Say("Дверь открыта");
    }

    // Игрок вошёл в зону выхода за открытой дверью.
    public void Complete()
    {
        if (completing) return;
        completing = true;
        StartCoroutine(CompleteRoutine());
    }

    IEnumerator CompleteRoutine()
    {
        Say("Уровень пройден");
        yield return new WaitForSecondsRealtime(exitDelay);

        string target = ResolveNextScene();
        if (target == null)
        {
            ShowEndScreen();
            yield break;
        }
        SceneManager.LoadScene(target);
    }

    // Имя/путь следующей сцены или null, если следующей нет.
    string ResolveNextScene()
    {
        if (!string.IsNullOrEmpty(nextSceneName)) return nextSceneName;

        int index = SceneManager.GetActiveScene().buildIndex;
        if (index < 0) return null; // текущая сцена не в Build Settings
        int next = index + 1;
        return next < SceneManager.sceneCountInBuildSettings ? SceneUtility.GetScenePathByBuildIndex(next) : null;
    }

    void Say(string message)
    {
        if (hint == null) return;
        hint.text = message;
        hintTimer = 3.5f;
    }

    void UpdateKeyLabel()
    {
        if (keyLabel == null) return;
        keyLabel.text = HasKey ? "Ключ: есть" : (NoKeyNeeded ? "" : "Ключ: нет");
        keyLabel.color = HasKey ? new Color(1f, 0.85f, 0.2f) : new Color(1f, 1f, 1f, 0.6f);
    }

    // --- Интерфейс ---

    void BuildUI()
    {
        Canvas canvas = FindFirstObjectByType<Canvas>();
        if (canvas == null) return;
        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        hint = NewText("LevelHint", canvas.transform, font, 54, new Vector2(0f, -170f), new Vector2(1400f, 80f), new Vector2(0.5f, 1f));
        hint.color = Color.white;
        keyLabel = NewText("KeyLabel", canvas.transform, font, 40, new Vector2(0f, -115f), new Vector2(600f, 60f), new Vector2(0.5f, 1f));

        endScreen = new GameObject("LevelEndScreen", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        endScreen.transform.SetParent(canvas.transform, false);
        var rt = (RectTransform)endScreen.transform;
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = rt.offsetMax = Vector2.zero;
        endScreen.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.8f);
        var title = NewText("Title", endScreen.transform, font, 100, new Vector2(0f, 100f), new Vector2(1400f, 200f), new Vector2(0.5f, 0.5f));
        title.text = "Игра пройдена";

        var btn = new GameObject("Again", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
        btn.transform.SetParent(endScreen.transform, false);
        var brt = (RectTransform)btn.transform;
        brt.anchoredPosition = new Vector2(0f, -100f);
        brt.sizeDelta = new Vector2(480f, 140f);
        btn.GetComponent<Image>().color = new Color(0.9f, 0.9f, 0.9f, 1f);
        btn.GetComponent<Button>().onClick.AddListener(() =>
        {
            Time.timeScale = 1f;
            SceneManager.LoadScene(SceneManager.GetActiveScene().path);
        });
        var label = NewText("Label", btn.transform, font, 70, Vector2.zero, brt.sizeDelta, new Vector2(0.5f, 0.5f));
        label.text = "Сначала";
        label.color = Color.black;
        endScreen.SetActive(false);
    }

    void ShowEndScreen()
    {
        if (endScreen != null) endScreen.SetActive(true);
    }

    static Text NewText(string name, Transform parent, Font font, int size, Vector2 pos, Vector2 dim, Vector2 anchor)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = rt.anchorMax = rt.pivot = anchor;
        rt.anchoredPosition = pos;
        rt.sizeDelta = dim;
        var t = go.GetComponent<Text>();
        t.font = font;
        t.fontSize = size;
        t.alignment = TextAnchor.MiddleCenter;
        t.color = Color.white;
        t.raycastTarget = false;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        return t;
    }
}

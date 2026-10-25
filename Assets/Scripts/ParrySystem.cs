using System;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

// Парирование. Когда удар врага должен пройти (а не просто враг замахнулся), а игрок смотрит на
// врага (враг попадает в конус перед игроком, по умолчанию 120°), время замирает и появляется
// мини-игра: шарик бегает по белой дуге туда-обратно-туда (passes проходов по passDuration
// секунд), а на дуге лежит зелёный квадратик. Тап по экрану, пока шарик внутри квадратика, -
// парирование: квадратик заливается зелёным (50%), урон не проходит. Тап мимо или шарик
// пробежал все проходы без тапа - урон проходит как обычно.
// Всё считается по нескалированному времени, потому что Time.timeScale на это время равен 0.
// После каждой мини-игры парирование уходит на кулдаун (cooldown); пока он идёт, удар проходит
// сразу. Значок скрещённых мечей в углу экрана показывает готовность: во время кулдауна он
// медленно проявляется жёлтым, а когда парирование снова доступно - сразу становится зелёным.
// Пока игрок целится и держит клинок, парировать нельзя: значок светится синим.
// Интерфейс строится в коде под Canvas (всё без raycast, чтобы не мешать джойстикам).
public class ParrySystem : MonoBehaviour
{
    public static ParrySystem Instance { get; private set; }
    public static bool Active => Instance != null && Instance.active;

    public Transform player;

    [Header("Правила")]
    [Tooltip("Ширина конуса перед игроком, внутри которого враг считается «на которого игрок смотрит».")]
    public float coneAngle = 120f;
    [Tooltip("Сколько секунд занимает один проход шарика по дуге.")]
    public float passDuration = 0.5f;
    [Tooltip("Сколько проходов делает шарик (туда, обратно, опять туда = 3).")]
    public int passes = 3;
    [Tooltip("Где на дуге (0 = левый конец, 1 = правый) стоит зелёный квадратик.")]
    [Range(0f, 1f)] public float targetT = 0.75f;
    [Tooltip("Половина ширины окна парирования в долях дуги: шарик в пределах targetT ± это значение - успех. Размер квадратика подгоняется под него.")]
    public float targetHalfWidth = 0.06f;
    [Tooltip("Кулдаун парирования после мини-игры, в секундах (реального времени).")]
    public float cooldown = 5f;
    [Tooltip("Сколько секунд показывается результат (заливка квадратика) перед возвратом времени.")]
    public float resultHoldTime = 0.35f;

    [Header("Внешний вид")]
    public float vignetteAlpha = 0.6f;
    public float vignetteFadeTime = 0.15f;
    [Tooltip("Спрайт виньетки. Если не задан, берётся спрайт виньетки прицеливания у TestScript.")]
    public Sprite vignetteSprite;
    [Tooltip("Значок готовности парирования: положение относительно правого верхнего угла и размер.")]
    public Vector2 iconPosition = new Vector2(-110f, -110f);
    public float iconSize = 110f;
    public Color cooldownColor = new Color(1f, 0.85f, 0.1f, 1f);
    public Vector2 panelPosition = new Vector2(0f, -130f);
    public Vector2 arcSize = new Vector2(640f, 90f);
    public Color lineColor = Color.white;
    public Color aimingColor = new Color(0.25f, 0.55f, 1f, 1f);
    public Color greenColor = new Color(0.3f, 1f, 0f, 1f);

    private bool active;
    private float elapsed;
    private bool resolved;
    private bool tapQueued;
    private float cooldownRemaining;
    private UnityEngine.UI.Image readyIcon;
    private TestScript playerControl;
    private bool success;
    private float resultTimer;
    private int beginFrame;
    private float savedTimeScale;
    private Action<bool> onResolved;

    private RectTransform panel;
    private UnityEngine.UI.Image vignette;
    private RectTransform ball;
    private RectTransform square;
    private UnityEngine.UI.Image squareFill;
    private Vector2 p0, p1, p2;

    void Awake()
    {
        Instance = this;
        BuildUI();
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    // Пробует запустить парирование для удара, который иначе прошёл бы. false - игрок смотрит не
    // на врага (или парирование уже идёт), и удар нужно засчитать сразу. true - мини-игра пошла,
    // onResolved(parried) вызовется, когда она закончится.
    public bool TryBegin(Vector2 enemyPoint, Action<bool> resolvedCallback)
    {
        if (active || player == null || cooldownRemaining > 0f || IsPlayerAiming() || PlayerHealth.IsDead) return false;

        Vector2 to = enemyPoint - (Vector2)player.position;
        if (to.sqrMagnitude > 0.0001f && Vector2.Angle(player.up, to) > coneAngle * 0.5f) return false;

        active = true;
        resolved = false;
        elapsed = 0f;
        beginFrame = Time.frameCount;
        onResolved = resolvedCallback;
        savedTimeScale = Time.timeScale;
        Time.timeScale = 0f;

        squareFill.color = new Color(greenColor.r, greenColor.g, greenColor.b, 0f);
        panel.gameObject.SetActive(true);
        UpdateBall(0f);
        return true;
    }

    void Update()
    {
        // Виньетка гаснет/появляется по реальному времени, в том числе уже после конца мини-игры.
        float targetAlpha = active ? vignetteAlpha : 0f;
        Color vc = vignette.color;
        vc.a = Mathf.MoveTowards(vc.a, targetAlpha, vignetteAlpha / Mathf.Max(vignetteFadeTime, 0.0001f) * Time.unscaledDeltaTime);
        vignette.color = vc;
        vignette.enabled = vc.a > 0.001f;

        if (!active && cooldownRemaining > 0f) cooldownRemaining = Mathf.Max(0f, cooldownRemaining - Time.unscaledDeltaTime);
        UpdateReadyIcon();

        if (!active) return;

        if (resolved)
        {
            resultTimer -= Time.unscaledDeltaTime;
            if (resultTimer <= 0f) Finish();
            return;
        }

        elapsed += Time.unscaledDeltaTime;
        float total = passDuration * passes;
        float t = BallT(Mathf.Min(elapsed, total));
        UpdateBall(t);

        // Тап в том же кадре, где началась мини-игра, не считается - это не реакция игрока.
        bool tapped = Time.frameCount != beginFrame && (tapQueued || (Pointer.current != null && Pointer.current.press.wasPressedThisFrame));
        tapQueued = false;
        if (tapped) Resolve(Mathf.Abs(t - targetT) <= targetHalfWidth);
        else if (elapsed >= total) Resolve(false);
    }

    // Программный тап (для кнопки на экране или тестов) - то же, что касание экрана.
    public void Tap() { tapQueued = true; }

    // Кулдаун: жёлтый значок проявляется от полной прозрачности до 100%; готово - сразу зелёный.
    bool IsPlayerAiming()
    {
        if (playerControl == null && player != null) playerControl = player.GetComponent<TestScript>();
        return playerControl != null && playerControl.IsAiming;
    }

    void UpdateReadyIcon()
    {
        if (readyIcon == null) return;
        if (!active && IsPlayerAiming()) { readyIcon.color = aimingColor; return; }
        if (cooldownRemaining > 0f || active)
        {
            float progress = cooldown > 0f ? 1f - cooldownRemaining / cooldown : 1f;
            readyIcon.color = new Color(cooldownColor.r, cooldownColor.g, cooldownColor.b, active ? 0f : progress);
        }
        else
        {
            readyIcon.color = greenColor;
        }
    }

    float BallT(float time)
    {
        int pass = Mathf.Min((int)(time / passDuration), passes - 1);
        float frac = Mathf.Clamp01((time - pass * passDuration) / passDuration);
        return pass % 2 == 0 ? frac : 1f - frac;
    }

    void Resolve(bool parried)
    {
        resolved = true;
        success = parried;
        resultTimer = resultHoldTime;
        if (parried) squareFill.color = new Color(greenColor.r, greenColor.g, greenColor.b, 0.5f);
    }

    void Finish()
    {
        active = false;
        panel.gameObject.SetActive(false);
        Time.timeScale = savedTimeScale;
        cooldownRemaining = cooldown;
        var cb = onResolved;
        onResolved = null;
        cb?.Invoke(success);
    }

    // --- Построение интерфейса ---

    Vector2 Bezier(float t)
    {
        float u = 1f - t;
        return u * u * p0 + 2f * u * t * p1 + t * t * p2;
    }

    void UpdateBall(float t)
    {
        ball.anchoredPosition = Bezier(t);
    }

    void BuildUI()
    {
        Canvas canvas = GetComponentInParent<Canvas>();
        Transform root = canvas != null ? canvas.transform : transform;

        if (vignetteSprite == null)
        {
            var ts = FindFirstObjectByType<TestScript>();
            if (ts != null && ts.aimingVignette != null)
            {
                var im = ts.aimingVignette.GetComponent<UnityEngine.UI.Image>();
                if (im != null) vignetteSprite = im.sprite;
            }
        }

        vignette = NewImage("ParryVignette", root, vignetteSprite);
        Stretch(vignette.rectTransform);
        vignette.color = new Color(1f, 1f, 1f, 0f);
        vignette.enabled = false;

        readyIcon = NewImage("ParryReadyIcon", root, MakeSwordsSprite(128));
        RectTransform ir = readyIcon.rectTransform;
        ir.anchorMin = ir.anchorMax = ir.pivot = new Vector2(1f, 1f);
        ir.anchoredPosition = iconPosition;
        ir.sizeDelta = new Vector2(iconSize, iconSize);
        readyIcon.color = greenColor;

        panel = new GameObject("ParryPanel", typeof(RectTransform)).GetComponent<RectTransform>();
        panel.SetParent(root, false);
        panel.anchorMin = panel.anchorMax = new Vector2(0.5f, 0.5f);
        panel.anchoredPosition = panelPosition;

        // Дуга: квадратичная кривая Безье, прогиб вверх.
        float hw = arcSize.x * 0.5f;
        p0 = new Vector2(-hw, 0f);
        p2 = new Vector2(hw, 0f);
        p1 = new Vector2(0f, arcSize.y * 2f);

        const int segments = 40;
        float arcLength = 0f;
        Vector2 prev = Bezier(0f);
        for (int i = 1; i <= segments; i++)
        {
            Vector2 cur = Bezier((float)i / segments);
            Line(prev, cur, 8f, lineColor, 1.5f);
            arcLength += Vector2.Distance(prev, cur);
            prev = cur;
        }

        // «Вилки» на концах дуги.
        AddFork(p0, (p0 - Bezier(0.02f)).normalized);
        AddFork(p2, (p2 - Bezier(0.98f)).normalized);

        // Зелёный квадратик (контур + заливка), повёрнутый вдоль касательной дуги.
        float side = Mathf.Max(8f, 2f * targetHalfWidth * arcLength);
        Vector2 tangent = (Bezier(Mathf.Min(1f, targetT + 0.01f)) - Bezier(Mathf.Max(0f, targetT - 0.01f))).normalized;
        float angle = Mathf.Atan2(tangent.y, tangent.x) * Mathf.Rad2Deg;

        square = new GameObject("ParryTarget", typeof(RectTransform)).GetComponent<RectTransform>();
        square.SetParent(panel, false);
        square.sizeDelta = new Vector2(side, side);
        square.anchoredPosition = Bezier(targetT);
        square.localRotation = Quaternion.Euler(0f, 0f, angle);

        squareFill = NewImage("Fill", square, MakeRoundedSprite(64, 12, 0));
        Stretch(squareFill.rectTransform);
        squareFill.color = new Color(greenColor.r, greenColor.g, greenColor.b, 0f);
        var outline = NewImage("Outline", square, MakeRoundedSprite(64, 12, 4));
        Stretch(outline.rectTransform);
        outline.color = greenColor;

        // Шарик.
        var ballImg = NewImage("ParryBall", panel, MakeCircleSprite(64));
        ball = ballImg.rectTransform;
        ball.sizeDelta = new Vector2(38f, 38f);
        ballImg.color = greenColor;

        panel.gameObject.SetActive(false);
    }

    void AddFork(Vector2 end, Vector2 outward)
    {
        foreach (float a in new[] { 40f, -40f })
        {
            Vector2 dir = Quaternion.Euler(0f, 0f, 180f + a) * outward;
            Line(end, end + dir * 32f, 8f, lineColor, 0f);
        }
    }

    void Line(Vector2 a, Vector2 b, float thickness, Color color, float extraLength)
    {
        var im = NewImage("Seg", panel, null);
        im.color = color;
        RectTransform rt = im.rectTransform;
        Vector2 d = b - a;
        rt.sizeDelta = new Vector2(d.magnitude + extraLength, thickness);
        rt.anchoredPosition = (a + b) * 0.5f;
        rt.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg);
    }

    static UnityEngine.UI.Image NewImage(string name, Transform parent, Sprite sprite)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(UnityEngine.UI.Image));
        go.transform.SetParent(parent, false);
        var im = go.GetComponent<UnityEngine.UI.Image>();
        im.sprite = sprite;
        im.raycastTarget = false;
        return im;
    }

    static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
    }

    // Процедурные спрайты, чтобы не зависеть от встроенных ресурсов редактора.
    static Sprite MakeCircleSprite(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
        float r = size * 0.5f;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(r, r));
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(r - d)));
            }
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
    }

    // Два скрещённых меча: клинок + гарда у каждого, рисуются по расстоянию до отрезков.
    static Sprite MakeSwordsSprite(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
        float m = size * 0.1f;
        Vector2 a0 = new Vector2(m, m), a1 = new Vector2(size - m, size - m);      // меч «/»
        Vector2 b0 = new Vector2(size - m, m), b1 = new Vector2(m, size - m);      // меч «\»
        float blade = size * 0.045f, guardHalf = size * 0.15f, guardW = size * 0.035f;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                Vector2 p = new Vector2(x + 0.5f, y + 0.5f);
                float d = Mathf.Min(SwordDist(p, a0, a1, blade, guardHalf, guardW), SwordDist(p, b0, b1, blade, guardHalf, guardW));
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(0.5f - d)));
            }
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
    }

    // Расстояние (в пикселях, отрицательное внутри) до меча: рукоять в `from`, остриё в `to`.
    static float SwordDist(Vector2 p, Vector2 from, Vector2 to, float bladeHalf, float guardHalf, float guardHalfW)
    {
        Vector2 dir = (to - from).normalized;
        Vector2 perp = new Vector2(-dir.y, dir.x);
        Vector2 guardCenter = from + (to - from) * 0.28f;
        float dBlade = SegDist(p, from, to) - bladeHalf;
        float dGuard = SegDist(p, guardCenter - perp * guardHalf, guardCenter + perp * guardHalf) - guardHalfW;
        return Mathf.Min(dBlade, dGuard);
    }

    static float SegDist(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
        return Vector2.Distance(p, a + ab * t);
    }

    // Скруглённый квадрат: border = 0 - заливка, иначе только контур толщиной border пикселей.
    static Sprite MakeRoundedSprite(int size, float radius, float border)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
        float half = size * 0.5f;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float px = Mathf.Abs(x + 0.5f - half) - (half - radius);
                float py = Mathf.Abs(y + 0.5f - half) - (half - radius);
                float dist = new Vector2(Mathf.Max(px, 0f), Mathf.Max(py, 0f)).magnitude + Mathf.Min(Mathf.Max(px, py), 0f) - radius;
                float a = Mathf.Clamp01(0.5f - dist);
                if (border > 0f) a *= Mathf.Clamp01(dist + border + 0.5f);
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
    }
}

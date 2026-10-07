using UnityEngine;

public class HealthBar : MonoBehaviour
{
    public float maxHealth = 100f;
    public float currentHealth = 100f;
    public float width = 0.5f;
    public float height = 0.08f;
    public Color backgroundColor = Color.black;
    public Color fillColor = Color.red;
    public Vector3 worldOffset = new Vector3(0f, 0.35f, 0f);
    public int sortingOrder = 10;
    public float visibleDuration = 2f;
    public float fadeOutDuration = 0.5f;

    private Transform fill;
    private Transform followTarget;
    private SpriteRenderer backgroundSr;
    private SpriteRenderer fillSr;
    private float lastHealth;
    private float visibleTimer;
    private float fadeTimer;
    private bool fadingOut;

    void Awake()
    {
        followTarget = transform.parent;
        lastHealth = currentHealth;

        // Арт-ассет не нужен - Texture2D.whiteTexture это встроенная белая текстура 1 пиксель,
        // всегда доступная, окрашивается для каждой полоски через SpriteRenderer.color.
        // pixelsPerUnit = размеру текстуры делает спрайт ровно 1x1 мировую единицу, поэтому
        // значения localScale ниже выражены в обычных мировых единицах, а не в почти
        // невидимой доле от стандартных 100 пикс/юнит.
        Sprite sprite = Sprite.Create(Texture2D.whiteTexture,
            new Rect(0, 0, Texture2D.whiteTexture.width, Texture2D.whiteTexture.height),
            new Vector2(0.5f, 0.5f), Texture2D.whiteTexture.width);

        // Обе полоски используют один sortingOrder, заметно выше всего, что может использовать
        // меч/оружие в руке - совпадение с порядком другого спрайта делает порядок отрисовки
        // сверху неоднозначным.
        backgroundSr = CreateBar("Background", sprite, backgroundColor, sortingOrder);
        backgroundSr.transform.localScale = new Vector3(width, height, 1f);

        fillSr = CreateBar("Fill", sprite, fillColor, sortingOrder + 1);
        fill = fillSr.transform;

        UpdateFill();
        SetAlpha(0f); // скрыта до первого попадания
    }

    void Update()
    {
        // Опрашивается каждый кадр (а не только в сеттере), чтобы достаточно было изменить
        // currentHealth в Инспекторе во время Play-режима и увидеть реакцию полоски - кода,
        // наносящего урон, который можно было бы вызвать, пока не существует.
        UpdateFill();
        UpdateVisibility();
    }

    void LateUpdate()
    {
        // Из-за того, что полоска дочерняя к владельцу (ради распространения enable/destroy),
        // её позиция и поворот обычно вычисляются из transform родителя - поэтому как только
        // враг поворачивается в сторону движения, полоска завертелась бы и оказалась сбоку
        // вместо того, чтобы оставаться сверху. Перепривязка обоих значений каждый кадр держит
        // её на фиксированном мировом смещении над владельцем независимо от его направления.
        if (followTarget == null) return;
        transform.position = followTarget.position + worldOffset;
        transform.rotation = Quaternion.identity;
    }

    SpriteRenderer CreateBar(string name, Sprite sprite, Color color, int order)
    {
        GameObject go = new GameObject(name, typeof(SpriteRenderer));
        go.transform.SetParent(transform, false);
        SpriteRenderer sr = go.GetComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.color = color;
        sr.sortingOrder = order;
        return sr;
    }

    void UpdateFill()
    {
        float pct = maxHealth > 0f ? Mathf.Clamp01(currentHealth / maxHealth) : 0f;
        // Масштабирование квада с пивотом в центре сжимает оба края к середине. Сдвиг вправо
        // на половину потерянной ширины фиксирует левый край, так что полоска убывает справа,
        // как обычная шкала здоровья, а не сжимается равномерно.
        fill.localScale = new Vector3(width * pct, height, 1f);
        fill.localPosition = new Vector3(-width / 2f + (width * pct) / 2f, 0f, 0f);
    }

    void UpdateVisibility()
    {
        if (currentHealth < lastHealth)
        {
            // Только что получили удар - сразу возвращаем полную видимость и перезапускаем
            // таймер удержания, даже если полоска была посреди затухания от прошлого удара.
            fadingOut = false;
            visibleTimer = visibleDuration;
            SetAlpha(1f);
        }
        lastHealth = currentHealth;

        if (!fadingOut)
        {
            if (visibleTimer <= 0f) return;
            visibleTimer -= Time.deltaTime;
            if (visibleTimer <= 0f)
            {
                fadingOut = true;
                fadeTimer = fadeOutDuration;
            }
            return;
        }

        fadeTimer -= Time.deltaTime;
        SetAlpha(fadeOutDuration > 0f ? Mathf.Clamp01(fadeTimer / fadeOutDuration) : 0f);
        if (fadeTimer <= 0f) fadingOut = false;
    }

    void SetAlpha(float alpha)
    {
        Color bc = backgroundColor; bc.a = backgroundColor.a * alpha;
        Color fc = fillColor; fc.a = fillColor.a * alpha;
        backgroundSr.color = bc;
        fillSr.color = fc;
    }
}

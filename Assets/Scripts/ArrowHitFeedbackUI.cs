using UnityEngine;
using TMPro;

// Тот же паттерн «опрос и вспышка», что и в KunaiHitFeedbackUI, только следит за
// Arrow.TotalHits - отдельный счётчик, так как стрелы - действительно другой тип снаряда.
public class ArrowHitFeedbackUI : MonoBehaviour
{
    public float flashDuration = 0.4f;

    private TextMeshProUGUI text;
    private int lastShown = -1;
    private Color normalColor;
    private float flashTimer;

    void Awake()
    {
        text = GetComponent<TextMeshProUGUI>();
        normalColor = text.color;
        text.text = "Arrow hits: " + Arrow.TotalHits;
        lastShown = Arrow.TotalHits;
    }

    void Update()
    {
        if (Arrow.TotalHits != lastShown)
        {
            lastShown = Arrow.TotalHits;
            text.text = "Arrow hits: " + lastShown;
            text.color = Color.red;
            flashTimer = flashDuration;
        }

        if (flashTimer > 0f)
        {
            flashTimer -= Time.deltaTime;
            if (flashTimer <= 0f) text.color = normalColor;
        }
    }
}

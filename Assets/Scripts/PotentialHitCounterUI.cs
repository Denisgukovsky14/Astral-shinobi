using UnityEngine;
using TMPro;

public class PotentialHitCounterUI : MonoBehaviour
{
    public EnemyAttackZone attackZone;
    public float flashDuration = 0.4f;

    private TextMeshProUGUI text;
    private int lastShown = -1;
    private Color normalColor;
    private float flashTimer;

    void Awake()
    {
        text = GetComponent<TextMeshProUGUI>();
        normalColor = text.color;
    }

    void Update()
    {
        // Привязан к тому ближнему врагу, который сейчас есть в сцене - остаётся не назначенным
        // (и этот счётчик просто молчит), когда в сцене только не-ближние враги.
        if (attackZone == null) return;

        if (attackZone.PotentialHitCount != lastShown)
        {
            lastShown = attackZone.PotentialHitCount;
            text.text = "Could've hit you: " + lastShown;
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

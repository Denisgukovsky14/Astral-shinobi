using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(Image))]
public class FadeImage : MonoBehaviour
{
    private Image image;
    private Coroutine activeFade;

    void Awake()
    {
        image = GetComponent<Image>();
    }

    public void FadeIn(float duration)
    {
        gameObject.SetActive(true);
        StartFade(image.color.a, 1f, duration, null);
    }

    public void FadeOut(float duration)
    {
        StartFade(image.color.a, 0f, duration, () => gameObject.SetActive(false));
    }

    void StartFade(float from, float to, float duration, Action onComplete)
    {
        if (activeFade != null) StopCoroutine(activeFade);
        activeFade = StartCoroutine(FadeRoutine(from, to, duration, onComplete));
    }

    IEnumerator FadeRoutine(float from, float to, float duration, Action onComplete)
    {
        Color c = image.color;
        float t = 0f;
        // Нескалированное время, чтобы затухание всегда занимало одинаковое реальное время
        // независимо от того, какой Time.timeScale сейчас использует эффект замедления при прицеливании.
        while (t < duration)
        {
            t += Time.unscaledDeltaTime;
            c.a = duration > 0f ? Mathf.Lerp(from, to, t / duration) : to;
            image.color = c;
            yield return null;
        }
        c.a = to;
        image.color = c;
        activeFade = null;
        onComplete?.Invoke();
    }
}

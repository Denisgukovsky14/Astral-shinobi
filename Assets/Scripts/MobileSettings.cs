using UnityEngine;

// Настройки запуска под телефон: на Android по умолчанию 30 FPS, а экран гаснет по таймеру.
// Срабатывает один раз при старте приложения, до загрузки первой сцены.
public static class MobileSettings
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Apply()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        QualitySettings.vSyncCount = 0; // иначе vSync перебивает targetFrameRate
        Application.targetFrameRate = 60;
        Screen.sleepTimeout = SleepTimeout.NeverSleep;
#endif
    }
}

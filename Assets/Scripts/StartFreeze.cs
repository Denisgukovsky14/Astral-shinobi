using UnityEngine;
using UnityEngine.InputSystem;

// Замораживает игру (Time.timeScale = 0) сразу после загрузки сцены и размораживает при первом
// действии игрока: любое касание/клик, нажатие клавиши или движение любого из джойстиков. Нужен,
// чтобы игрок не получал урон, пока управление ещё не у него. Висит на игроке; после
// разморозки выключает сам себя (перезапуск сцены сработает снова, так как сцена создаётся заново).
[DefaultExecutionOrder(-1500)]
public class StartFreeze : MonoBehaviour
{
    [Tooltip("Порог отклонения джойстика, с которого движение считается действием.")]
    public float joystickThreshold = 0.1f;

    private TestScript control;

    void Awake()
    {
        control = GetComponent<TestScript>();
        Time.timeScale = 0f;
    }

    void Update()
    {
        if (!PlayerActed()) return;

        Time.timeScale = 1f;
        enabled = false;
    }

    bool PlayerActed()
    {
        if (Pointer.current != null && Pointer.current.press.wasPressedThisFrame) return true;
        if (Keyboard.current != null && Keyboard.current.anyKey.wasPressedThisFrame) return true;
        if (control != null)
        {
            if (control.moveJoystick != null && control.moveJoystick.Direction.magnitude > joystickThreshold) return true;
            if (control.aimJoystick != null && control.aimJoystick.Direction.magnitude > joystickThreshold) return true;
        }
        return false;
    }
}

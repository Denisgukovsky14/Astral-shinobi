using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

[RequireComponent(typeof(Rigidbody2D))]
public class TestScript : MonoBehaviour
{
    public float moveSpeed = 4f;
    public Joystick moveJoystick;
    public Joystick aimJoystick;
    public GameObject knifePrefab;
    public float aimActivateThreshold = 0.15f;
    public float aimReleaseThreshold = 0.05f;
    public float holdDistance = 0.55f;
    public float aimTimeScale = 0.1f;

    [Tooltip("How long bullet-time holds at full aimTimeScale after aiming starts, in real (unscaled) seconds - not scaled game time, since the whole point is a fixed real-world duration regardless of how slow time itself currently is.")]
    public float aimSlowDuration = 1f;

    [Tooltip("How long it takes to ramp back up to normal speed once aimSlowDuration elapses, in real (unscaled) seconds. Runs on a fixed schedule from when aiming started - it recovers on time even if the player keeps holding aim well past aimSlowDuration + aimRecoverDuration.")]
    public float aimRecoverDuration = 1f;

    public GameObject aimingVignette;
    public float vignetteFadeDuration = 0.15f;
    public Button catchButton;
    public float catchRadius = 1f;

    // Игрок сейчас целится и держит клинок - в это время парирование недоступно.
    public bool IsAiming => heldKnife != null;

    private const float NormalFixedDeltaTime = 0.02f;

    private Rigidbody2D rb;
    private FadeImage aimingVignetteFader;
    private GameObject heldKnife;
    private Knife heldKnifeScript;
    private GameObject flyingKnife;
    private Knife flyingKnifeScript;
    private Vector2 lastAimDir;
    private bool aimWasActive;
    private bool knifeWasFlying;
    private float cooldownRemaining;
    private float aimSlowTimer;
    private bool vignetteFadeOutStarted;
    private bool aimFacingActive;
    private float aimFacingAngle;
    private bool wasAimFacing;
    // Куда игрок смотрел по ходу движения в последний раз: после броска он разворачивается обратно туда
    // (на телефоне взгляд всегда совпадает с направлением движения, кроме момента прицеливания).
    private float moveFacingAngle;

    void Awake()
    {
        rb = GetComponent<Rigidbody2D>();
        moveFacingAngle = rb.rotation;
        if (aimingVignette != null) aimingVignetteFader = aimingVignette.GetComponent<FadeImage>();
        if (catchButton != null) catchButton.onClick.AddListener(TryCatchKnife);
    }

    void Update()
    {
        // Пока идёт мини-игра парирования, время стоит и игрок ничего не делает.
        if (ParrySystem.Active || PlayerHealth.IsDead) return;

        UpdateKnifeCooldown();
        aimFacingActive = false;
        HandleAim();
        UpdateCatchButton();

        UpdateMovementAndFacing();

        // Пока игрок целится (держит джойстик броска и нож в руке), он смотрит в сторону прицела, а не по
        // ходу движения или за мышью: это важно для парирования (оно зависит от того, куда смотрит игрок).
        // Применяется ПОСЛЕ движения, поэтому перекрывает поворот по ходу; после броска остаётся как есть.
        if (aimFacingActive)
        {
            rb.rotation = aimFacingAngle;
        }
        else if (wasAimFacing)
        {
            // Прицеливание закончилось (бросок или отпустили): разворачиваем обратно по направлению движения.
            // Если левый джойстик зажат, поворот и так уже по ходу; если нет - возвращаем последнее направление.
            rb.rotation = moveFacingAngle;
        }
        wasAimFacing = aimFacingActive;
    }

    void UpdateMovementAndFacing()
    {
        Vector2 joystickDir = moveJoystick != null ? moveJoystick.Direction : Vector2.zero;
        bool useJoystick = joystickDir.sqrMagnitude > 0.01f;

        if (useJoystick)
        {
            rb.linearVelocity = joystickDir * moveSpeed;
            // Перед спрайта теперь смотрит в мировой +Y при повороте 0.
            float angle = Mathf.Atan2(joystickDir.y, joystickDir.x) * Mathf.Rad2Deg - 90f;
            rb.rotation = angle;
            moveFacingAngle = angle;
            return;
        }

        Keyboard keyboard = Keyboard.current;
        if (keyboard != null)
        {
            Vector2 move = Vector2.zero;
            if (keyboard.leftArrowKey.isPressed) move.x -= 1f;
            if (keyboard.rightArrowKey.isPressed) move.x += 1f;
            if (keyboard.upArrowKey.isPressed) move.y += 1f;
            if (keyboard.downArrowKey.isPressed) move.y -= 1f;
            rb.linearVelocity = move.normalized * moveSpeed;
        }

        Mouse mouse = Mouse.current;
        if (mouse != null && Camera.main != null)
        {
            Vector3 screenPos = mouse.position.ReadValue();
            screenPos.z = -Camera.main.transform.position.z;
            Vector2 mouseWorld = Camera.main.ScreenToWorldPoint(screenPos);
            Vector2 lookDir = mouseWorld - rb.position;
            if (lookDir.sqrMagnitude > 0.0001f)
            {
                // поворачиваем спрайт на 90 градусов, так как математически 90 - это направление вверх.
                float angle = Mathf.Atan2(lookDir.y, lookDir.x) * Mathf.Rad2Deg - 90f;
                rb.rotation = angle;
            }
        }
    }

    void HandleAim()
    {
        if (aimJoystick == null || knifePrefab == null) return;

        // Джойстик отдаёт направление относительно экрана (перетаскивание «вверх» - всегда (0,1),
        // как бы ни была повёрнута камера). Свободный обзор CameraFollow вращает камеру вокруг Z,
        // из-за чего «вверх по экрану» перестаёт совпадать с «вверх в мире» - поворот на
        // собственный угол Z камеры переводит перетаскивание в правильное мировое направление,
        // так что бросок всё равно летит туда, куда визуально прицелились, а не по осям до поворота.
        Vector2 dir = aimJoystick.Direction;
        if (Camera.main != null) dir = RotateVector(dir, Camera.main.transform.eulerAngles.z);

        // Гистерезис: однажды начав прицеливание, отпусканием считается только реальный возврат
        // в центр. Без этого джойстик, на один кадр проседающий почти в ноль при проходе по дуге
        // (или обычный дрожащий ввод), читался бы как «отпущен», мгновенно бросал удерживаемый
        // нож, затем на следующем кадре снова активировался и создавал новый.
        float magnitude = dir.magnitude;
        bool active = aimWasActive ? magnitude > aimReleaseThreshold : magnitude > aimActivateThreshold;

        // Одновременно существует только один нож - это один и тот же зачарованный клинок,
        // возвращающийся обратно, а не стопка метательных. Полностью игнорируем ввод прицеливания,
        // пока летящий нож не исчезнет. Сравнение с null работает, хотя сама ссылка не обнуляется:
        // Unity перегружает ==, так что уничтоженный объект сравнивается как равный null.
        if (flyingKnife != null || cooldownRemaining > 0f) active = false;

        if (active)
        {
            if (!aimWasActive)
            {
                aimSlowTimer = 0f;
                vignetteFadeOutStarted = false;
                if (aimingVignetteFader != null) aimingVignetteFader.FadeIn(vignetteFadeDuration);
            }

            // Замедление при прицеливании («bullet-time»): замедляет физику/анимацию (всё, что
            // использует Time.deltaTime), тогда как чтение джойстика/указателя в Update() идёт
            // каждый реальный кадр, поэтому само прицеливание никогда не ощущается вялым. Работает
            // по фиксированному расписанию в реальном времени с момента начала прицеливания, а не
            // зависит от того, как долго игрок его держит - полное замедление на aimSlowDuration,
            // затем плавный возврат к норме за aimRecoverDuration, и после этого остаётся нормальной
            // скорость, даже если игрок всё ещё целится. Именно Time.unscaledDeltaTime, потому что
            // эта длительность должна означать одинаковое реальное время независимо от того,
            // какой timeScale действует в данный момент.
            aimSlowTimer += Time.unscaledDeltaTime;
            float targetTimeScale;
            if (aimSlowTimer < aimSlowDuration)
            {
                targetTimeScale = aimTimeScale;
            }
            else if (aimSlowTimer < aimSlowDuration + aimRecoverDuration)
            {
                // Виньетка гаснет синхронно с возвратом timeScale, начиная в тот же момент, что и
                // возврат (а не при отпускании) - становится полностью прозрачной ровно когда время
                // заканчивает нормализоваться, по тому же фиксированному расписанию, что и сам
                // возврат timeScale, даже если игрок всё ещё держит прицел.
                if (!vignetteFadeOutStarted)
                {
                    vignetteFadeOutStarted = true;
                    if (aimingVignetteFader != null) aimingVignetteFader.FadeOut(aimRecoverDuration);
                }
                float t = (aimSlowTimer - aimSlowDuration) / aimRecoverDuration;
                targetTimeScale = Mathf.Lerp(aimTimeScale, 1f, t);
            }
            else
            {
                targetTimeScale = 1f;
            }
            // Множитель скорости игры (кнопки 0.5/0.75/1) действует поверх замедления прицеливания.
            targetTimeScale *= GameSpeed.Multiplier;
            Time.timeScale = targetTimeScale;
            Time.fixedDeltaTime = NormalFixedDeltaTime * targetTimeScale;

            if (heldKnife == null)
            {
                heldKnife = Instantiate(knifePrefab);
                heldKnifeScript = heldKnife.GetComponent<Knife>();
            }
            // Держится на «сфере» вокруг игрока в направлении текущего прицеливания - выглядит как
            // выставленный вперёд и готовый к броску, а не приклеенный к телу. (Направление движения
            // здесь не используется, так как оно не зависит от прицела.) Rigidbody2D симулируется
            // в мировых координатах и игнорирует перемещения родительского transform, поэтому
            // позицию удержания нужно синхронизировать с игроком каждый кадр. Если обычная точка
            // удержания оказывается за стеной (игрок стоит вплотную к ней), нож подтягивается
            // обратно на сторону комнаты, а не создаётся за пределами уровня.
            Vector2 holdOffset = dir.normalized * holdDistance;
            heldKnifeScript.SetHoldPosition(rb.position, holdOffset);
            lastAimDir = dir;
            if (dir.sqrMagnitude > 0.0001f)
            {
                aimFacingActive = true;
                aimFacingAngle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg - 90f;
            }
            heldKnifeScript.SetAimDirection(dir);
        }
        else if (aimWasActive)
        {
            Time.timeScale = GameSpeed.Multiplier;
            Time.fixedDeltaTime = NormalFixedDeltaTime * GameSpeed.Multiplier;
            // Нужно только если затухание по расписанию выше так и не успело стартовать (отпустили
            // рано, ещё внутри aimSlowDuration) - как только оно идёт или завершено, виньетка уже
            // гаснет/исчезла, и FadeOut попытался бы запустить StartCoroutine на GameObject,
            // который сам FadeOut мог только что деактивировать.
            if (aimingVignetteFader != null && !vignetteFadeOutStarted) aimingVignetteFader.FadeOut(vignetteFadeDuration);

            if (heldKnife != null)
            {
                // То же правило, о котором уже предупреждает скрытая траектория: бросок прямо в
                // стену так близко отскочил бы обратно почти мгновенно, поэтому он не считается
                // настоящим броском - нож отменяется, а не запускается.
                if (lastAimDir.sqrMagnitude > 0.01f && !heldKnifeScript.IsThrowBlocked)
                {
                    heldKnifeScript.Throw(lastAimDir);
                    flyingKnife = heldKnife;
                    flyingKnifeScript = heldKnifeScript;
                }
                else
                {
                    Destroy(heldKnife);
                }
                heldKnife = null;
            }
        }

        aimWasActive = active;
    }

    static Vector2 RotateVector(Vector2 v, float angleDeg)
    {
        float rad = angleDeg * Mathf.Deg2Rad;
        float cos = Mathf.Cos(rad);
        float sin = Mathf.Sin(rad);
        return new Vector2(v.x * cos - v.y * sin, v.x * sin + v.y * cos);
    }

    void UpdateKnifeCooldown()
    {
        // Ловит ситуацию, когда нож исчезает сам (попадание во врага, лимит отскоков, истечение
        // времени жизни) - ни одна из этих причин не сообщает TestScript напрямую, поэтому
        // отслеживается переход «летел -> исчез». Поимка (TryCatchKnife) уже сама обрабатывает
        // себя через EndKnifeFlight до этого, так что к этому моменту knifeWasFlying уже false,
        // и повторно читать уже по-настоящему null flyingKnifeScript не придётся.
        bool knifeFlyingNow = flyingKnife != null;
        if (knifeWasFlying && !knifeFlyingNow) EndKnifeFlight(flyingKnifeScript.ResultingCooldown);
        knifeWasFlying = knifeFlyingNow;

        if (cooldownRemaining > 0f) cooldownRemaining -= Time.unscaledDeltaTime;
    }

    void EndKnifeFlight(float cooldown)
    {
        cooldownRemaining = cooldown;
        flyingKnife = null;
        flyingKnifeScript = null;
        knifeWasFlying = false;
    }

    void UpdateCatchButton()
    {
        if (catchButton == null) return;
        catchButton.interactable = IsKnifeCatchable();
    }

    bool IsKnifeCatchable()
    {
        return flyingKnife != null && flyingKnifeScript.HasBounced
            && Vector2.Distance(rb.position, flyingKnifeScript.Position) <= catchRadius;
    }

    public void TryCatchKnife()
    {
        // Перепроверяем, а не доверяем состоянию interactable кнопки - клик и выход ножа из
        // радиуса поимки могут прийтись на один кадр, до обновления кнопки.
        if (!IsKnifeCatchable()) return;

        // Пойманный нож полностью пропускает кулдаун - поймать его обратно из воздуха уже
        // само по себе награда, а не промах.
        // Раньше было так -> float cooldown = flyingKnifeScript.ResultingCooldown;
        // Но, поскольку кд ножа при поимке должно быть чистый 0, сразу передаём ноль.
        Destroy(flyingKnife);
        
        EndKnifeFlight(0f);
    }
}

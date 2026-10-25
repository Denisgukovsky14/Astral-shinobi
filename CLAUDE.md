# Astral Shinobi — инструкции для Claude

2D top-down экшен про самурая (мобильный в первую очередь): игрок бросает отскакивающий нож, враги ходят по A*, есть парирование, здоровье и экран смерти. Разрабатывается соло, **живьём через Unity MCP** в открытом редакторе.

Личность и тон ассистента («Петька») задаются в `C:\Users\Admin\CLAUDE.md`, здесь только про проект.
**Текущее состояние, что сделано и что дальше — в [PROGRESS.md](PROGRESS.md). Читай его в начале сессии и обновляй в конце этапа.**

## Правила работы (важно)

- **Язык:** общение и все комментарии в коде — на русском. Идентификаторы — на английском.
- **Обсуждение ≠ реализация.** «Что думаешь», «расскажи», «ничего не делай» — только анализ и рекомендация, код не трогать, пока Денис явно не скажет делать.
- **Проверять в Play Mode, а не «должно работать».** Результат — числами (расстояния, счётчики, кадры). Что проверить не удалось — говорить прямо, без «готово, всё работает».
- **Простое лучше умного.** Если «умная» схема требует очередной заплатки при каждом тесте — откатиться к самой тупой версии, которая делает основное. Денис уже дважды откатывал переусложнённое (замах мечника, геометрия фланга AntiTricker).
- **Больше ~5 раундов «тест — заплатка» подряд — предложить структурно другой подход**, а не шестую заплатку. Новый фикс проверять самому синтетическим тестом, не просить Дениса прогонять.
- **Первопричина, а не симптом.** Сначала механизм (почему), потом фикс. Если фикс — компромисс, так и сказать.
- Перед удалением/перезаписью — посмотреть, что там лежит. Коммиты и пуш — только по просьбе Дениса.
- После теста, который временно менял поля/enabled у живых компонентов, явно вернуть значения и проверить в Edit Mode (выход из Play не гарантирует откат).

## Стек

- Unity **6000.2.6f2**, URP, **New Input System**, TextMeshPro, Joystick Pack (два экранных джойстика).
- **A\* Pathfinding Project (Free)** в `Assets/AstarPathfindingProject` — `GridGraph`, `AIPath`, `Seeker`, `AIDestinationSetter`. Лицензия подтверждена как free-for-commercial.
- Слои: `Obstacles` (маска 256: стены, колонны), `Player`, враги. Стены уровня — один `CompositeCollider2D` из тонких (~0.19) боксов.

## Соглашения кода

- «Перед» спрайта = **+Y при повороте 0**, поэтому везде `Mathf.Atan2(dy, dx) * Rad2Deg - 90f`. Лук (`BowVisual`) смотрит вправо, у него фиксированный локальный поворот +90°.
- Враги — **Kinematic** `Rigidbody2D` (Dynamic ломает руление AIPath, см. memory). Позицией владеет AIPath: правку `transform.position` он затирает. Телепорт — `ai.Teleport`, мелкие подталкивания — **`ai.Move`** (не Teleport каждый кадр, он обнуляет скорость).
- Визуал врага — дочерний `Visual` + `VisualRotationSmoother`, корень поворачивает AIPath.
- Тонкие физические запросы (`Linecast`/`Raycast` нулевой ширины) для проверки пролёта снаряда не годятся: использовать `CircleCast` с реальным радиусом и обернуть в `Physics2D.queriesStartInColliders = false` (сохранить/вернуть).
- Меню/UI, которые строятся в коде (`ParrySystem`, `DeathScreen`, `PlayerHealthBar`), не используют TMP: у шрифта TMP по умолчанию нет кириллицы, берётся legacy `Text` + `LegacyRuntime.ttf`.
- Публичные поля настроек, `[Tooltip]` по-русски. Новое поле на уже сериализованном объекте сцены получает дефолт из кода только один раз — потом значение хранится в сцене.

## Кто чем владеет (не нарушать — это источник большинства багов)

- `ai.canMove` / `ai.isStopped` пишут: `EnemyChaseStop` (остановка по касанию игрока), `EnemyAttackZone` (стоит на замахе), `AntiTricker` (рывок), Kunai/Archer (каждый кадр), `EnemyDeath`. Любой новый код **не должен** принудительно ставить `canMove = true`, не спросив владельца; есть явные хэнд-оффы (`IsWindingUp`, `IsCountering`, `CombatAgent.IsWithdrawing`).
- `Time.timeScale` пишут: `TestScript` (замедление при прицеливании), `ParrySystem` (0 на мини-игре), `StartFreeze` (0 до первого действия), `PlayerHealth` (0 при смерти), `DeathScreen` (возврат 1). Новый код проверяет `ParrySystem.Active` / `PlayerHealth.IsDead`.
- Цель `AIDestinationSetter` у мечника: игрок, либо точка ожидания от `CombatDirector` (если нет токена).

## Unity MCP — как работать

- `Unity_RunCommand`: класс `internal class CommandScript : IRunCommand`, метод `Execute(ExecutionResult result)`; `result.Log(...)`. Нет `System.Reflection`. Неоднозначность `Image` с namespace `Unity.AI...` — писать `UnityEngine.UI.Image`.
- Real-time `Update` между вызовами не тикает → симуляция **циклом `EditorApplication.Step()`** (dt = 0.02). После `Step` позиции врага/игрока читать из transform.
- Вход в Play: `EditorApplication.isPlaying = true`, подождать ~8–10 c (домен-релоад). Выход — `= false`.
- **Не править скрипты, пока идёт Play**: домен-релоад убивает A\*-граф («There are no graphs in the scene»). Сначала Stop, правка, ждать компиляцию (~20 c), `isCompiling == false`, потом Play.
- Правки скриптов Python-скриптом: файл писать через Write с `# -*- coding: utf-8 -*-` и запускать с `PYTHONIOENCODING=utf-8` (кириллица в stdin ломается).
- Изменения полей у инстансов префабов в сцене: `PrefabUtility.RecordPrefabInstancePropertyModifications` + `EditorSceneManager.MarkSceneDirty` + `SaveOpenScenes`; значения префаба — через `LoadPrefabContents`/`SaveAsPrefabAsset`.
- `GameObject.Find` не видит неактивные объекты; ссылки на объекты сцены префаб хранить не может (для этого `PlayerInjector`).
- **A\*-граф в редакторе:** после правки `GridGraph` сохранять `d.SetData(d.SerializeGraphs())` (один `SerializeGraphs()` ничего не сохраняет). Копия сцены унаследует старую сетку.
- Игрок в тестах: `TestScript` перезаписывает поворот мышью — в симуляции его выключать; `Rigidbody2D` игрока Kinematic, иначе снаряды его сносят; `StartFreeze` выключать; `ParrySystem.Instance.player = null`, чтобы мини-игра не мешала.
- Ввод через Input System из MCP не идёт (окно без фокуса): для парирования есть `ParrySystem.Tap()`.
- Классификатор разрешений иногда падает транзиентно («no verdict») — повторить вызов.

## Карта проекта

`Assets/Scripts` (основное):
- Игрок: `TestScript` (движение, прицел, нож, bullet-time), `Knife`, `PlayerHealth`, `PlayerHealthBar`, `DeathScreen`, `StartFreeze`, `ParrySystem`, `PlayerInjector`, `CameraFollow`.
- Враги: `EnemyChaseStop` (столкновения, туннелирование, остановка по касанию), `EnemyAttackZone` (удар мечника, замах, анимация меча), `AntiTricker` (контрудар по «кружению»), `KunaiFighter`, `ArcherFighter`, `Kunai`, `Arrow`, `EnemyDeath`, `EnemySeparation`, `HealthBar`, `VisualRotationSmoother`.
- Координация: `CombatDirector`, `CombatAgent`.
- HUD-отладка: `PotentialHitCounterUI`, `KunaiHitFeedbackUI`, `ArrowHitFeedbackUI`, `FadeImage`.

Сцены: `Prorotype` (старый уровень, без директора; враги в иерархии выключены), `Coordination_Testing_Scene` (комната для координации; **рабочая**), `SampleScene` (пустая). Обе игровые сцены в Build Settings (нужны для кнопки «Заново»).

Заметки Дениса по дизайну: `C:\Users\Admin\Desktop\Игра Astral Shinobi\` (дизайн-документ, чек-лист).
Память Claude: `C:\Users\Admin\.claude\projects\C--Users-Admin-Desktop-game-projects-Astral-shinobi\memory\` (AI/Pathfinding, стиль работы, кунайщик, координация) — часть старых записей про AntiTricker устарела, верить коду.

# PC Performance Analyzer — архитектура

## Слои и модули

```
PcAnalyzer.App (WPF, MVVM)            ← UI Layer, Settings Service, трей, автозапуск, логи
   │
   ├── PcAnalyzer.Core (net8.0, без зависимостей от Windows)
   │     Models/            TelemetrySample, GameSession, SessionSummary, SystemInfo, AppSettings
   │     Abstractions/      ITelemetryService, IFrameTimeProvider, IGameDetector, ISessionStore, ISystemInfoProvider, ISettingsService
   │     Services/          SessionRecorder (Session Recorder), LiveEventDetector
   │     Analysis/          PerformanceAnalyzer (Performance Analyzer + Report Generator)
   │       Rules/           Bottleneck Detection Engine — по правилу на класс
   │       Narration.cs     IDiagnosisNarrator + LocalNarrator (офлайн «ИИ-диагностика»)
   │       DropEventDetector, SessionComparer, FrameStatistics
   │     Export/            HtmlReportExporter, CsvExporter
   │
   ├── PcAnalyzer.Windows (net8.0-windows)
   │     TelemetryService   Hardware Monitoring Service (фоновый поток, слияние источников)
   │     Sensors/           PdhSystemSource (счётчики Windows), LhmSensorSource (LibreHardwareMonitor)
   │     Processes/         ProcessMonitor (Process Monitoring Service)
   │     Games/             GameDetectionService (Game Detection Service), GameCatalog
   │     Frames/            PresentMonProvider (FPS / Frametime Provider)
   │     Hardware/          SystemInfoProvider (WMI, реестр, дисплеи, план питания)
   │
   └── PcAnalyzer.Data (net8.0)
         SqliteSessionStore Database Service (SQLite WAL, retention)
```

Новый источник данных добавляется реализацией интерфейса (`IFrameTimeProvider`, новый `*Source` внутри `TelemetryService`) — анализатор и UI не меняются. Новое диагностическое правило — новый класс `IAnalysisRule` в `PerformanceAnalyzer.DefaultRules()`.

## Поток данных

1. `TelemetryService` раз в N мс (0,5–5 с, настраивается) на отдельном потоке с пониженным приоритетом опрашивает PDH, LHM, процесс игры, PresentMon → `TelemetrySample` + frame times → событие `Tick`.
2. `LiveMonitor` (UI) показывает значения и графики; `SessionRecorder` во время записи буферизует данные и каждые 5 с пакетно пишет в SQLite (при аварии теряется ≤ 5 с).
3. `GameDetectionService` раз в 3 с проверяет активное окно; игра должна держаться 3 проверки подряд → `GameStarted`; закрытие процесса → `GameExited`.
4. По завершении сессии данные читаются из БД, `PerformanceAnalyzer` строит `SessionSummary` + `SessionReport`, которые сохраняются в строке сессии.
5. При запуске приложения сессии со статусом `Recording` (аварийное завершение) закрываются как `Interrupted` и анализируются по сохранённым данным.

## Какие показатели доступны на Windows

| Показатель | Источник | Требования |
|---|---|---|
| Загрузка CPU общая и по потокам | PDH `Processor Information(*)\% Processor Time` | нет |
| Эффективная частота CPU | PDH `Processor Frequency × % Processor Performance` | нет |
| Температура / мощность CPU | LibreHardwareMonitor | права администратора + драйвер PawnIO; зависит от CPU |
| Загрузка GPU по движкам (3D/Copy/Compute/Video), по процессам | PDH `GPU Engine(*)` | WDDM 2.0+, любой вендор |
| VRAM (адаптер и процесс) | PDH `GPU Adapter/Process Memory` | WDDM 2.0+ |
| Температура, hot spot, частоты, мощность, вентиляторы GPU | LHM (NVAPI / ADL / Intel) | драйвер вендора; набор зависит от карты |
| RAM, commit, page inputs, pagefile | PDH `Memory`, `Paging File` | нет |
| Диск (активность самого загруженного, R/W, очередь), сеть | PDH `PhysicalDisk`, `Network Interface` | нет |
| CPU/RAM процесса игры | `System.Diagnostics.Process` | процессы с анти-читом могут быть недоступны |
| FPS, frametime, 1%/0.1% low | PresentMon (ETW Present-события) | PresentMon указан в настройках; админ или «Пользователи журналов производительности» |
| Сетевая задержка | — | не реализовано |

Недоступный показатель хранится как `null` и в UI/отчётах отображается как «—» / «нет данных», а в отчёте перечисляется в разделе «Ограничения данных».

## Диагностика

- Посэмпловая классификация: **GPU-bound** (GPU ≥ 95 %), **CPU-bound** (GPU < 85 % и ядро ≥ 90 % или CPU ≥ 85 %), **Capped** (стабильный FPS при запасе и GPU, и ядра CPU).
- Скачок времени кадра: `ft > max(2.5 × медиана, медиана + 8 мс)`; фриз: `ft ≥ 150 мс` (настраивается).
- 1% low = 1000 / P99 времени кадра (нужно ≥ 300 кадров), 0.1% low = 1000 / P99.9 (≥ 5000 кадров).
- Троттлинг подтверждается только сочетанием «температура ≥ порога» **и** «частота < 90 % от P90 частоты под нагрузкой».
- Нехватка VRAM/RAM подтверждается, только если скачки кадров чаще возникают в моменты заполненной памяти (RAM — вместе с чтением из файла подкачки).
- Компиляция шейдеров, power limit, деградация — только как гипотезы с низкой/средней уверенностью.
- **Оценка плавности** = `clamp(1%low/avg × 115 − скачков_в_минуту × 2 − фризы × 4, 0, 100)`.
- **Общая оценка** = `0.5 × min(FPS / цель, 1) × 100 + 0.5 × плавность − штрафы (15/7/2 за критич./значит./незначит. проблемы)`, цель = частота монитора (60–144).

## Хранение

SQLite `%LocalAppData%\PcAnalyzer\pcanalyzer.db` (WAL): `sessions` (метаданные + JSON снимка железа, итогов, отчёта), `telemetry` (JSON-строка на замер), `frames` (float32 blob на интервал), `events`. Retention: целые сессии старше N дней (0 — хранить всегда), сырые данные старше M дней, лимит размера БД (удаляются сырые данные самых старых сессий; отчёты остаются). Настройки — `settings.json`, логи — `logs\app-YYYYMMDD.log` (14 дней).

## Библиотеки и лицензии

| Библиотека | Лицензия | Коммерческое использование |
|---|---|---|
| LibreHardwareMonitorLib 0.9.6 | MPL-2.0 | да (копилефт только на изменённые файлы самой библиотеки) |
| Microsoft.Data.Sqlite, System.Management, Microsoft.Extensions.* | MIT | да |
| CommunityToolkit.Mvvm | MIT | да |
| PresentMon (не поставляется, указывается пользователем) | MIT | да |

Драйверы не поставляются. LHM 0.9.6 использует подписанный драйвер PawnIO (не уязвимый WinRing0); если его нет — температура CPU просто недоступна.

## Не реализовано (явно помечено в UI)

- Внешний AI API (интерфейс `IDiagnosisNarrator` готов, в настройках переключатель отключён).
- Нативный экспорт PDF (HTML-отчёт печатается в PDF из браузера), экспорт графиков отдельными картинками.
- Сетевая задержка, онлайн-база игр, установщик/подпись, автообновление.
- Признаки троттлинга из вендорских флагов (NVAPI perf-limit reasons) — сейчас троттлинг выводится по температуре и частотам.

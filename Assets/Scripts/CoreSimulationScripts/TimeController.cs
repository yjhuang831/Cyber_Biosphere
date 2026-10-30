using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;

public class TimeController : MonoBehaviour
{
    public Slider speedSlider; // Assign in Inspector
    public Text speedText;     // Assign in Inspector
    internal static long simDaysSinceStart = 0;

    private float lastSpeedBeforePause = 1f; // Track the speed before pausing

    public float simTick = 0.04f;        // simulation seconds per step
    private float targetTimeScale = 1f;
    private float simTimeScale = 1f;
    private float accumulator = 0f;
    [Header("Simulation Speed")]
    public float sliderMaxTimeScale = 200f;
    public int maxTicksPerFrame = 120;   // safety cap to prevent spiral of death
    public float minRegulatedTimeScale = 1f;

    [Header("Per-Subsystem Intervals (in sim seconds)")]
    private float environmentUpdateInterval = 0.5f;
    private float grassUpdateInterval = 0.4f;
    private float oxygenDiffusionInterval = 10f;
    private float pruneInterval = 30f; // Prune species every 30 real seconds
    private float visualUpdateInterval = 0.033f; // Refresh overlays at roughly 30 Hz without tying them to render FPS.
    private float dayNightInterval = 0.2f; // Update day-night cycle every 0.2 sim seconds

    [Header("Seeds")]
    [Tooltip("Germination steps per sim day, spaced evenly across the day. Independent of day length.")]
    [Min(1)] public int germinationStepsPerDay = 24;
    private float daySimTime = 0f;              // Sim seconds since the current day began (dawn)
    private int germinationStepsDoneToday = 0;

    [Header("Research Logging")]
    [Tooltip("Write daily research data to persistentDataPath/ResearchLogs (one folder per session).")]
    public bool enableResearchLogging = true;
    [Tooltip("Sim days between trait distribution snapshots. 0 = never.")]
    [Min(0)] public int traitSnapshotIntervalDays = 5;
    private ResearchLogger researchLogger;

    [Header("Per-Subsystem Timers (real-time only)")]
    private float pruneTimer = 0f;
    private float visualTimer = 0f;
    private float smoothRunTimer = 0f;
    /// <summary>
    /// Sim seconds since day 0's dawn. Derived from the day counter so it stays exact in long runs
    /// (a float accumulator stops advancing by 0.04 s ticks after ~1M sim seconds, about 4 sim years).
    /// </summary>
    internal double TotalSimulatedSeconds => simDaysSinceStart * (double)dayLength + daySimTime;
    private float autoSaveTimer = 0f;
    private float dayTimeAccumulator = 0f;
    private float switchAccumulator = 0f;
    private const int dayLength = 720; // Sim seconds in one day/night cycle = 24 sim hours (30 sim seconds per hour)
    private bool dayNightSwitch = true;
    internal static float fullDayLength = 0f;
    private bool isDay = true;
    internal static bool isNight = false;
    internal float noonTime = 16f; // Hours in a 24h day
    private float nightTime = 0f;  // Sim seconds
    private float noonTimeSeconds = 0f; // Sim seconds
    // [OPTIMIZED] Static cache for accumulated age to avoid repeated field access
    internal static long accumulatedAge = 0;
    private long simTickCount = 0;
    // [OPTIMIZED] Pre-calculated period ticks to avoid division in main loop
    private int environmentPeriodTicks = 1;
    private int grassPeriodTicks = 1;
    private int oxygenPeriodTicks = 1;
    private int dayNightPeriodTicks = 1;
    private bool oxygenPending = false;
    private long tickWrap = long.MaxValue;
    private const long MAX_DAY_COUNTER = 1_000_000_000L;

    [Header("Display Settings")]
    public Text fpsText;
    public Text timeText;
    public Text simTimeText;
    public Text simDate;
    public Text seedCountText;
    public Text plantCountText;
    [Header("Update Frequency")]
    public float updateInterval = 0.2f;  // Update display every 0.2 seconds

    // FPS tracking variables
    private float currentFps = 0f;
    private float timeSinceLastUpdate = 0f;

    [Header("References")]
    // references to your managers
    public EnvironmentManager environmentManager;
    public GrassManager grassManager;
    public SeedManager seedManager;

    [Header("Autosave")]
    public bool enableAutoSave = true;
    public float autoSaveIntervalSeconds = 300f; // 5 real minutes
    public Text autoSaveStatusText;
    public string autoSaveStatusMessage = "Autosaving...";
    public float autoSaveStatusHoldSeconds = 1f;

    [Header("Save Inputs")]
    public TMP_InputField manualSaveNameInputField;
    private bool isAutoSaving = false;

    public GameObject infoPanelRoot;

    void Awake()
    {
        // Statics survive scene reloads; reset them so a new simulation doesn't inherit the previous session's clock.
        // A queued snapshot overrides these in Start(). fullDayLength is left alone because SeasonManager.Awake may already have set it.
        simDaysSinceStart = 0;
        accumulatedAge = 0;
        isNight = false;
    }

    void Start()
    {
        Time.timeScale = 1f;
        UpdateLightDistribution();
        speedSlider.maxValue = Mathf.Max(speedSlider.maxValue, sliderMaxTimeScale);
        speedSlider.onValueChanged.AddListener(OnSliderValueChanged);
        speedSlider.SetValueWithoutNotify(1f);
        UpdateSpeedText(1f);
        SetPlaybackSpeed(1f);
        RecomputePeriodTicks();

        // Initialize display
        if (fpsText != null)
        {
            fpsText.text = "FPS: 0";
        }
        if (timeText != null)
        {
            timeText.text = "Sim Time: X1.0";
        }
        if (simTimeText != null)
        {
            simTimeText.text = "00:00:00";
        }
        if (simDate != null)
        {
            simDate.text = "0000/01/01";
        }
        SetAutoSaveStatus(false);

        // If the main menu queued a save file, apply it as soon as this scene is initialized.
        // The load rebuilds the map itself; otherwise make sure the default map exists before the first Update.
        bool loaded = SimulationLoadSystem.TryApplyPendingSnapshot(
            this,
            environmentManager,
            grassManager,
            seedManager);
        if (!loaded && environmentManager.mapManager != null)
        {
            environmentManager.mapManager.EnsureInitialized();
        }

        if (enableResearchLogging)
        {
            string source = loaded ? "load:" + SimulationLoadSystem.SimulationName : "new";
            researchLogger = ResearchLogger.StartRun(source, this, environmentManager, grassManager, seedManager, traitSnapshotIntervalDays);
        }
        else
        {
            ResearchCounters.TakeAndReset();
        }

        infoPanelRoot.SetActive(true);
    }

    void Update()
    {
        // Clear focus when mouse is released
        if (Input.GetMouseButtonUp(0))
        {
            ClearSliderFocus();
        }

        if (Input.GetKeyDown(KeyCode.Space))
        {
            TogglePause();
        }

        // Calculate FPS using unscaled delta time (not affected by Time.timeScale)
        float unscaledDeltaTime = Time.unscaledDeltaTime;
        currentFps = 1f / unscaledDeltaTime;

        // Update display at specified interval
        timeSinceLastUpdate += unscaledDeltaTime;
        visualTimer += unscaledDeltaTime;
        if (timeSinceLastUpdate >= updateInterval)
        {
            UpdateDisplay();
            timeSinceLastUpdate = 0f;
        }
        if (visualTimer >= visualUpdateInterval)
        {
            environmentManager.VisualUpdate();
            visualTimer = 0f;
        }

        if (enableAutoSave && !isAutoSaving)
        {
            autoSaveTimer += unscaledDeltaTime;
            if (autoSaveTimer >= autoSaveIntervalSeconds)
            {
                StartCoroutine(AutoSaveWithGuardRoutine());
            }
        }

        if (targetTimeScale <= 0.01f) return;   // paused

        if (simTimeScale > targetTimeScale)
        {
            // Smoothly reduce time scale if we're above target (e.g. after a performance hiccup)
            simTimeScale -= 1.5f;
            simTimeScale = Mathf.Clamp(simTimeScale, 0f, targetTimeScale);
        }

        int tickBudget = Mathf.Max(1, maxTicksPerFrame);
        float maxSimTimePerFrame = tickBudget * simTick;

        accumulator += unscaledDeltaTime * simTimeScale; // Convert real time to simulation time

        if (smoothRunTimer > 4f && simTimeScale < targetTimeScale)
        {
            // Smoothly increase time scale back to target after we've been running smoothly for a while
            simTimeScale += 0.5f;
            simTimeScale = Mathf.Clamp(simTimeScale, 0f, targetTimeScale);
        }

        int ticks = 0;
        while (accumulator >= simTick && ticks < tickBudget)
        {
            AdvanceSimTick();
            daySimTime += simTick;

            if (simTickCount % dayNightPeriodTicks == 0)
            {
                DayNightCycle(dayNightInterval);
            }

            RunDueGerminationSteps();

            if (simTickCount % oxygenPeriodTicks == 0)
            {
                oxygenPending = true;
            }

            if (simTickCount % environmentPeriodTicks == 0)
            {
                // Hydration/radiation only change with terrain or settings edits, which trigger their own recalculation.
                environmentManager.UpdateTileEnvironment(null, false, oxygenPending, environmentUpdateInterval, skipHydrationUpdate: true);
                oxygenPending = false;
            }

            if (simTickCount % grassPeriodTicks == 0)
            {
                grassManager.UpdateAllGrassInstances(grassUpdateInterval);
            }

            accumulator -= simTick;
            ticks++;
        }

        if (ticks >= tickBudget && accumulator >= simTick)
        {
            smoothRunTimer = 0f; // reset smooth run timer if we had to skip updates
            accumulator = Mathf.Min(accumulator, simTick * 0.5f);
            float sustainableScale = maxSimTimePerFrame / Mathf.Max(unscaledDeltaTime, 0.0001f);
            simTimeScale = Mathf.Clamp(
                Mathf.Min(simTimeScale, sustainableScale),
                Mathf.Min(minRegulatedTimeScale, targetTimeScale),
                targetTimeScale);
        }
        else if (smoothRunTimer < 6f)
        {
            smoothRunTimer += unscaledDeltaTime;
        }
    }

    public void OnSliderValueChanged(float value)
    {
        // Update playback speed whenever slider value changes (both drag and click)
        UpdateSpeedText(value);
        SetPlaybackSpeed(value);
    }

    void UpdateSpeedText(float value)
    {
        speedText.text = $"X{value:F1}"; // Display speed with 1 decimal place
    }

    void SetPlaybackSpeed(float speed)
    {
        float previousTargetTimeScale = targetTimeScale;
        targetTimeScale = speed;
        if (speed > 0.01f)
        {
            // Slider changes are intentional, so apply them immediately instead of waiting for recovery.
            simTimeScale = speed;
            smoothRunTimer = 0f;
        }
        else if (speed <= 0.01f)
        {
            // If the new speed is effectively zero, pause immediately
            if (previousTargetTimeScale > 0.01f)
            {
                lastSpeedBeforePause = previousTargetTimeScale;
            }
            simTimeScale = 0f;
            accumulator = 0f;
        }
    }

    public void TogglePause(bool lockCamera = false, bool unpauseIfAlreadyPaused = true)
    {
        if (targetTimeScale > 0.01f || simTimeScale > 0.001f)
        {
            // Pause: Save the requested speed and set time scale to 0
            lastSpeedBeforePause = targetTimeScale > 0.01f ? targetTimeScale : simTimeScale;
            targetTimeScale = 0f;
            simTimeScale = 0;
            accumulator = 0f;
            speedSlider.SetValueWithoutNotify(0); // Update slider to show paused state
            UpdateSpeedText(0); // Update speed text to show X0.0
        }
        else if (unpauseIfAlreadyPaused)
        {
            // Unpause: Restore the last requested speed
            targetTimeScale = lastSpeedBeforePause;
            simTimeScale = lastSpeedBeforePause;
            speedSlider.SetValueWithoutNotify(lastSpeedBeforePause); // Update slider to match
            UpdateSpeedText(lastSpeedBeforePause); // Update speed text to show restored speed
        }
        if (lockCamera)
        {
            CameraController.LockCamera();
            MapManager.DisableTileModification();
        }
    }

    public void SaveSimulationNow()
    {
        string inputName = manualSaveNameInputField != null ? manualSaveNameInputField.text : null;
        string fallbackName = "manual_save_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string finalName = string.IsNullOrWhiteSpace(inputName) ? fallbackName : inputName;
        SaveSimulationWithName(finalName);
    }

    public bool SaveSimulationWithName(string saveName)
    {
        SimulationLoadSystem.SetSimulationNameFromSaveName(saveName);

        bool saveSucceeded = SimulationSaveSystem.SaveManualSnapshot(
            this,
            environmentManager,
            grassManager,
            seedManager,
            saveName);
        if (!saveSucceeded)
        {
            SimulationLoadSystem.SetSimulationNameFromSaveName(string.Empty);
        }
        if (saveSucceeded)
        {
            autoSaveTimer = 0f;
        }
        return saveSucceeded;
    }

    private IEnumerator AutoSaveWithGuardRoutine()
    {
        isAutoSaving = true;

        float previousTargetTimeScale = targetTimeScale;
        float previousSimTimeScale = simTimeScale;
        bool wasPaused = previousTargetTimeScale <= 0.01f && previousSimTimeScale <= 0.01f;

        targetTimeScale = 0f;
        simTimeScale = 0f;

        SetAutoSaveStatus(true);
        yield return null;

        bool saveSucceeded = SimulationSaveSystem.SaveAutoSnapshot(
            this,
            environmentManager,
            grassManager,
            seedManager);

        if (saveSucceeded)
        {
            autoSaveTimer = 0f;
        }

        float holdSeconds = Mathf.Max(0f, autoSaveStatusHoldSeconds);
        if (holdSeconds > 0f)
        {
            yield return new WaitForSecondsRealtime(holdSeconds);
        }

        if (!wasPaused)
        {
            targetTimeScale = previousTargetTimeScale;
            simTimeScale = previousSimTimeScale;
        }

        SetAutoSaveStatus(false);
        isAutoSaving = false;
    }

    private void SetAutoSaveStatus(bool visible)
    {
        if (autoSaveStatusText == null)
            return;

        autoSaveStatusText.text = autoSaveStatusMessage;
        autoSaveStatusText.gameObject.SetActive(visible);
    }

    /// <summary>
    /// Restores clock state from a snapshot. Saves with an exact day phase (schema 8+) resume mid-day precisely;
    /// older saves reconstruct the phase from the light level.
    /// </summary>
    internal void ApplyLoadedTimeState(TimeState state, float loadedBaseLightLevel, bool hasExactDayPhase)
    {
        simDaysSinceStart = System.Math.Max(0L, state.simDaysSinceStart);
        accumulatedAge = System.Math.Max(0L, state.accumulatedAge);
        isNight = state.isNight;
        isDay = !state.isNight;
        float loadedNoonTimeHours = state.noonTimeHours;

        // Total simulated time is derived from the day counter and daySimTime, so the saved total isn't read back

        float clampedScale = 1f;
        simTimeScale = clampedScale;
        targetTimeScale = clampedScale;
        lastSpeedBeforePause = clampedScale;

        accumulator = 0f;
        autoSaveTimer = 0f;
        dayTimeAccumulator = 0f;
        smoothRunTimer = 0f;
        pruneTimer = 0f;
        visualTimer = 0f;
        simTickCount = 0;
        oxygenPending = false;
        dayNightSwitch = true;

        if (!float.IsNaN(loadedNoonTimeHours))
        {
            noonTime = Mathf.Clamp(loadedNoonTimeHours, 0f, 24f);
        }

        UpdateLightDistribution();
        if (hasExactDayPhase)
        {
            isDay = state.isDay;
            dayNightSwitch = state.dayNightSwitch;
            dayTimeAccumulator = Mathf.Max(0f, state.dayTimeAccumulator);
            switchAccumulator = Mathf.Max(0f, state.switchAccumulator);
            daySimTime = Mathf.Max(0f, state.daySimTime);
            germinationStepsDoneToday = Mathf.Max(0, state.germinationStepsDoneToday);
        }
        else
        {
            RecomputeDayNightSwitchAccumulator(loadedBaseLightLevel);
            // Legacy saves don't know how far into the day they were; skip germination for the rest of
            // the loaded day rather than bunching a whole day's steps into the remainder.
            daySimTime = 0f;
            germinationStepsDoneToday = Mathf.Max(1, germinationStepsPerDay);
        }

        if (speedSlider != null)
        {
            speedSlider.SetValueWithoutNotify(clampedScale);
        }
        if (speedText != null)
        {
            UpdateSpeedText(clampedScale);
        }
        if (simDate != null)
        {
            simDate.text = ConvertDaysToDate(simDaysSinceStart, 12, 30);
        }
    }

    /// <summary>
    /// Fills the clock fields of a snapshot TimeState (the save system owns the rest).
    /// </summary>
    internal void WriteTimeState(TimeState state)
    {
        state.simDaysSinceStart = simDaysSinceStart;
        state.accumulatedAge = accumulatedAge;
        state.isNight = isNight;
        state.isDay = isDay;
        state.totalSimulatedTimeSeconds = TotalSimulatedSeconds; // Informational (research tools); not read on load
        state.noonTimeHours = noonTime;
        state.dayNightSwitch = dayNightSwitch;
        state.dayTimeAccumulator = dayTimeAccumulator;
        state.switchAccumulator = switchAccumulator;
        state.daySimTime = daySimTime;
        state.germinationStepsDoneToday = germinationStepsDoneToday;
    }

    private void RecomputeDayNightSwitchAccumulator(float baseLightLevel)
    {
        float light = Mathf.Clamp01(baseLightLevel);
        if (fullDayLength <= 0.0001f)
        {
            switchAccumulator = 0f;
            return;
        }

        float cosine = Mathf.Clamp(1f - (2f * light), -1f, 1f);
        float radians = Mathf.Acos(cosine);
        float cycle = 24f * Mathf.PI / fullDayLength;
        if (cycle <= 0.0001f)
        {
            switchAccumulator = 0f;
            return;
        }

        switchAccumulator = radians / cycle;
    }

    void ClearSliderFocus()
    {
        // Clear focus from the slider
        EventSystem.current.SetSelectedGameObject(null);
    }

    private void UpdateDisplay()
    {
        // Update UI text if assigned
        if (fpsText != null)
        {
            float displayFps = currentFps;
            if (displayFps > 60f)
            {
                displayFps = 60f;
            }
            fpsText.text = $"FPS: {displayFps:F1}";
        }
        if (timeText != null)
        {
            timeText.text = $"Sim Time: X{simTimeScale:F1}"; // Example of showing sim time or day-night cycle
        }
        SetSimTimeText(TotalSimulatedSeconds, simTimeText);
        plantCountText.text = "#Plants: " + grassManager.plantCount.ToString();
        seedCountText.text = "#Seeds: " + seedManager.seedCount.ToString();
    }

    /// <summary>
    /// Gets current FPS.
    /// </summary>
    public float GetCurrentFPS()
    {
        return currentFps;
    }

    private static void SetSimTimeText(double totalSeconds, Text simTimeText)
    {
        long seconds = (long)System.Math.Round(totalSeconds); // long: int overflows after ~68 years of sim seconds

        long hours = seconds / 3600;
        long minutes = (seconds % 3600) / 60;
        long secs = seconds % 60;

        simTimeText.text = $"{hours:00}:{minutes:00}:{secs:00}";
    }

    private void DayNightCycle(float interval)
    {
        // Cheap; keeps the daylight/night split current if the ratio or season changes mid-day (e.g. from a slider)
        UpdateLightDistribution();

        // Advance day-night
        if (dayNightSwitch)
        {
            switchAccumulator += interval;
            environmentManager.baseLightLevel = Mathf.Clamp01((-0.5f) * Mathf.Cos(24 * Mathf.PI / fullDayLength * switchAccumulator) + 0.5f);
            if (environmentManager.baseLightLevel >= 1f)
            {
                dayNightSwitch = false;
                isDay = true; // Toggle day/night state
            }
            else if (environmentManager.baseLightLevel <= 0f)
            {
                dayNightSwitch = false;
                isDay = false; // Toggle day/night state
                isNight = true;
            }
        }
        else if (isDay)
        {
            dayTimeAccumulator += interval;
            if (dayTimeAccumulator >= noonTimeSeconds)
            {
                dayNightSwitch = true;
                dayTimeAccumulator = 0f;
            }
        }
        else
        {
            dayTimeAccumulator += interval;
            if (dayTimeAccumulator >= nightTime)
            {
                dayNightSwitch = true;
                dayTimeAccumulator = 0f;
                switchAccumulator = 0f; // Reset switch accumulator for smooth transition
                isNight = false;
                simDaysSinceStart = IncrementWithWrap(simDaysSinceStart, MAX_DAY_COUNTER);
                // Season progression is now player-driven (see SeasonManager coroutine)
                accumulatedAge = IncrementWithWrap(accumulatedAge, MAX_DAY_COUNTER); // Increment accumulated age at the end of each night
                FinishGerminationDay();
                seedManager.SeedDecay();
                grassManager.UpdateGenera();
                long completedDay = simDaysSinceStart > 0 ? simDaysSinceStart - 1 : MAX_DAY_COUNTER - 1;
                if (researchLogger != null)
                {
                    researchLogger.OnDayCompleted(completedDay);
                }
                else
                {
                    ResearchCounters.TakeAndReset(); // Keep counters per-day even when not logging
                }
                simDate.text = ConvertDaysToDate(simDaysSinceStart, 12, 30);
            }
        }
    }

    /// <summary>
    /// A day is always one full day/night cycle of 24 sim hours (dayLength sim seconds):
    /// dawn (1 h) + daylight hold + dusk (1 h) + night. The season sets the daylight hold (noonTime) and
    /// dayNightCycleSpeed scales it, so both only shift the daylight/night split, never the day's length.
    /// </summary>
    internal void UpdateLightDistribution()
    {
        fullDayLength = dayLength;
        float hourLength = fullDayLength / 24f;
        float transitionHours = 2f; // Dawn and dusk ramps, one hour each (see DayNightCycle's cosine)
        float daylightHoldHours = Mathf.Clamp(noonTime * Mathf.Max(0f, environmentManager.dayNightCycleSpeed), 0f, 24f - transitionHours);
        noonTimeSeconds = daylightHoldHours * hourLength;
        nightTime = Mathf.Max(0f, fullDayLength - noonTimeSeconds - transitionHours * hourLength);
    }

    private void RecomputePeriodTicks()
    {
        environmentPeriodTicks = Mathf.Max(1, Mathf.RoundToInt(environmentUpdateInterval / simTick));
        grassPeriodTicks = Mathf.Max(1, Mathf.RoundToInt(grassUpdateInterval / simTick));
        oxygenPeriodTicks = Mathf.Max(1, Mathf.RoundToInt(oxygenDiffusionInterval / simTick));
        dayNightPeriodTicks = Mathf.Max(1, Mathf.RoundToInt(dayNightInterval / simTick));
        tickWrap = ComputeTickWrap(environmentPeriodTicks, grassPeriodTicks, oxygenPeriodTicks, dayNightPeriodTicks);
    }

    /// <summary>
    /// Runs germination steps due so far today: step k of N happens once k/N of the day has elapsed.
    /// </summary>
    private void RunDueGerminationSteps()
    {
        int stepsPerDay = Mathf.Max(1, germinationStepsPerDay);
        float cycleLength = Mathf.Max(fullDayLength, simTick);
        int due = Mathf.Min(stepsPerDay, Mathf.FloorToInt(daySimTime / cycleLength * stepsPerDay));
        while (germinationStepsDoneToday < due)
        {
            seedManager.GerminationStep();
            germinationStepsDoneToday++;
        }
    }

    /// <summary>
    /// Called at dawn: tops up any step lost to rounding so each day gets exactly germinationStepsPerDay, then starts a new day.
    /// </summary>
    private void FinishGerminationDay()
    {
        int stepsPerDay = Mathf.Max(1, germinationStepsPerDay);
        while (germinationStepsDoneToday < stepsPerDay)
        {
            seedManager.GerminationStep();
            germinationStepsDoneToday++;
        }
        germinationStepsDoneToday = 0;
        daySimTime = 0f;
    }

    private void AdvanceSimTick()
    {
        simTickCount++;
        if (simTickCount >= tickWrap)
        {
            simTickCount = 0;
        }
    }

    private static long ComputeTickWrap(params int[] periods)
    {
        long lcm = 1;
        for (int i = 0; i < periods.Length; i++)
        {
            int period = periods[i];
            if (period <= 0)
                continue;
            lcm = Lcm(lcm, period);
        }
        if (lcm <= 0)
        {
            return long.MaxValue;
        }
        return lcm;
    }

    private static long Lcm(long a, long b)
    {
        if (a == 0 || b == 0)
            return 0;
        long gcd = Gcd(a, b);
        return (a / gcd) * b;
    }

    private static long Gcd(long a, long b)
    {
        while (b != 0)
        {
            long t = a % b;
            a = b;
            b = t;
        }
        return a;
    }

    private static long IncrementWithWrap(long value, long wrapAt)
    {
        if (wrapAt <= 0)
            return value + 1;
        value++;
        if (value >= wrapAt)
        {
            return 0;
        }
        return value;
    }

    private static string ConvertDaysToDate(long totalDays, int monthsPerYear, int daysPerMonth)
    {
        long daysPerYear = (long)monthsPerYear * daysPerMonth;

        // Year
        long year = totalDays / daysPerYear;

        // Remaining days inside the current year
        long dayOfYear = totalDays % daysPerYear;

        // Month
        long month = dayOfYear / daysPerMonth;

        // Day
        long day = dayOfYear % daysPerMonth;

        // Convert to 1-based indexing for display
        return $"{year:D4}/{month + 1:D2}/{day + 1:D2}";
    }

    public void SetMaxAutosaveFiles(int newLimit)
    {
        SimulationSnapshotConstants.MaxAutosaveFiles = Mathf.Max(1, newLimit);
    }
}

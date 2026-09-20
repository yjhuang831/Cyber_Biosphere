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
    private float visualUpdateInterval = 0.02f; // Update visuals every 0.02 real seconds
    private float dayNightInterval = 0.2f; // Update day-night cycle every 0.2 sim seconds
    private float seasonUpdateInterval = 30f; // Check for season change every 30 sim seconds

    [Header("Per-Subsystem Timers (real-time only)")]
    private float pruneTimer = 0f;
    private float visualTimer = 0f;
    private float smoothRunTimer = 0f;
    private float totalSimulatedTime = 0f; // Track total simulated time for display
    private float autoSaveTimer = 0f;
    private float dayTimeAccumulator = 0f;
    private float switchAccumulator = 0f;
    private int dayLength = 720; // Length of a full day-night cycle in
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
    private int seasonPeriodTicks = 1;
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

    void Start()
    {
        Time.timeScale = 1f;
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
        SimulationLoadSystem.TryApplyPendingSnapshot(
            this,
            environmentManager,
            grassManager,
            seedManager);

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

            if (simTickCount % dayNightPeriodTicks == 0)
            {
                DayNightCycle(dayNightInterval);
            }

            if (simTickCount % seasonPeriodTicks == 0)
            {
                seedManager.HourlySeedUpdate();
            }

            if (simTickCount % oxygenPeriodTicks == 0)
            {
                oxygenPending = true;
            }

            if (simTickCount % environmentPeriodTicks == 0)
            {
                environmentManager.UpdateTileEnvironment(null, false, oxygenPending, environmentUpdateInterval);
                oxygenPending = false;
            }

            if (simTickCount % grassPeriodTicks == 0)
            {
                grassManager.UpdateAllGrassInstances(grassUpdateInterval);
            }

            accumulator -= simTick;
            ticks++;
        }
        totalSimulatedTime += ticks * simTick; // Track processed simulation time for display

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
            totalSimulatedTime,
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
            seedManager,
            totalSimulatedTime);

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

    internal void ApplyLoadedTimeState(
        long loadedSimDaysSinceStart,
        long loadedAccumulatedAge,
        bool loadedIsNight,
        float loadedTotalSimulatedTimeSeconds,
        float loadedNoonTimeHours,
        float loadedBaseLightLevel)
    {
        simDaysSinceStart = System.Math.Max(0L, loadedSimDaysSinceStart);
        accumulatedAge = System.Math.Max(0L, loadedAccumulatedAge);
        isNight = loadedIsNight;
        isDay = !loadedIsNight;

        totalSimulatedTime = Mathf.Max(0f, loadedTotalSimulatedTimeSeconds);

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
        RecomputeDayNightSwitchAccumulator(loadedBaseLightLevel);

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
        SetSimTimeText(totalSimulatedTime, simTimeText);
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

    private static void SetSimTimeText(float totalSeconds, Text simTimeText)
    {
        int seconds = Mathf.RoundToInt(totalSeconds);

        int hours = seconds / 3600;
        int minutes = (seconds % 3600) / 60;
        int secs = seconds % 60;

        simTimeText.text = $"{hours:00}:{minutes:00}:{secs:00}";
    }

    private void DayNightCycle(float interval)
    {
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
                seedManager.SeedDecay();
                simDate.text = ConvertDaysToDate(simDaysSinceStart, 12, 30);
            }
        }
    }

    internal void UpdateLightDistribution()
    {
        fullDayLength = dayLength * environmentManager.dayNightCycleSpeed;
        float clampedNoonHours = Mathf.Clamp(noonTime, 0f, 24f);
        noonTimeSeconds = clampedNoonHours * fullDayLength / 24f;
        nightTime = fullDayLength - noonTimeSeconds - fullDayLength / 12f;
        if (nightTime < 0f)
        {
            nightTime = 0f;
        }
    }

    private void RecomputePeriodTicks()
    {
        environmentPeriodTicks = Mathf.Max(1, Mathf.RoundToInt(environmentUpdateInterval / simTick));
        grassPeriodTicks = Mathf.Max(1, Mathf.RoundToInt(grassUpdateInterval / simTick));
        oxygenPeriodTicks = Mathf.Max(1, Mathf.RoundToInt(oxygenDiffusionInterval / simTick));
        dayNightPeriodTicks = Mathf.Max(1, Mathf.RoundToInt(dayNightInterval / simTick));
        seasonPeriodTicks = Mathf.Max(1, Mathf.RoundToInt(seasonUpdateInterval / simTick));
        tickWrap = ComputeTickWrap(environmentPeriodTicks, grassPeriodTicks, oxygenPeriodTicks, dayNightPeriodTicks, seasonPeriodTicks);
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

using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public class SimulationSettingsPanelController : MonoBehaviour
{
    [Header("Debug")]
    public bool enableDebugLogs = true;

    [Header("Scene Behavior")]
    public string simulationSceneName = "SimulationScene";

    [Header("Local Settings - Autosave")]
    public Toggle autoSaveToggle;
    public bool autoSaveEnabled = true;

    public TMP_Dropdown autoSavePeriodDropdown;
    public float[] autoSavePeriodOptionsSeconds = { 60f, 180f, 300f, 600f, 900f, 1800f };
    public float autoSavePeriodSeconds = 300f;

    public TMP_Dropdown maxAutosaveFilesDropdown;
    public int[] maxAutosaveFileOptions = { 3, 5, 10, 20, 50 };
    public int maxAutosaveFiles = 10;

    [Header("Local Settings - Display")]
    public Toggle fullScreenToggle;
    public bool fullScreen = true;
    public FullScreenMode fullScreenMode = FullScreenMode.FullScreenWindow;

    [Header("Simulation Settings - Seeds")]
    public TMP_Dropdown maxSeedsPerTileDropdown;
    public int[] maxSeedsPerTileOptions = { 0, 1, 3, 5, 10, 20, 50 };
    public int maxSeedsPerTile = 0;

    [Header("Simulation Settings - Plants")]
    public Toggle plantAgingToggle;
    public bool plantAging = true;

    public Slider agingRateSlider;
    public TMP_Text agingRateValueText;
    public float agingRateScalar = 0.05f;
    public float agingRate = 0.05f;

    public Slider metabolismCostPerAlleleSlider;
    public TMP_Text metabolismCostPerAlleleValueText;
    public float metabolismCostPerAlleleScalar = 0.005f;
    public float metabolismCostPerAllele = 0.01f;

    public Slider baseGrowthCostSlider;
    public TMP_Text baseGrowthCostValueText;
    public float baseGrowthCostScalar = 0.1f;
    public float baseGrowthCost = 0.5f;

    public TMP_Dropdown flowerLimitDropdown;
    public int[] flowerLimitOptions = { 1, 2, 3, 5, 10 };
    public int flowerLimit = 1;

    [Header("References")]
    public GameObject localSettingsPanel;
    public GameObject simSettingsPanel;
    public GameObject controlsPanel;
    public GameObject simSettingsToggle;

    private void OnEnable()
    {
        PanelDefault();
    }

    private void OnDisable()
    {
        SharedUIPanelManager.Instance.SavePlayerPreferences();
    }

    public void SetMaxAutosaveFilesFromDropdown(int optionIndex)
    {
        int resolvedIndex = GetDropdownCurrentIndex(maxAutosaveFilesDropdown, optionIndex);
        int value = GetIntDropdownValue(maxAutosaveFilesDropdown, maxAutosaveFileOptions, resolvedIndex, maxAutosaveFiles);
        LogDropdownResolved("Max autosave files dropdown", maxAutosaveFilesDropdown, optionIndex, resolvedIndex, value);
        SetMaxAutosaveFiles(value);
    }

    public void SetAutoSaveEnabled(bool enabled)
    {
        autoSaveEnabled = enabled;
        LogSettingChanged("Autosave enabled", autoSaveEnabled);
        ApplyAutoSaveSettingsToActiveTimeController();
    }

    public void SetAutoSavePeriodFromDropdown(int optionIndex)
    {
        int resolvedIndex = GetDropdownCurrentIndex(autoSavePeriodDropdown, optionIndex);
        float value = GetFloatDropdownValue(autoSavePeriodDropdown, autoSavePeriodOptionsSeconds, resolvedIndex, autoSavePeriodSeconds);
        LogDropdownResolved("Autosave period dropdown", autoSavePeriodDropdown, optionIndex, resolvedIndex, value + "s");
        SetAutoSavePeriod(value);
    }

    public void SetAutoSavePeriod(float seconds)
    {
        autoSavePeriodSeconds = Mathf.Max(1f, seconds);
        LogSettingChanged("Autosave period", autoSavePeriodSeconds + "s");
        ApplyAutoSaveSettingsToActiveTimeController();
    }

    public void SetMaxAutosaveFiles(int newLimit)
    {
        maxAutosaveFiles = Mathf.Max(1, newLimit);
        SimulationSnapshotConstants.MaxAutosaveFiles = maxAutosaveFiles;
        SimulationSaveSystem.PruneAutosavesToCurrentLimit();
        LogSettingChanged("Max autosave files", maxAutosaveFiles);
    }

    public void SetFullScreen(bool enabled)
    {
        fullScreen = enabled;
        FullScreenMode targetMode = fullScreen ? fullScreenMode : FullScreenMode.Windowed;
        Screen.SetResolution(Screen.currentResolution.width, Screen.currentResolution.height, targetMode);
        LogSettingChanged(
            "Fullscreen",
            "requested=" + fullScreen
            + ", targetMode=" + targetMode
            + ", actualFullScreen=" + Screen.fullScreen
            + ", actualMode=" + Screen.fullScreenMode
            + ", resolution=" + Screen.width + "x" + Screen.height);
        if (isActiveAndEnabled)
        {
            StartCoroutine(LogFullScreenStateAfterFrame());
        }
        else
        {
            LogSettingChanged("Fullscreen after-frame log", "skipped; settings panel is inactive");
        }

#if UNITY_EDITOR
        if (fullScreen)
        {
            Debug.Log("[SimulationSettings] Fullscreen changes may not be visible in the Unity Editor. Test this in a player build.");
        }
#endif
    }

    public void SetMaxSeedsPerTileFromDropdown(int optionIndex)
    {
        int resolvedIndex = GetDropdownCurrentIndex(maxSeedsPerTileDropdown, optionIndex);
        SetMaxSeedsPerTile(GetIntDropdownValue(maxSeedsPerTileDropdown, maxSeedsPerTileOptions, resolvedIndex, maxSeedsPerTile));
    }

    public void SetMaxSeedsPerTile(int newLimit)
    {
        maxSeedsPerTile = Mathf.Max(0, newLimit);

        SeedManager seedManager = GetActiveSeedManager();
        if (seedManager != null)
        {
            seedManager.SetMaxSeedsPerTile(maxSeedsPerTile);
        }
    }

    public void SetPlantAging(bool enabled)
    {
        plantAging = enabled;

        GrassManager grassManager = GetActiveGrassManager();
        if (grassManager != null)
        {
            grassManager.SetPlantAging(plantAging);
        }
    }

    public void SetAgingRate(float unscaledNewRate)
    {
        float newRate = unscaledNewRate * agingRateScalar;
        agingRate = newRate;
        GrassManager grassManager = GetActiveGrassManager();
        if (grassManager != null)
        {
            grassManager.SetAgingRate(agingRate);
            agingRateValueText.text = agingRate.ToString("F2") + " J/s/day";
        }
    }

    public void SetMetabolismCostPerAllele(float unscaledNewCost)
    {
        float newCost = unscaledNewCost * metabolismCostPerAlleleScalar;
        metabolismCostPerAllele = newCost;
        GrassManager grassManager = GetActiveGrassManager();
        if (grassManager != null)
        {
            grassManager.SetMetabolismCostPerAllele(metabolismCostPerAllele);
            metabolismCostPerAlleleValueText.text = metabolismCostPerAllele.ToString("F3") + " J/s/allele";
        }
    }

    public void SetBaseGrowthCost(float unscaledNewCost)
    {
        float newCost = unscaledNewCost * baseGrowthCostScalar;
        baseGrowthCost = newCost;
        GrassManager grassManager = GetActiveGrassManager();
        if (grassManager != null)
        {
            grassManager.SetBaseGrowthCost(baseGrowthCost);
            baseGrowthCostValueText.text = baseGrowthCost.ToString("F2") + " J/s";
        }
    }

    public void SetFlowerLimitFromDropdown(int optionIndex)
    {
        int resolvedIndex = GetDropdownCurrentIndex(flowerLimitDropdown, optionIndex);
        SetFlowerLimit(GetIntDropdownValue(flowerLimitDropdown, flowerLimitOptions, resolvedIndex, flowerLimit));
    }

    public void SetFlowerLimit(int newLimit)
    {
        flowerLimit = Mathf.Max(1, newLimit);

        GrassManager grassManager = GetActiveGrassManager();
        if (grassManager != null)
        {
            grassManager.SetFlowerLimit(flowerLimit);
        }
    }

    public void ApplyAllSettings()
    {
        ApplyLocalSettings();

        if (IsSimulationScene(SceneManager.GetActiveScene()))
        {
            ApplySimulationSettings();
        }
    }

    public void ApplyLocalSettings()
    {
        LogLocalSettings("ApplyLocalSettings");
        ApplyAutoSaveSettingsToActiveTimeController();
        SimulationSnapshotConstants.MaxAutosaveFiles = Mathf.Max(1, maxAutosaveFiles);
    }

    public void ApplySimulationSettings()
    {
        SetMaxSeedsPerTile(maxSeedsPerTile);
        ApplyPlantSettingsToActiveManager();
    }

    public void SyncLocalControlsFromValues()
    {
        LogLocalSettings("SyncLocalControlsFromValues");

        if (autoSaveToggle != null)
        {
            autoSaveToggle.SetIsOnWithoutNotify(autoSaveEnabled);
        }

        SetDropdownToFloatValue(autoSavePeriodDropdown, autoSavePeriodOptionsSeconds, autoSavePeriodSeconds);
        SetDropdownToIntValue(maxAutosaveFilesDropdown, maxAutosaveFileOptions, maxAutosaveFiles);

        if (fullScreenToggle != null)
        {
            fullScreenToggle.SetIsOnWithoutNotify(fullScreen);
        }
    }

    public void SyncSimulationControlsFromValues()
    {
        SetDropdownToIntValue(maxSeedsPerTileDropdown, maxSeedsPerTileOptions, maxSeedsPerTile);
        SetSliderToScaledIntValue(agingRateSlider, agingRateScalar, agingRate);
        agingRateValueText.text = agingRate.ToString("F2") + " J/s/day";
        SetSliderToScaledIntValue(metabolismCostPerAlleleSlider, metabolismCostPerAlleleScalar, metabolismCostPerAllele);
        metabolismCostPerAlleleValueText.text = metabolismCostPerAllele.ToString("F3") + " J/s/allele";
        SetSliderToScaledIntValue(baseGrowthCostSlider, baseGrowthCostScalar, baseGrowthCost);
        baseGrowthCostValueText.text = baseGrowthCost.ToString("F2") + " J/s";
        SetDropdownToIntValue(flowerLimitDropdown, flowerLimitOptions, flowerLimit);

        if (plantAgingToggle != null)
        {
            plantAgingToggle.SetIsOnWithoutNotify(plantAging);
        }
    }

    public void ApplyForSceneFromSharedUI(Scene scene)
    {
        ConfigureForScene(scene);
        ApplyLocalSettings();
        SyncLocalControlsFromValues();

        if (IsSimulationScene(scene))
        {
            PullSimulationSettingsFromActiveManagers();
            SyncSimulationControlsFromValues();
        }
    }

    private void ConfigureForScene(Scene scene)
    {
        if (simSettingsToggle != null)
        {
            simSettingsToggle.SetActive(IsSimulationScene(scene));
        }
    }

    private bool IsSimulationScene(Scene scene)
    {
        return !string.IsNullOrWhiteSpace(scene.name) && scene.name == GetSimulationSceneName();
    }

    private string GetSimulationSceneName()
    {
        SharedUIPanelManager sharedUI = SharedUIPanelManager.Instance;
        if (sharedUI != null && !string.IsNullOrWhiteSpace(sharedUI.simulationSceneName))
            return sharedUI.simulationSceneName;

        if (!string.IsNullOrWhiteSpace(simulationSceneName))
            return simulationSceneName;

        return "SimulationScene";
    }

    private void PullSimulationSettingsFromActiveManagers()
    {
        SeedManager seedManager = GetActiveSeedManager();
        if (seedManager != null)
        {
            maxSeedsPerTile = Mathf.Max(0, seedManager.maxSeedsPerTile);
        }

        GrassManager grassManager = GetActiveGrassManager();
        if (grassManager == null)
            return;

        metabolismCostPerAllele = Mathf.Max(0f, grassManager.metabolismCostPerAllele);
        plantAging = grassManager.plantAging;
        agingRate = Mathf.Max(0f, grassManager.agingRate);
        baseGrowthCost = Mathf.Max(0f, grassManager.baseGrowthCost);
        flowerLimit = Mathf.Max(1, grassManager.flowerLimit);
    }

    private void ApplyAutoSaveSettingsToActiveTimeController()
    {
        TimeController timeController = GetActiveTimeController();
        if (timeController == null)
        {
            LogSettingChanged("Autosave TimeController apply", "skipped; TimeController not found in active scene");
            return;
        }

        timeController.enableAutoSave = autoSaveEnabled;
        timeController.autoSaveIntervalSeconds = Mathf.Max(1f, autoSavePeriodSeconds);
        LogSettingChanged(
            "Autosave TimeController apply",
            "enabled=" + timeController.enableAutoSave + ", interval=" + timeController.autoSaveIntervalSeconds + "s");
    }

    private void ApplyPlantSettingsToActiveManager()
    {
        GrassManager grassManager = GetActiveGrassManager();
        if (grassManager == null)
            return;

        grassManager.SetMetabolismCostPerAllele(metabolismCostPerAllele);
        grassManager.SetPlantAging(plantAging);
        grassManager.SetAgingRate(agingRate);
        grassManager.SetBaseGrowthCost(baseGrowthCost);
        grassManager.SetFlowerLimit(flowerLimit);
    }

    private static SeedManager GetActiveSeedManager()
    {
        return FindFirstObjectByType<SeedManager>();
    }

    private static GrassManager GetActiveGrassManager()
    {
        return FindFirstObjectByType<GrassManager>();
    }

    private static TimeController GetActiveTimeController()
    {
        return FindFirstObjectByType<TimeController>();
    }

    private static int GetIntDropdownValue(TMP_Dropdown dropdown, int[] values, int optionIndex, int fallback)
    {
        if (values != null && values.Length > 0)
        {
            int clampedIndex = Mathf.Clamp(optionIndex, 0, values.Length - 1);
            return values[clampedIndex];
        }

        string optionText = GetDropdownOptionText(dropdown, optionIndex);
        return TryParseSettingInt(optionText, out int parsed) ? parsed : fallback;
    }

    private static float GetFloatDropdownValue(TMP_Dropdown dropdown, float[] values, int optionIndex, float fallback)
    {
        if (values != null && values.Length > 0)
        {
            int clampedIndex = Mathf.Clamp(optionIndex, 0, values.Length - 1);
            return values[clampedIndex];
        }

        string optionText = GetDropdownOptionText(dropdown, optionIndex);
        return TryParseSettingFloat(optionText, out float parsed) ? parsed : fallback;
    }

    private static string GetDropdownOptionText(TMP_Dropdown dropdown, int optionIndex)
    {
        if (dropdown == null || dropdown.options == null || dropdown.options.Count == 0)
            return string.Empty;

        int clampedIndex = Mathf.Clamp(optionIndex, 0, dropdown.options.Count - 1);
        return dropdown.options[clampedIndex].text;
    }

    private static int GetDropdownCurrentIndex(TMP_Dropdown dropdown, int fallbackIndex)
    {
        if (dropdown == null)
            return fallbackIndex;

        return dropdown.value;
    }

    private static bool TryParseSettingInt(string text, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        string normalized = text.Trim().ToUpperInvariant();
        if (normalized == "NO LIMIT" || normalized == "NONE" || normalized == "UNLIMITED")
        {
            value = 0;
            return true;
        }

        return int.TryParse(normalized, out value);
    }

    private static bool TryParseSettingFloat(string text, out float value)
    {
        value = 0f;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        string normalized = text.Trim().ToUpperInvariant();
        if (float.TryParse(normalized, out value))
            return true;

        if (!TryParseLeadingFloat(normalized, out float amount))
            return false;

        if (normalized.Contains("HOUR") || normalized.Contains("HR"))
        {
            value = amount * 3600f;
            return true;
        }

        if (normalized.Contains("MIN"))
        {
            value = amount * 60f;
            return true;
        }

        if (normalized.Contains("SEC") || normalized.EndsWith("S"))
        {
            value = amount;
            return true;
        }

        value = amount;
        return true;
    }

    private static bool TryParseLeadingFloat(string text, out float value)
    {
        value = 0f;
        int endIndex = 0;
        while (endIndex < text.Length && (char.IsDigit(text[endIndex]) || text[endIndex] == '.' || text[endIndex] == ','))
        {
            endIndex++;
        }

        if (endIndex == 0)
            return false;

        string numberText = text.Substring(0, endIndex).Replace(',', '.');
        return float.TryParse(numberText, out value);
    }

    private static void SetSliderToScaledIntValue(Slider slider, float scalar, float unscaledValue)
    {
        if (slider == null || scalar <= 0f)
            return;

        int scaledValue = (int)(unscaledValue / scalar);
        slider.SetValueWithoutNotify(scaledValue);
    }

    private static void SetDropdownToIntValue(TMP_Dropdown dropdown, int[] values, int targetValue)
    {
        if (dropdown == null || dropdown.options == null || dropdown.options.Count == 0 || values == null || values.Length == 0)
            return;

        int closestIndex = 0;
        int closestDistance = Mathf.Abs(values[0] - targetValue);
        for (int i = 1; i < values.Length; i++)
        {
            int distance = Mathf.Abs(values[i] - targetValue);
            if (distance < closestDistance)
            {
                closestDistance = distance;
                closestIndex = i;
            }
        }

        dropdown.SetValueWithoutNotify(Mathf.Clamp(closestIndex, 0, dropdown.options.Count - 1));
    }

    private static void SetDropdownToFloatValue(TMP_Dropdown dropdown, float[] values, float targetValue)
    {
        if (dropdown == null || dropdown.options == null || dropdown.options.Count == 0 || values == null || values.Length == 0)
            return;

        int closestIndex = 0;
        float closestDistance = Mathf.Abs(values[0] - targetValue);
        for (int i = 1; i < values.Length; i++)
        {
            float distance = Mathf.Abs(values[i] - targetValue);
            if (distance < closestDistance)
            {
                closestDistance = distance;
                closestIndex = i;
            }
        }

        dropdown.SetValueWithoutNotify(Mathf.Clamp(closestIndex, 0, dropdown.options.Count - 1));
    }

    private void LogLocalSettings(string source)
    {
        if (!enableDebugLogs)
            return;

        Debug.Log(
            "[SimulationSettings] " + source
            + " local settings: autosaveEnabled=" + autoSaveEnabled
            + ", autoSavePeriodSeconds=" + autoSavePeriodSeconds
            + ", maxAutosaveFiles=" + maxAutosaveFiles
            + ", fullScreen=" + fullScreen
            + ", fullScreenMode=" + fullScreenMode
            + ", Screen.fullScreen=" + Screen.fullScreen
            + ", Screen.fullScreenMode=" + Screen.fullScreenMode);
    }

    private void LogSettingChanged(string settingName, object value)
    {
        if (!enableDebugLogs)
            return;

        Debug.Log("[SimulationSettings] " + settingName + " set to " + value);
    }

    private void LogDropdownResolved(string settingName, TMP_Dropdown dropdown, int eventIndex, int resolvedIndex, object value)
    {
        if (!enableDebugLogs)
            return;

        Debug.Log(
            "[SimulationSettings] " + settingName
            + " resolved eventIndex=" + eventIndex
            + ", dropdownValue=" + resolvedIndex
            + ", optionText='" + GetDropdownOptionText(dropdown, resolvedIndex) + "'"
            + ", value=" + value);
    }

    private IEnumerator LogFullScreenStateAfterFrame()
    {
        yield return null;

        if (!enableDebugLogs)
            yield break;

        Debug.Log(
            "[SimulationSettings] Fullscreen after frame: Screen.fullScreen=" + Screen.fullScreen
            + ", Screen.fullScreenMode=" + Screen.fullScreenMode
            + ", resolution=" + Screen.width + "x" + Screen.height);
    }

    private void PanelDefault()
    {
        localSettingsPanel.SetActive(true);
        simSettingsPanel.SetActive(false);
        controlsPanel.SetActive(false);
    }
}

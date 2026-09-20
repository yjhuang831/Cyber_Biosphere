using UnityEngine;
using UnityEngine.SceneManagement;
using TMPro;

public class MenuManager : MonoBehaviour
{
    [Header("Title Elements")]
    public GameObject titlePanel;

    [Header("Setup Elements")]
    public GameObject setupPanel;
    public TMP_Dropdown mapSizeDropdown;
    public TMP_Dropdown radiationDropdown;
    public TMP_Dropdown seedLimitDropdown;

    [Header("Shared UI")]
    public SharedUIPanelManager sharedUIPanelManager;

    [Header("Library Elements")]
    public GameObject libraryPanel;

    [Header("Credits Elements")]
    public GameObject creditsPanel;

    [Header("Simulation Setup Values")]
    public int startupMapWidth = 30;
    public int startupMapLength = 30;
    public float startupWaterRadiation = 0.1f;
    public float startupLandRadiation = 0.2f;
    public int startupMaxSeedsPerTile = 0;
    
    private string simulationSceneName = "SimulationScene";

    private void Start()
    {
        SharedUIPanelManager.EnsureSharedUISceneLoaded();
        ShowTitle();
    }

    public void ShowTitle()
    {
        HideAll();
        titlePanel.SetActive(true);
    }

    public void ShowSetup()
    {
        HideAll();
        setupPanel.SetActive(true);
    }

    public void ShowLoad()
    {
        SharedUIPanelManager sharedUI = ResolveSharedUI();
        if (sharedUI != null)
        {
            sharedUI.ShowSavesPanel();
        }
    }

    public void ShowSettings()
    {
        SharedUIPanelManager sharedUI = ResolveSharedUI();
        if (sharedUI != null)
        {
            sharedUI.ShowSettingsPanel();
        }
    }

    public void ShowLibrary()
    {
        HideAll();
        libraryPanel.SetActive(true);
    }

    public void ShowCredits()
    {
        HideAll();
        creditsPanel.SetActive(true);
    }

    void HideAll()
    {
        titlePanel.SetActive(false);
        setupPanel.SetActive(false);
        libraryPanel.SetActive(false);
        creditsPanel.SetActive(false);
    }

    public void StartSimulation()
    {
        SimulationLoadSystem.ClearPendingSnapshotLoad();
        ApplyStartupDropdownValues();
        QueueStartupSettings();
        SharedUIPanelManager sharedUI = ResolveSharedUI();
        if (sharedUI != null)
        {
            sharedUI.LoadSceneWithLoading(simulationSceneName);
            return;
        }

        LoadSimulationScene();
    }

    public void SetStartupMapSizeFromDropdown(int optionIndex)
    {
        SetStartupMapSizeFromDropdown();
    }

    private void SetStartupMapSizeFromDropdown()
    {
        if (mapSizeDropdown == null)
            return;

        SetStartupMapSize(GetSelectedDropdownText(mapSizeDropdown));
    }

    private void SetStartupMapSize(string size)
    {
        if (string.IsNullOrWhiteSpace(size))
        {
            Debug.LogWarning("Map size skipped: value is empty.");
            return;
        }

        string[] parts = size.ToUpperInvariant().Split('X');
        if (parts.Length != 2)
        {
            Debug.LogWarning("Map size skipped: expected format WidthXLength, such as 30X30.");
            return;
        }

        if (!int.TryParse(parts[0].Trim(), out int width) || !int.TryParse(parts[1].Trim(), out int length))
        {
            Debug.LogWarning("Map size skipped: width and length must be whole numbers.");
            return;
        }

        startupMapWidth = Mathf.Max(1, width);
        startupMapLength = Mathf.Max(1, length);
        Debug.Log($"Startup map size set to {startupMapWidth}x{startupMapLength}.");
    }

    public void SetRadiationFromDropdown(int optionIndex)
    {
        SetRadiationFromDropdown();
    }

    private void SetRadiationFromDropdown()
    {
        if (radiationDropdown == null)
            return;

        SetStartupRadiation(GetSelectedDropdownText(radiationDropdown));
    }

    private void SetStartupRadiation(string radiationLevel)
    {
        if (string.IsNullOrWhiteSpace(radiationLevel))
        {
            Debug.LogWarning("Radiation level skipped: value is empty.");
            return;
        }

        switch (radiationLevel.ToUpperInvariant())
        {
            case "LOW":
                startupWaterRadiation = 0.1f;
                startupLandRadiation = 0.2f;
                break;
            case "MEDIUM":
                startupWaterRadiation = 0.4f;
                startupLandRadiation = 0.5f;
                break;
            case "HIGH":
                startupWaterRadiation = 0.7f;
                startupLandRadiation = 0.8f;
                break;
            default:
                Debug.LogWarning("Radiation level skipped: unrecognized value. Use Low, Medium, or High.");
                break;
        }
    }

    private void QueueStartupSettings()
    {
        SimulationStartupSettings.Configure(
            startupMapWidth,
            startupMapLength,
            startupWaterRadiation,
            startupLandRadiation,
            startupMaxSeedsPerTile);
    }

    private void ApplyStartupDropdownValues()
    {
        SetStartupMapSizeFromDropdown();
        SetRadiationFromDropdown();
        SetSeedLimitFromDropdown();
    }

    public void SetSeedLimitFromDropdown(int optionIndex)
    {
        SetSeedLimitFromDropdown();
    }

    private void SetSeedLimitFromDropdown()
    {
        if (seedLimitDropdown == null)
            return;

        SetStartupSeedLimit(GetSelectedDropdownText(seedLimitDropdown));
    }

    private void SetStartupSeedLimit(string seedLimit)
    {
        if (string.IsNullOrWhiteSpace(seedLimit))
        {
            Debug.LogWarning("Seed limit skipped: value is empty.");
            return;
        }

        string normalized = seedLimit.Trim().ToUpperInvariant();
        if (normalized == "NO LIMIT" || normalized == "NONE" || normalized == "UNLIMITED")
        {
            startupMaxSeedsPerTile = 0;
            return;
        }

        if (!int.TryParse(normalized, out int parsedLimit))
        {
            Debug.LogWarning("Seed limit skipped: expected a whole number or No Limit.");
            return;
        }

        startupMaxSeedsPerTile = Mathf.Max(0, parsedLimit);
    }

    private string GetSelectedDropdownText(TMP_Dropdown dropdown)
    {
        if (dropdown == null || dropdown.options == null || dropdown.options.Count == 0)
            return string.Empty;

        int optionIndex = Mathf.Clamp(dropdown.value, 0, dropdown.options.Count - 1);
        return dropdown.options[optionIndex].text;
    }

    private void LoadSimulationScene()
    {
        if (string.IsNullOrWhiteSpace(simulationSceneName))
        {
            Debug.LogWarning("Simulation scene load skipped: simulationSceneName is empty.");
            return;
        }

        SceneManager.LoadScene(simulationSceneName);
    }

    private SharedUIPanelManager ResolveSharedUI()
    {
        if (sharedUIPanelManager != null)
            return sharedUIPanelManager;

        sharedUIPanelManager = SharedUIPanelManager.Instance;
        if (sharedUIPanelManager == null)
        {
            sharedUIPanelManager = FindFirstObjectByType<SharedUIPanelManager>();
        }

        if (sharedUIPanelManager == null)
        {
            SharedUIPanelManager.EnsureSharedUISceneLoaded();
            Debug.LogWarning("Shared UI panel manager was not found. Make sure the shared UI scene is loaded.");
        }

        return sharedUIPanelManager;
    }

    public void ReturnToDesktop()
    {
        Application.Quit();

        // Note: Application.Quit() does not work in the Unity editor. To test quitting in the editor
        #if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
        #endif
    }

    public void SetMaxAutosaveFiles(int newLimit)
    {
        SimulationSnapshotConstants.MaxAutosaveFiles = Mathf.Max(1, newLimit);
    }
}

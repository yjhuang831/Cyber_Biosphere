using System.Collections.Generic;
using System.Collections;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

public class SharedUIPanelManager : MonoBehaviour
{
    public const string DefaultSharedUISceneName = "SharedUIScene";
    public static SharedUIPanelManager Instance { get; private set; }
    private static bool sharedSceneLoadQueued;

    [Header("Scene Behavior")]
    public bool persistAcrossSceneLoads = true;
    public string simulationSceneName = "SimulationScene";
    public bool hidePanelsAfterSceneLoad = true;

    [Header("Shared Panels")]
    public GameObject Mask;
    public GameObject savesPanel;
    public GameObject settingsPanel;

    [Header("Settings")]
    public SimulationSettingsPanelController settingsPanelController;
    private const string PlayerPrefsFileName = "PlayerPrefs.json";

    [Header("Loading")]
    public GameObject loadingPanel;
    public float minimumLoadingPanelSeconds = 1.5f;

    [Header("Saves Panel")]
    public GameObject loadContentParent;
    public TMP_Text selectedSaveText;
    public GameObject saveButtonPrefab;
    public string noSaveSelectedText = "No save selected";

    internal string selectedSaveName;
    internal readonly List<string> saveNames = new List<string>();
    private Coroutine sceneLoadRoutine;
    private Coroutine hideLoadingRoutine;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;

        if (persistAcrossSceneLoads)
        {
            DontDestroyOnLoad(gameObject);
        }

        sharedSceneLoadQueued = false;

        settingsPanelController = ResolveSettingsPanelController();
        if (settingsPanelController != null)
        {
            LoadPlayerPreferences();
        }
    }

    public static void EnsureSharedUISceneLoaded(string sceneName = DefaultSharedUISceneName)
    {
        if (Instance != null || sharedSceneLoadQueued || string.IsNullOrWhiteSpace(sceneName))
            return;

        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            Scene loadedScene = SceneManager.GetSceneAt(i);
            if (loadedScene.name == sceneName)
                return;
        }

        sharedSceneLoadQueued = true;
        SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Additive);
    }

    private void OnEnable()
    {
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private void OnDisable()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    private void Start()
    {
        HideAllPanels();
    }

    public void ShowSavesPanel()
    {
        RefreshSaveNames();
        RebuildSaveButtons();
        selectedSaveName = null;
        SetSelectedSaveText(noSaveSelectedText);

        HideAllPanels();
        SetActive(savesPanel, true);
        SetActive(Mask, true);
    }

    public void ShowSettingsPanel()
    {
        HideAllPanels();
        SetActive(settingsPanel, true);
        SetActive(Mask, true);
    }

    public void HideAllPanels()
    {
        SetActive(savesPanel, false);
        SetActive(settingsPanel, false);
        SetActive(Mask, false);
        SetActive(loadingPanel, false);
    }

    public void ShowLoadingPanel()
    {
        SetActive(savesPanel, false);
        SetActive(settingsPanel, false);
        SetActive(Mask, false);
        SetActive(loadingPanel, true);
    }

    public void LoadSimulationSceneWithLoading()
    {
        LoadSceneWithLoading(simulationSceneName);
    }

    public void LoadSceneWithLoading(string sceneName)
    {
        if (sceneLoadRoutine != null)
            return;

        sceneLoadRoutine = StartCoroutine(LoadSceneWithLoadingRoutine(sceneName));
    }

    public void SelectSave(string saveName)
    {
        selectedSaveName = saveName;
        SetSelectedSaveText(string.IsNullOrWhiteSpace(saveName) ? noSaveSelectedText : saveName);
    }

    public void LoadSelectedSave()
    {
        if (string.IsNullOrWhiteSpace(selectedSaveName))
        {
            Debug.LogWarning("Load skipped: no save file selected.");
            return;
        }

        LoadSave(selectedSaveName);
    }

    public bool LoadSave(string saveNameOrPath)
    {
        if (string.IsNullOrWhiteSpace(saveNameOrPath))
        {
            Debug.LogWarning("Load skipped: save name is empty.");
            return false;
        }

        SimulationStartupSettings.Clear();
        if (!SimulationLoadSystem.QueueSnapshotLoadByIdentifier(saveNameOrPath))
        {
            return false;
        }

        LoadSimulationSceneWithLoading();
        return true;
    }

    public void DeleteSelectedSave()
    {
        if (string.IsNullOrWhiteSpace(selectedSaveName))
        {
            Debug.LogWarning("Delete save skipped: no save file selected.");
            return;
        }

        DeleteSave(selectedSaveName);
    }

    public bool DeleteSave(string saveNameOrPath)
    {
        if (!SimulationLoadSystem.TryResolveSnapshotPathByIdentifier(saveNameOrPath, out string filePath))
        {
            Debug.LogWarning("Delete save failed: could not resolve save file from input '" + saveNameOrPath + "'.");
            return false;
        }

        File.Delete(filePath);
        RefreshSaveNames();
        RebuildSaveButtons();

        if (selectedSaveName == saveNameOrPath)
        {
            selectedSaveName = null;
            SetSelectedSaveText(noSaveSelectedText);
        }

        return true;
    }

    public void RefreshSaveNames()
    {
        saveNames.Clear();
        AddSavesFromFolder(SimulationSnapshotConstants.ManualSaveFolder);
        AddSavesFromFolder(SimulationSnapshotConstants.AutosaveFolder);
    }

    private void RebuildSaveButtons()
    {
        if (loadContentParent == null || saveButtonPrefab == null)
            return;

        for (int i = loadContentParent.transform.childCount - 1; i >= 0; i--)
        {
            Destroy(loadContentParent.transform.GetChild(i).gameObject);
        }

        for (int i = 0; i < saveNames.Count; i++)
        {
            string saveName = saveNames[i];
            GameObject buttonObj = Instantiate(saveButtonPrefab, loadContentParent.transform);

            TMP_Text label = buttonObj.GetComponentInChildren<TMP_Text>();
            if (label != null)
            {
                label.text = saveName;
            }

            LoadButton loadButton = buttonObj.GetComponent<LoadButton>();
            if (loadButton != null)
            {
                loadButton.fileName = saveName;
            }
        }
    }

    private void AddSavesFromFolder(string subfolder)
    {
        string folderPath = Path.Combine(Application.persistentDataPath, subfolder);
        if (!Directory.Exists(folderPath))
            return;

        string[] files = Directory.GetFiles(folderPath, "*" + SimulationSnapshotConstants.SnapshotFileExtension, SearchOption.TopDirectoryOnly);
        System.Array.Sort(files, (a, b) => File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)));

        for (int i = 0; i < files.Length; i++)
        {
            string fileName = Path.GetFileNameWithoutExtension(files[i]);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                saveNames.Add(fileName);
            }
        }
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        ApplySettingsForScene(scene);
        StartCoroutine(ApplySettingsAfterSceneStartRoutine(scene));

        if (sceneLoadRoutine != null)
            return;

        if (hidePanelsAfterSceneLoad)
        {
            if (hideLoadingRoutine != null)
            {
                StopCoroutine(hideLoadingRoutine);
            }

            hideLoadingRoutine = StartCoroutine(HideLoadingAfterSceneStartRoutine());
        }
    }

    private IEnumerator LoadSceneWithLoadingRoutine(string sceneName)
    {
        if (string.IsNullOrWhiteSpace(sceneName))
        {
            Debug.LogWarning("Scene load skipped: sceneName is empty.");
            sceneLoadRoutine = null;
            yield break;
        }

        ShowLoadingPanel();
        float loadingStartedAt = Time.unscaledTime;

        yield return null;

        AsyncOperation loadOperation = SceneManager.LoadSceneAsync(sceneName);
        if (loadOperation == null)
        {
            Debug.LogWarning("Scene load failed to start: " + sceneName);
            HideAllPanels();
            sceneLoadRoutine = null;
            yield break;
        }

        while (!loadOperation.isDone)
        {
            yield return null;
        }

        float remainingLoadingTime = minimumLoadingPanelSeconds - (Time.unscaledTime - loadingStartedAt);
        if (remainingLoadingTime > 0f)
        {
            yield return new WaitForSecondsRealtime(remainingLoadingTime);
        }

        HideAllPanels();
        sceneLoadRoutine = null;
    }

    private IEnumerator HideLoadingAfterSceneStartRoutine()
    {
        yield return null;

        HideAllPanels();
        hideLoadingRoutine = null;
    }

    private IEnumerator ApplySettingsAfterSceneStartRoutine(Scene scene)
    {
        yield return null;

        ApplySettingsForScene(scene);
    }

    private void ApplySettingsForScene(Scene scene)
    {
        SimulationSettingsPanelController settingsController = ResolveSettingsPanelController();
        if (settingsController != null)
        {
            settingsController.ApplyForSceneFromSharedUI(scene);
        }
    }

    private SimulationSettingsPanelController ResolveSettingsPanelController()
    {
        if (settingsPanelController != null)
            return settingsPanelController;

        if (settingsPanel != null)
        {
            settingsPanelController = settingsPanel.GetComponentInChildren<SimulationSettingsPanelController>(true);
        }

        if (settingsPanelController == null)
        {
            settingsPanelController = GetComponentInChildren<SimulationSettingsPanelController>(true);
        }

        return settingsPanelController;
    }

    private void SetSelectedSaveText(string text)
    {
        if (selectedSaveText != null)
        {
            selectedSaveText.text = text;
        }
    }

    private static void SetActive(GameObject target, bool active)
    {
        if (target != null)
        {
            target.SetActive(active);
        }
    }

    internal void SavePlayerPreferences()
    {
        LocalPlayerPreferences newPreferences = new LocalPlayerPreferences
        {
            autoSaveEnabled = settingsPanelController.autoSaveEnabled,
            maxAutoSaves = settingsPanelController.maxAutosaveFiles,
            autoSavePeriodSeconds = settingsPanelController.autoSavePeriodSeconds,
            isFullScreen = settingsPanelController.fullScreen
        };

        string filePath = GetPlayerPreferencePath();
        string json = JsonUtility.ToJson(newPreferences, prettyPrint: true);
        File.WriteAllText(filePath, json);
    }

    internal void LoadPlayerPreferences()
    {
        string filePath = GetPlayerPreferencePath();
        if (File.Exists(filePath))
        {
            string json = File.ReadAllText(filePath);
            LocalPlayerPreferences preferences = JsonUtility.FromJson<LocalPlayerPreferences>(json);

            settingsPanelController.autoSaveEnabled = preferences.autoSaveEnabled;
            settingsPanelController.maxAutosaveFiles = preferences.maxAutoSaves;
            settingsPanelController.autoSavePeriodSeconds = preferences.autoSavePeriodSeconds;
            settingsPanelController.fullScreen = preferences.isFullScreen;
        }
    }

    private static string GetPlayerPreferencePath()
    {
        return Path.Combine(Application.persistentDataPath, PlayerPrefsFileName);
    }

    [System.Serializable]
    public class LocalPlayerPreferences
    {
        public bool autoSaveEnabled = true;
        public int maxAutoSaves = 10;
        public float autoSavePeriodSeconds = 300f;
        public bool isFullScreen = false;
    }
}

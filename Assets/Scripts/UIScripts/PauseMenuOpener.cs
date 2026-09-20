using UnityEngine;
using UnityEngine.SceneManagement;

public class PauseMenuOpener : MonoBehaviour
{
    // Reference to the settings panel GameObject
    public GameObject PausePanel;
    public GameObject Mask;
    public GameObject SavePanel;
    public TimeController timeController; // Reference to the TimeController script
    public DetailedInformationPanelController detailedInformationPanelController;
    public GrassDataPanelController grassDataPanelController;

    void Start()
    {
        SharedUIPanelManager.EnsureSharedUISceneLoaded();

        // Optionally, ensure the panel is initially inactive
        if (PausePanel != null)
        {
            PausePanel.SetActive(false);
            Mask.SetActive(false);
        }
        if (SavePanel != null)
        {
            SavePanel.SetActive(false);
        }
    }

    void Update()
    {
        // Check for the Escape key press to toggle the pause menu
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            PauseMenu();
        }
    }

    // Method to toggle the settings panel
    public void PauseMenu()
    {
        // Check if the PausePanel reference is not null
        if (PausePanel != null)
        {
            // Toggle the active state of the pause panel
            bool isOpening = !PausePanel.activeSelf;
            PausePanel.SetActive(isOpening);
            Mask.SetActive(isOpening);
            if (isOpening)
            {
                ClearTileFocusAndInfoPanels();
                timeController.TogglePause(true, false);
            }
            else
            {
                timeController.TogglePause(true);
            }
        }
    }

    public void OpenSavePanel()
    {
        PausePanel.SetActive(!PausePanel.activeSelf);
        SavePanel.SetActive(!SavePanel.activeSelf);

        if (!string.IsNullOrWhiteSpace(SimulationLoadSystem.SimulationName))
        {
            timeController.manualSaveNameInputField.text = SimulationLoadSystem.SimulationName;
        }
        else
        {
            timeController.manualSaveNameInputField.text = null;
        }
    }

    public void SaveNow()
    {
        timeController.SaveSimulationNow();
        OpenSavePanel();
    }

    public void OpenLoadMenu()
    {
        if (SharedUIPanelManager.Instance != null)
        {
            SharedUIPanelManager.Instance.ShowSavesPanel();
        }
        else
        {
            Debug.LogWarning("Open load menu skipped: SharedUIPanelManager was not found.");
        }
    }

    public void OpenSettingsMenu()
    {
        if (SharedUIPanelManager.Instance != null)
        {
            SharedUIPanelManager.Instance.ShowSettingsPanel();
        }
    }

    public void ReturnToTitle()
    {
        if (SharedUIPanelManager.Instance != null)
        {
            SharedUIPanelManager.Instance.LoadSceneWithLoading("MainMenu");
        }
        else
        {
            SceneManager.LoadScene("MainMenu");
        }
    }

    public void ReturnToDesktop()
    {
        Application.Quit();

        // Note: Application.Quit() does not work in the Unity editor. To test quitting in the editor
        #if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
        #endif
    }

    private void ClearTileFocusAndInfoPanels()
    {
        if (detailedInformationPanelController == null)
        {
            MapManager mapManager = FindFirstObjectByType<MapManager>();
            if (mapManager != null)
            {
                detailedInformationPanelController = mapManager.detailedInformationPanelController;
                grassDataPanelController = mapManager.grassDataPanelController;
            }
        }

        if (detailedInformationPanelController != null)
        {
            detailedInformationPanelController.Hide();
        }

        if (grassDataPanelController != null)
        {
            grassDataPanelController.Hide();
        }
    }
}

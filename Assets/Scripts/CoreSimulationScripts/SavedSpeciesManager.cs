using UnityEngine;
using System.Collections.Generic;
using TMPro;
using UnityEngine.UI;

/// <summary>
/// Manages UI for displaying and selecting saved species of significance.
/// Handles loading species references from GenusManager and displaying them.
/// </summary>
public class SavedSpeciesManager : MonoBehaviour
{
    [Header("Species Panel Elements")]
    public GameObject speciesPanel;
    public GameObject speciesContentParent;
    public GameObject speciesButtonPrefab;
    
    [Header("Selection Display")]
    public TMP_Text selectedSpeciesText;
    public Button clearSelectionButton;

    private GenusManager subscribedGenusManager;

    void Start()
    {
        selectedSpeciesText.text = "";
        if (clearSelectionButton != null){
            clearSelectionButton.onClick.AddListener(ClearSelection);
            clearSelectionButton.gameObject.SetActive(false);
        }
        HideSpecies();
        subscribedGenusManager = GenusManager.Instance;
        if (subscribedGenusManager != null)
        {
            subscribedGenusManager.LibraryChanged += OnLibraryChanged;
        }
    }

    void OnDestroy()
    {
        // Use the stored reference: during scene teardown GenusManager.Instance may already be gone
        if (subscribedGenusManager != null)
        {
            subscribedGenusManager.LibraryChanged -= OnLibraryChanged;
        }
    }

    // Keeps an open list and the selection label in sync with saves, renames, and deletions
    private void OnLibraryChanged()
    {
        if (speciesPanel != null && speciesPanel.activeSelf)
        {
            ShowSpecies(true);
        }
        RefreshSelectionText();
    }

    private void RefreshSelectionText()
    {
        GenusManager.SpeciesEntry entry = null;
        GenusManager genusManager = GenusManager.Instance;
        bool hasSelection = genusManager != null
            && genusManager.TryGetSpeciesEntry(genusManager.selectedSpeciesId, out entry);

        if (selectedSpeciesText != null)
        {
            selectedSpeciesText.text = hasSelection ? "Selected: " + entry.DisplayName : "";
        }
        if (clearSelectionButton != null)
        {
            clearSelectionButton.gameObject.SetActive(hasSelection);
        }
    }

    /// <summary>
    /// Shows the species selection panel with all available saved species.
    /// </summary>
    public void ShowSpecies(bool refresh = false)
    {
        if (speciesPanel == null)
        {
            Debug.LogError("SavedSpeciesManager: speciesPanel is not assigned.");
            return;
        }

        // Clear existing buttons
        for (int i = speciesContentParent.transform.childCount - 1; i >= 0; i--)
        {
            Destroy(speciesContentParent.transform.GetChild(i).gameObject);
        }

        // Create buttons for each available species, sorted by name; buttons carry the GUID
        if (GenusManager.Instance != null)
        {
            List<GenusManager.SpeciesEntry> entries = new List<GenusManager.SpeciesEntry>(GenusManager.Instance.Local_Species.Values);
            entries.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, System.StringComparison.OrdinalIgnoreCase));
            foreach (GenusManager.SpeciesEntry entry in entries)
            {
                GameObject buttonObj = Instantiate(speciesButtonPrefab, speciesContentParent.transform);
                buttonObj.GetComponentInChildren<TMP_Text>().text = entry.DisplayName;
                buttonObj.GetComponent<SpeciesButton>().speciesId = entry.guid;
            }
        }

        if (refresh)
        {
            // If refreshing, keep the panel open but update the buttons
            return;
        }
        speciesPanel.SetActive(!speciesPanel.activeSelf);
    }

    /// <summary>
    /// Refreshes the species list and shows the panel (called after deletion).
    /// </summary>
    public void RefreshAndShowSpecies()
    {
        ShowSpecies(true);
    }

    /// <summary>
    /// Hides the species selection panel.
    /// </summary>
    public void HideSpecies()
    {
        if (speciesPanel != null)
        {
            speciesPanel.SetActive(false);
        }
    }

    /// <summary>
    /// Clears the currently selected species.
    /// </summary>
    public void ClearSelection()
    {
        if (GenusManager.Instance != null)
        {
            GenusManager.Instance.selectedSpeciesId = null;
        }

        RefreshSelectionText();
    }

    /// <summary>
    /// Called by SpeciesButton when a species is selected.
    /// Updates GenusManager and UI display.
    /// </summary>
    internal void SelectSpecies(string speciesId)
    {
        if (string.IsNullOrEmpty(speciesId))
            return;

        // Store selection in GenusManager (the source of truth)
        if (GenusManager.Instance != null)
        {
            GenusManager.Instance.SelectSpecies(speciesId);
        }

        RefreshSelectionText();
    }
}

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

    void Start()
    {
        selectedSpeciesText.text = "";
        if (clearSelectionButton != null){
            clearSelectionButton.onClick.AddListener(ClearSelection);
            clearSelectionButton.gameObject.SetActive(false);
        }
        HideSpecies();
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

        // Create buttons for each available species
        if (GenusManager.Instance != null)
        {
            foreach (var speciesId in GenusManager.Instance.Local_SpeciesReferences)
            {
                GameObject buttonObj = Instantiate(speciesButtonPrefab, speciesContentParent.transform);
                buttonObj.GetComponentInChildren<TMP_Text>().text = speciesId;
                buttonObj.GetComponent<SpeciesButton>().speciesId = speciesId;
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

        if (selectedSpeciesText != null)
        {
            selectedSpeciesText.text = "";
        }
        clearSelectionButton.gameObject.SetActive(false);
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

        // Update UI display
        if (selectedSpeciesText != null)
        {
            selectedSpeciesText.text = "Selected: " + speciesId;
        }
        clearSelectionButton.gameObject.SetActive(true);
    }
}

using UnityEngine;
using UnityEngine.UI;
using System.IO;

/// <summary>
/// Handles selection of a saved species from the species UI.
/// Mirrors LoadButton for save file selection.
/// </summary>
public class SpeciesButton : MonoBehaviour
{
    internal string speciesId; // Library species GUID

    public void OnSpeciesSelected()
    {
        if (string.IsNullOrEmpty(speciesId))
            return;

        SavedSpeciesManager manager = FindFirstObjectByType<SavedSpeciesManager>();
        if (manager != null)
        {
            manager.SelectSpecies(speciesId);
        }
    }

    public void DeleteSpecies()
    {
        SavedSpeciesManager manager = FindFirstObjectByType<SavedSpeciesManager>();
        if (manager != null)
        {
            if (GenusManager.Instance != null)
            {
                GenusManager.Instance.DeleteSavedSpecies(speciesId); // LibraryChanged refreshes the open list
            }
        }
    }
}

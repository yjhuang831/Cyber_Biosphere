using UnityEngine;
using System.Collections.Generic;
using System.IO;

/// <summary>
/// Manages the user-facing local species save registry.
/// Persists saved-species references and loads selected genomes on demand.
/// </summary>
public class GenusManager : MonoBehaviour
{
    private static GenusManager instance;
    public static GenusManager Instance
    {
        get
        {
            if (instance == null)
            {
                instance = FindFirstObjectByType<GenusManager>();
                if (instance == null)
                {
                    Debug.LogError("GenusManager instance not found in scene.");
                }
            }
            return instance;
        }
        private set => instance = value;
    }

    private const string LOCAL_SPECIES_FOLDER = "species";
    private const string LOCAL_SPECIES_FILE_EXTENSION = ".json";
    // Reference pointers to available species of significance (loaded on Awake)
    internal readonly HashSet<string> Local_SpeciesReferences = new HashSet<string>();
    // Full genome data for saved species (loaded on-demand, doesn't interrupt simulation)
    internal readonly Dictionary<string, PlantGenomeState> Local_SpeciesGenomes = new Dictionary<string, PlantGenomeState>();
    // Currently selected species from saved list (used during simulation)
    internal string selectedSpeciesId;

    void Awake()
    {
        if (instance == null)
        {
            Instance = this;
            // Load species references without loading full genome data to avoid interrupting simulation
            LoadLocalSpeciesReferences();
        }
        else if (instance != this)
        {
            Destroy(gameObject);
        }
    }

    /// <summary>
    /// Loads only species references (names) on Awake without interrupting simulation.
    /// Full genome data is loaded on-demand via LoadLocalSpeciesData().
    /// </summary>
    internal void LoadLocalSpeciesReferences()
    {
        string folderPath = GetLocalSpeciesDirectoryPath();
        if (!Directory.Exists(folderPath))
        {
            Directory.CreateDirectory(folderPath);
            // Copy default species from StreamingAssets on first run
            CopyDefaultSpeciesFromStreamingAssets(folderPath);
        }

        Local_SpeciesReferences.Clear();

        try
        {
            string[] speciesFiles = Directory.GetFiles(folderPath, "*" + LOCAL_SPECIES_FILE_EXTENSION, SearchOption.TopDirectoryOnly);
            for (int i = 0; i < speciesFiles.Length; i++)
            {
                string filePath = speciesFiles[i];
                try
                {
                    string json = File.ReadAllText(filePath);
                    LocalSpeciesData speciesData = JsonUtility.FromJson<LocalSpeciesData>(json);
                    if (speciesData == null || speciesData.genome == null)
                        continue;

                    string genus = speciesData.genome.genus;
                    string species = speciesData.genome.species;

                    if (string.IsNullOrEmpty(genus) || string.IsNullOrEmpty(species))
                        continue;

                    string speciesId = BuildSpeciesId(genus.Trim(), species.Trim());
                    Local_SpeciesReferences.Add(speciesId);
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning("Skipped local species reference due to parse error: " + filePath + " | " + e.Message);
                }
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError("Failed to load local species references: " + e.Message);
        }
    }

    /// <summary>
    /// Copies default species from StreamingAssets/species to persistent storage on first run.
    /// </summary>
    private static void CopyDefaultSpeciesFromStreamingAssets(string targetFolder)
    {
        string streamingPath = System.IO.Path.Combine(Application.streamingAssetsPath, "species");
        
        // Check if defaults exist in StreamingAssets
        if (!Directory.Exists(streamingPath))
        {
            return; // No defaults bundled
        }

        try
        {
            string[] defaultFiles = Directory.GetFiles(streamingPath, "*.json", SearchOption.TopDirectoryOnly);
            foreach (string filePath in defaultFiles)
            {
                string fileName = System.IO.Path.GetFileName(filePath);
                string destPath = System.IO.Path.Combine(targetFolder, fileName);
                File.Copy(filePath, destPath, overwrite: false);
            }
            Debug.Log("Copied " + defaultFiles.Length + " default species from StreamingAssets.");
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("Failed to copy default species from StreamingAssets: " + e.Message);
        }
    }

    /// <summary>
    /// Selects a species from the saved list for use during simulation.
    /// </summary>
    internal void SelectSpecies(string speciesId)
    {
        if (string.IsNullOrEmpty(speciesId) || !Local_SpeciesReferences.Contains(speciesId))
            return;
        selectedSpeciesId = speciesId;
    }

    /// <summary>
    /// Deletes a saved species file and removes it from the references.
    /// </summary>
    internal void DeleteSavedSpecies(string speciesId)
    {
        if (string.IsNullOrEmpty(speciesId) || !Local_SpeciesReferences.Contains(speciesId))
            return;

        try
        {
            // Parse speciesId to get genus and species
            string[] parts = speciesId.Split('_');
            if (parts.Length < 2)
                return;

            string genus = parts[0];
            string species = parts[1];

            string filePath = GetLocalSpeciesFilePath(genus, species);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                Local_SpeciesReferences.Remove(speciesId);
                Local_SpeciesGenomes.Remove(speciesId);
                if (selectedSpeciesId == speciesId)
                {
                    selectedSpeciesId = null;
                }
                Debug.Log("Deleted saved species: " + speciesId);
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError("Failed to delete species \"" + speciesId + "\": " + e.Message);
        }
    }

    /// <summary>
    /// Attempts to load a single saved species genome by ID. Reads only the specific file needed.
    /// Caches the result in Local_SpeciesGenomes for future use.
    /// </summary>
    internal bool TryGetSavedSpeciesGenome(string speciesId, out PlantGenomeState genomeState)
    {
        genomeState = null;

        if (string.IsNullOrEmpty(speciesId))
            return false;

        // Check if already cached
        if (Local_SpeciesGenomes.TryGetValue(speciesId, out genomeState))
            return true;

        // Check if species exists in references
        if (!Local_SpeciesReferences.Contains(speciesId))
            return false;

        try
        {
            // Parse the speciesId to get genus and species
            string[] parts = speciesId.Split('_');
            if (parts.Length < 2)
                return false;

            string genus = parts[0];
            string species = parts[1];

            string filePath = GetLocalSpeciesFilePath(genus, species);
            if (!File.Exists(filePath))
                return false;

            string json = File.ReadAllText(filePath);
            LocalSpeciesData speciesData = JsonUtility.FromJson<LocalSpeciesData>(json);

            if (speciesData == null || speciesData.genome == null)
                return false;

            // Cache it for future use
            genomeState = speciesData.genome;
            Local_SpeciesGenomes[speciesId] = genomeState;
            return true;
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("Failed to load species genome '" + speciesId + "': " + e.Message);
            return false;
        }
    }


    /// <summary>
    /// Saves a species of significant to local persistent storage.
    /// Creates one file per species with full genome data.
    /// </summary>
    public bool SaveLocalSpeciesGenome(PlantGenome genome, string genusName, string speciesName)
    {
        if (genome == null)
        {
            Debug.LogWarning("SaveLocalSpeciesGenome skipped: genome is null.");
            return false;
        }

        if (!TryNormalizeTaxonomyNames(genusName, speciesName, out string genus, out string species))
        {
            Debug.LogWarning("SaveLocalSpeciesGenome skipped: genus or species name is empty.");
            return false;
        }

        try
        {
            if (HasSanitizedSpeciesFileCollision(genus, species))
            {
                Debug.LogWarning("SaveLocalSpeciesGenome skipped: species '" + genus + " " + species + "' collides with another species file name after sanitization.");
                return false;
            }

            string speciesId = BuildSpeciesId(genus, species);
            string existingSpeciesKey = FindExistingSpeciesKeyIgnoreCase(Local_SpeciesGenomes, speciesId);
            if (!string.IsNullOrEmpty(existingSpeciesKey) && existingSpeciesKey != speciesId)
            {
                Debug.LogWarning("SaveLocalSpeciesGenome skipped: species '" + speciesId + "' conflicts with existing species '" + existingSpeciesKey + "' (case-insensitive duplicate).");
                return false;
            }

            bool isOverwrite = Local_SpeciesGenomes.ContainsKey(speciesId);
            if (isOverwrite)
            {
                Debug.LogWarning("SaveLocalSpeciesGenome: overwriting existing local species entry for '" + genus + " " + species + "'.");
            }

            Local_SpeciesGenomes[speciesId] = SerializeGenomeForLocalStore(genome, genus, species);

            WriteLocalSpeciesFile(speciesId, genus, species);
            return true;
        }
        catch (System.Exception e)
        {
            Debug.LogError("SaveLocalSpeciesGenome failed: " + e.Message);
            return false;
        }
    }


    private void LoadLocalSpeciesData()
    {
        string folderPath = GetLocalSpeciesDirectoryPath();
        if (!Directory.Exists(folderPath))
        {
            Directory.CreateDirectory(folderPath);
            return;
        }

        string[] speciesFiles = Directory.GetFiles(folderPath, "*" + LOCAL_SPECIES_FILE_EXTENSION, SearchOption.TopDirectoryOnly);
        for (int i = 0; i < speciesFiles.Length; i++)
        {
            string filePath = speciesFiles[i];
            try
            {
                string json = File.ReadAllText(filePath);
                LocalSpeciesData speciesData = JsonUtility.FromJson<LocalSpeciesData>(json);
                if (speciesData == null || speciesData.genome == null)
                    continue;

                string genus = speciesData.genome.genus;
                string species = speciesData.genome.species;
                
                if (string.IsNullOrEmpty(genus) || string.IsNullOrEmpty(species))
                    continue;

                genus = genus.Trim();
                species = species.Trim();

                if (string.IsNullOrEmpty(genus) || string.IsNullOrEmpty(species))
                    continue;

                string speciesId = BuildSpeciesId(genus, species);
                string existingSpeciesKey = FindExistingSpeciesKeyIgnoreCase(Local_SpeciesGenomes, speciesId);
                if (!string.IsNullOrEmpty(existingSpeciesKey) && existingSpeciesKey != speciesId)
                {
                    Debug.LogWarning("Skipping species '" + speciesId + "' from file due to case-insensitive duplicate of existing species '" + existingSpeciesKey + "'.");
                    continue;
                }

                if (HasSanitizedSpeciesFileCollision(genus, species))
                {
                    Debug.LogWarning("Skipping species '" + speciesId + "' from file due to sanitized file-name collision with another species.");
                    continue;
                }

                Local_SpeciesGenomes[speciesId] = speciesData.genome;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("Skipped local species file due to parse error: " + filePath + " | " + e.Message);
            }
        }
    }

    private void WriteLocalSpeciesFile(string speciesId, string genus, string species)
    {
        if (string.IsNullOrWhiteSpace(speciesId) || string.IsNullOrWhiteSpace(genus) || string.IsNullOrWhiteSpace(species))
            return;

        if (!Local_SpeciesGenomes.TryGetValue(speciesId, out PlantGenomeState genomeState))
            return;

        if (HasSanitizedSpeciesFileCollision(genus, species))
        {
            Debug.LogWarning("WriteLocalSpeciesFile skipped for species '" + speciesId + "' due to sanitized file-name collision.");
            return;
        }

        string folderPath = GetLocalSpeciesDirectoryPath();
        Directory.CreateDirectory(folderPath);

        LocalSpeciesData speciesData = new LocalSpeciesData
        {
            genome = genomeState
        };

        string json = JsonUtility.ToJson(speciesData, true);
        string filePath = GetLocalSpeciesFilePath(genus, species);
        File.WriteAllText(filePath, json);
    }

    private static PlantGenomeState SerializeGenomeForLocalStore(PlantGenome genome, string genusName = null, string speciesName = null)
    {
        string resolvedGenus = string.IsNullOrWhiteSpace(genusName) ? genome.genus : genusName.Trim();
        string resolvedSpecies = string.IsNullOrWhiteSpace(speciesName) ? genome.species : speciesName.Trim();

        PlantGenomeState state = new PlantGenomeState
        {
            genus = resolvedGenus,
            species = resolvedSpecies,
            origin = null,
            dominantDNA = genome.dominantDNA
        };

        if (genome.maternalDNA != null && genome.maternalDNA.Count > 0)
        {
            state.maternalDNA = new List<AlleleState>(genome.maternalDNA.Count);
            for (int i = 0; i < genome.maternalDNA.Count; i++)
            {
                Allele a = genome.maternalDNA[i];
                state.maternalDNA.Add(new AlleleState { value = a.value, dominance = a.dominance, traitId = a.traitId });
            }
        }

        if (genome.paternalDNA != null && genome.paternalDNA.Count > 0)
        {
            state.paternalDNA = new List<AlleleState>(genome.paternalDNA.Count);
            for (int i = 0; i < genome.paternalDNA.Count; i++)
            {
                Allele a = genome.paternalDNA[i];
                state.paternalDNA.Add(new AlleleState { value = a.value, dominance = a.dominance, traitId = a.traitId });
            }
        }

        return state;
    }

    private static string GetLocalSpeciesDirectoryPath()
    {
        return Path.Combine(Application.persistentDataPath, LOCAL_SPECIES_FOLDER);
    }

    private static string GetLocalSpeciesFilePath(string genus, string species)
    {
        string safeGenus = SanitizeFileName(genus);
        string safeSpecies = SanitizeFileName(species);
        string filename = safeGenus + "_" + safeSpecies + LOCAL_SPECIES_FILE_EXTENSION;
        return Path.Combine(GetLocalSpeciesDirectoryPath(), filename);
    }

    private static string SanitizeFileName(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "unknown";

        string result = input.Trim();
        char[] invalid = Path.GetInvalidFileNameChars();
        for (int i = 0; i < invalid.Length; i++)
        {
            result = result.Replace(invalid[i], '_');
        }
        return result;
    }

    private static bool TryNormalizeTaxonomyNames(string genusName, string speciesName, out string genus, out string species)
    {
        genus = string.IsNullOrWhiteSpace(genusName) ? string.Empty : genusName.Trim();
        species = string.IsNullOrWhiteSpace(speciesName) ? string.Empty : speciesName.Trim();

        if (string.IsNullOrEmpty(genus) || string.IsNullOrEmpty(species))
            return false;

        string sanitizedGenus = SanitizeFileName(genus);
        if (string.IsNullOrEmpty(sanitizedGenus) || sanitizedGenus == "unknown")
            return false;

        return true;
    }

    private bool HasSanitizedSpeciesFileCollision(string genus, string species)
    {
        if (string.IsNullOrWhiteSpace(genus) || string.IsNullOrWhiteSpace(species))
            return false;

        string sanitizedTarget = SanitizeFileName(genus) + "_" + SanitizeFileName(species);
        foreach (KeyValuePair<string, PlantGenomeState> kvp in Local_SpeciesGenomes)
        {
            PlantGenomeState existingGenome = kvp.Value;
            if (existingGenome == null)
                continue;

            string existingGenus = existingGenome.genus;
            string existingSpecies = existingGenome.species;
            
            if (string.IsNullOrWhiteSpace(existingGenus) || string.IsNullOrWhiteSpace(existingSpecies))
                continue;

            if (existingGenus == genus && existingSpecies == species)
                continue;

            string existingSanitized = SanitizeFileName(existingGenus) + "_" + SanitizeFileName(existingSpecies);
            if (string.Equals(existingSanitized, sanitizedTarget, System.StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string FindExistingKeyIgnoreCase<TValue>(Dictionary<string, TValue> dictionary, string target)
    {
        if (dictionary == null || string.IsNullOrEmpty(target))
            return null;

        foreach (KeyValuePair<string, TValue> kvp in dictionary)
        {
            if (string.Equals(kvp.Key, target, System.StringComparison.OrdinalIgnoreCase))
            {
                return kvp.Key;
            }
        }

        return null;
    }

    private static string FindExistingSpeciesKeyIgnoreCase(Dictionary<string, PlantGenomeState> dictionary, string speciesId)
    {
        if (dictionary == null || string.IsNullOrEmpty(speciesId))
            return null;

        foreach (KeyValuePair<string, PlantGenomeState> kvp in dictionary)
        {
            if (string.Equals(kvp.Key, speciesId, System.StringComparison.OrdinalIgnoreCase))
            {
                return kvp.Key;
            }
        }

        return null;
    }

    private static string BuildSpeciesId(string genus, string species)
    {
        return genus + "_" + species;
    }

    [System.Serializable]
    private class LocalSpeciesData
    {
        public PlantGenomeState genome;
    }
}

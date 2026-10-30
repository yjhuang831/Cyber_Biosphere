using UnityEngine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Manages the user-facing local species library.
/// Each species is identified by a GUID; genus/species names are display labels kept unique by suffixing ("bar (2)").
/// Files are named by GUID, so renaming never moves files. Legacy files (named genus_species.json, no GUID) are
/// assigned a deterministic GUID derived from their content and upgraded in place.
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

    internal class SpeciesEntry
    {
        public string guid;
        public string genus;
        public string species;
        public string filePath;
        public string DisplayName => genus + " " + species;
    }

    private const string LOCAL_SPECIES_FOLDER = "species";
    private const string LOCAL_SPECIES_FILE_EXTENSION = ".json";
    private const int LIBRARY_FORMAT_VERSION = 2; // 1 = legacy (no GUID), 2 = GUID-identified
    private static readonly Regex NumericSuffixPattern = new Regex(@"^(.*\S)\s\((\d+)\)$");

    // Library index, keyed by species GUID (loaded on Awake; reads names only, genomes stay on disk)
    internal readonly Dictionary<string, SpeciesEntry> Local_Species = new Dictionary<string, SpeciesEntry>();
    // Full genome data for saved species (loaded on-demand, doesn't interrupt simulation), keyed by GUID
    internal readonly Dictionary<string, PlantGenomeState> Local_SpeciesGenomes = new Dictionary<string, PlantGenomeState>();
    // GUID of the species currently selected for planting
    internal string selectedSpeciesId;
    // Raised after any add/rename/delete/reload so UI lists can refresh
    internal event Action LibraryChanged;

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
    /// Rebuilds the library index from disk. Upgrades legacy files (adds GUID) and resolves name clashes by suffixing.
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

        Local_Species.Clear();
        Local_SpeciesGenomes.Clear();

        try
        {
            string[] speciesFiles = Directory.GetFiles(folderPath, "*" + LOCAL_SPECIES_FILE_EXTENSION, SearchOption.TopDirectoryOnly);
            Array.Sort(speciesFiles, StringComparer.OrdinalIgnoreCase); // Deterministic suffix assignment
            for (int i = 0; i < speciesFiles.Length; i++)
            {
                RegisterSpeciesFile(speciesFiles[i]);
            }
        }
        catch (Exception e)
        {
            Debug.LogError("Failed to load local species references: " + e.Message);
        }

        if (!string.IsNullOrEmpty(selectedSpeciesId) && !Local_Species.ContainsKey(selectedSpeciesId))
        {
            selectedSpeciesId = null;
        }
        LibraryChanged?.Invoke();
    }

    private void RegisterSpeciesFile(string filePath)
    {
        LocalSpeciesData speciesData;
        try
        {
            speciesData = JsonUtility.FromJson<LocalSpeciesData>(File.ReadAllText(filePath));
        }
        catch (Exception e)
        {
            Debug.LogWarning("Skipped local species file due to parse error: " + filePath + " | " + e.Message);
            return;
        }

        PlantGenomeState genome = speciesData != null ? speciesData.genome : null;
        if (genome == null || !HasDNA(genome))
        {
            Debug.LogWarning("Skipped local species file without genome data: " + filePath);
            return;
        }

        string genus = NormalizeName(genome.genus);
        string species = NormalizeName(genome.species);
        if (string.IsNullOrEmpty(genus) || string.IsNullOrEmpty(species))
        {
            Debug.LogWarning("Skipped local species file without genus/species names: " + filePath);
            return;
        }

        bool needsRewrite = speciesData.formatVersion < LIBRARY_FORMAT_VERSION;
        string guid = NormalizeGuid(genome.speciesGuid);
        if (guid == null)
        {
            guid = BuildContentGuid(genus, species, genome);
            needsRewrite = true;
        }

        if (Local_Species.TryGetValue(guid, out SpeciesEntry existing))
        {
            Debug.LogWarning("Skipped species file '" + filePath + "': same species GUID as '" + existing.filePath + "' (duplicate copy?).");
            return;
        }

        string uniqueSpecies = MakeUniqueSpeciesName(genus, species, guid);
        if (uniqueSpecies != species)
        {
            Debug.LogWarning("Species '" + genus + " " + species + "' clashes with an existing name; listed as '" + genus + " " + uniqueSpecies + "'.");
            species = uniqueSpecies;
            needsRewrite = true;
        }

        genome.genus = genus;
        genome.species = species;
        genome.speciesGuid = guid;

        if (needsRewrite)
        {
            try
            {
                WriteSpeciesFile(filePath, genome);
            }
            catch (Exception e)
            {
                Debug.LogWarning("Could not upgrade species file '" + filePath + "' (kept in memory only): " + e.Message);
            }
        }

        Local_Species[guid] = new SpeciesEntry { guid = guid, genus = genus, species = species, filePath = filePath };
    }

    /// <summary>
    /// Copies default species from StreamingAssets/species to persistent storage on first run.
    /// </summary>
    private static void CopyDefaultSpeciesFromStreamingAssets(string targetFolder)
    {
        string streamingPath = Path.Combine(Application.streamingAssetsPath, "species");

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
                string fileName = Path.GetFileName(filePath);
                string destPath = Path.Combine(targetFolder, fileName);
                File.Copy(filePath, destPath, overwrite: false);
            }
            Debug.Log("Copied " + defaultFiles.Length + " default species from StreamingAssets.");
        }
        catch (Exception e)
        {
            Debug.LogWarning("Failed to copy default species from StreamingAssets: " + e.Message);
        }
    }

    internal bool TryGetSpeciesEntry(string speciesGuid, out SpeciesEntry entry)
    {
        entry = null;
        return !string.IsNullOrEmpty(speciesGuid) && Local_Species.TryGetValue(speciesGuid, out entry);
    }

    /// <summary>
    /// Selects a species from the saved list for use during simulation.
    /// </summary>
    internal void SelectSpecies(string speciesGuid)
    {
        if (string.IsNullOrEmpty(speciesGuid) || !Local_Species.ContainsKey(speciesGuid))
            return;
        selectedSpeciesId = speciesGuid;
    }

    /// <summary>
    /// Deletes a saved species file. Plants already in a simulation keep their genome and GUID.
    /// </summary>
    internal void DeleteSavedSpecies(string speciesGuid)
    {
        if (!TryGetSpeciesEntry(speciesGuid, out SpeciesEntry entry))
            return;

        try
        {
            if (File.Exists(entry.filePath))
            {
                File.Delete(entry.filePath);
            }
            Local_Species.Remove(speciesGuid);
            Local_SpeciesGenomes.Remove(speciesGuid);
            if (selectedSpeciesId == speciesGuid)
            {
                selectedSpeciesId = null;
            }
            Debug.Log("Deleted saved species: " + entry.DisplayName);
            LibraryChanged?.Invoke();
        }
        catch (Exception e)
        {
            Debug.LogError("Failed to delete species \"" + entry.DisplayName + "\": " + e.Message);
        }
    }

    /// <summary>
    /// Attempts to load a single saved species genome by GUID. Reads only the specific file needed.
    /// Caches the result in Local_SpeciesGenomes for future use.
    /// </summary>
    internal bool TryGetSavedSpeciesGenome(string speciesGuid, out PlantGenomeState genomeState)
    {
        genomeState = null;

        if (string.IsNullOrEmpty(speciesGuid))
            return false;

        // Check if already cached
        if (Local_SpeciesGenomes.TryGetValue(speciesGuid, out genomeState))
            return true;

        if (!TryGetSpeciesEntry(speciesGuid, out SpeciesEntry entry) || !File.Exists(entry.filePath))
            return false;

        try
        {
            LocalSpeciesData speciesData = JsonUtility.FromJson<LocalSpeciesData>(File.ReadAllText(entry.filePath));
            if (speciesData == null || speciesData.genome == null)
                return false;

            // The index is authoritative for identity and names (covers files not yet upgraded on disk)
            genomeState = speciesData.genome;
            genomeState.speciesGuid = entry.guid;
            genomeState.genus = entry.genus;
            genomeState.species = entry.species;
            Local_SpeciesGenomes[speciesGuid] = genomeState;
            return true;
        }
        catch (Exception e)
        {
            Debug.LogWarning("Failed to load species genome '" + entry.DisplayName + "': " + e.Message);
            return false;
        }
    }

    /// <summary>
    /// Saves a genome as a new library species with a fresh GUID. If the name is already taken, a numeric
    /// suffix is added to the species epithet ("bar" -> "bar (2)"); the saved entry (final name, GUID) is returned.
    /// </summary>
    internal bool SaveLocalSpeciesGenome(PlantGenome genome, string genusName, string speciesName, out SpeciesEntry savedEntry)
    {
        savedEntry = null;
        if (genome == null || ((genome.maternalDNA == null || genome.maternalDNA.Count == 0) && (genome.paternalDNA == null || genome.paternalDNA.Count == 0)))
        {
            Debug.LogWarning("SaveLocalSpeciesGenome skipped: genome is null or has no DNA.");
            return false;
        }

        string genus = NormalizeName(genusName);
        string species = NormalizeName(speciesName);
        if (string.IsNullOrEmpty(genus) || string.IsNullOrEmpty(species))
        {
            Debug.LogWarning("SaveLocalSpeciesGenome skipped: genus or species name is empty.");
            return false;
        }

        string guid = Guid.NewGuid().ToString("N");
        species = MakeUniqueSpeciesName(genus, species, guid);
        PlantGenomeState state = SerializeGenomeForLocalStore(genome, genus, species, guid);
        string filePath = Path.Combine(GetLocalSpeciesDirectoryPath(), guid + LOCAL_SPECIES_FILE_EXTENSION);

        try
        {
            WriteSpeciesFile(filePath, state);
        }
        catch (Exception e)
        {
            Debug.LogError("SaveLocalSpeciesGenome failed: " + e.Message);
            return false;
        }

        SpeciesEntry entry = new SpeciesEntry { guid = guid, genus = genus, species = species, filePath = filePath };
        Local_Species[guid] = entry;
        Local_SpeciesGenomes[guid] = state;
        savedEntry = entry;
        LibraryChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// Renames a library species (for the future rename UI). The GUID is unchanged, so saves and living plants
    /// stay linked; a clashing name gets a numeric suffix. Also updates names on genomes of that species in the
    /// running simulation, if one is loaded. Returns the final display name.
    /// </summary>
    internal bool RenameSpecies(string speciesGuid, string newGenusName, string newSpeciesName, out string finalDisplayName)
    {
        finalDisplayName = null;
        if (!TryGetSpeciesEntry(speciesGuid, out SpeciesEntry entry))
        {
            Debug.LogWarning("RenameSpecies skipped: unknown species GUID '" + speciesGuid + "'.");
            return false;
        }

        string genus = NormalizeName(newGenusName);
        string species = NormalizeName(newSpeciesName);
        if (string.IsNullOrEmpty(genus) || string.IsNullOrEmpty(species))
        {
            Debug.LogWarning("RenameSpecies skipped: genus or species name is empty.");
            return false;
        }

        if (!TryGetSavedSpeciesGenome(speciesGuid, out PlantGenomeState state))
        {
            Debug.LogWarning("RenameSpecies failed: could not read species file for '" + entry.DisplayName + "'.");
            return false;
        }

        species = MakeUniqueSpeciesName(genus, species, speciesGuid);
        string oldGenus = entry.genus;
        string oldSpecies = entry.species;

        state.genus = genus;
        state.species = species;
        try
        {
            WriteSpeciesFile(entry.filePath, state);
        }
        catch (Exception e)
        {
            state.genus = oldGenus;
            state.species = oldSpecies;
            Debug.LogError("RenameSpecies failed: " + e.Message);
            return false;
        }

        entry.genus = genus;
        entry.species = species;
        finalDisplayName = entry.DisplayName;

        GrassManager grassManager = FindFirstObjectByType<GrassManager>();
        if (grassManager != null)
        {
            grassManager.ApplySpeciesRename(speciesGuid, oldSpecies, genus, species);
        }

        LibraryChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// Returns the species epithet, suffixed if needed so "genus species" is unique in the library
    /// (case-insensitive), ignoring the entry with excludeGuid. An existing " (n)" suffix is replaced, not stacked.
    /// </summary>
    internal string MakeUniqueSpeciesName(string genus, string species, string excludeGuid)
    {
        if (!IsNameTaken(genus, species, excludeGuid))
            return species;

        string baseSpecies = species;
        Match match = NumericSuffixPattern.Match(species);
        if (match.Success)
        {
            baseSpecies = match.Groups[1].Value;
        }

        for (int n = 2; ; n++)
        {
            string candidate = baseSpecies + " (" + n + ")";
            if (!IsNameTaken(genus, candidate, excludeGuid))
                return candidate;
        }
    }

    private bool IsNameTaken(string genus, string species, string excludeGuid)
    {
        foreach (SpeciesEntry entry in Local_Species.Values)
        {
            if (entry.guid == excludeGuid)
                continue;
            if (string.Equals(entry.genus, genus, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.species, species, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static void WriteSpeciesFile(string filePath, PlantGenomeState genome)
    {
        LocalSpeciesData speciesData = new LocalSpeciesData
        {
            formatVersion = LIBRARY_FORMAT_VERSION,
            savedAtUtc = DateTime.UtcNow.ToString("O"),
            genome = genome
        };
        SaveFileUtility.WriteAllTextAtomic(filePath, JsonUtility.ToJson(speciesData, true));
    }

    private static PlantGenomeState SerializeGenomeForLocalStore(PlantGenome genome, string genus, string species, string speciesGuid)
    {
        PlantGenomeState state = new PlantGenomeState
        {
            genus = genus,
            species = species,
            origin = null,
            speciesGuid = speciesGuid
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

    /// <summary>
    /// Deterministic GUID for legacy files: the same default species gets the same GUID on every install,
    /// so saves made on one machine still match the library on another.
    /// </summary>
    private static string BuildContentGuid(string genus, string species, PlantGenomeState genome)
    {
        StringBuilder canonical = new StringBuilder();
        canonical.Append(genus).Append('|').Append(species).Append('|');
        AppendAlleles(canonical, genome.maternalDNA);
        canonical.Append('|');
        AppendAlleles(canonical, genome.paternalDNA);

        using (MD5 md5 = MD5.Create())
        {
            byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(canonical.ToString()));
            return new Guid(hash).ToString("N");
        }
    }

    private static void AppendAlleles(StringBuilder builder, List<AlleleState> alleles)
    {
        if (alleles == null)
            return;
        for (int i = 0; i < alleles.Count; i++)
        {
            builder.Append(alleles[i].traitId).Append(':').Append(alleles[i].value).Append(':').Append(alleles[i].dominance ? '1' : '0').Append(';');
        }
    }

    private static bool HasDNA(PlantGenomeState genome)
    {
        return (genome.maternalDNA != null && genome.maternalDNA.Count > 0)
            || (genome.paternalDNA != null && genome.paternalDNA.Count > 0);
    }

    private static string NormalizeName(string name)
    {
        return string.IsNullOrWhiteSpace(name) ? string.Empty : Regex.Replace(name.Trim(), @"\s+", " ");
    }

    private static string NormalizeGuid(string guid)
    {
        return !string.IsNullOrWhiteSpace(guid) && Guid.TryParse(guid.Trim(), out Guid parsed) ? parsed.ToString("N") : null;
    }

    private static string GetLocalSpeciesDirectoryPath()
    {
        return Path.Combine(Application.persistentDataPath, LOCAL_SPECIES_FOLDER);
    }

    [Serializable]
    private class LocalSpeciesData
    {
        public int formatVersion;
        public string savedAtUtc;
        public PlantGenomeState genome;
    }
}

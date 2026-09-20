using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// JSON snapshot loader for full simulation state.
/// Supports direct in-scene loads and queued loads from the main menu.
/// </summary>
public static class SimulationLoadSystem
{
    private static string pendingSnapshotPath;
    public static string SimulationName { get; private set; } = string.Empty;

    public static void ClearPendingSnapshotLoad()
    {
        pendingSnapshotPath = null;
        SimulationName = string.Empty;
    }

    public static void SetSimulationNameFromSaveName(string saveNameOrPath)
    {
        if (string.IsNullOrWhiteSpace(saveNameOrPath))
        {
            SimulationName = string.Empty;
            return;
        }

        string trimmed = saveNameOrPath.Trim();
        string name = Path.GetFileNameWithoutExtension(trimmed);
        SimulationName = string.IsNullOrWhiteSpace(name) ? string.Empty : name;
    }

    public static bool QueueSnapshotLoadByIdentifier(string fileNameOrPath)
    {
        if (!TryResolveSnapshotPath(fileNameOrPath, out string resolvedPath))
        {
            SimulationName = string.Empty;
            Debug.LogWarning("Queue load failed: could not resolve save file from input '" + fileNameOrPath + "'.");
            return false;
        }

        pendingSnapshotPath = resolvedPath;
        Debug.Log("Queued snapshot load: " + pendingSnapshotPath);
        return true;
    }

    public static bool TryResolveSnapshotPathByIdentifier(string fileNameOrPath, out string resolvedPath)
    {
        return TryResolveSnapshotPath(fileNameOrPath, out resolvedPath);
    }

    public static bool TryApplyPendingSnapshot(
        TimeController timeController,
        EnvironmentManager environmentManager,
        GrassManager grassManager,
        SeedManager seedManager)
    {
        if (string.IsNullOrEmpty(pendingSnapshotPath))
        {
            return false;
        }

        bool loaded = LoadSnapshotFromPath(
            pendingSnapshotPath,
            timeController,
            environmentManager,
            grassManager,
            seedManager);

        if (loaded)
        {
            pendingSnapshotPath = null;
        }

        return loaded;
    }

    public static bool LoadSnapshotByIdentifier(
        string fileNameOrPath,
        TimeController timeController,
        EnvironmentManager environmentManager,
        GrassManager grassManager,
        SeedManager seedManager)
    {
        if (!TryResolveSnapshotPath(fileNameOrPath, out string resolvedPath))
        {
            Debug.LogWarning("Load failed: could not resolve save file from input '" + fileNameOrPath + "'.");
            return false;
        }

        return LoadSnapshotFromPath(resolvedPath, timeController, environmentManager, grassManager, seedManager);
    }

    public static bool LoadSnapshotFromPath(
        string snapshotPath,
        TimeController timeController,
        EnvironmentManager environmentManager,
        GrassManager grassManager,
        SeedManager seedManager)
    {
        if (timeController == null || environmentManager == null || grassManager == null || seedManager == null)
        {
            Debug.LogWarning("Load skipped: one or more required manager references are missing.");
            return false;
        }

        if (string.IsNullOrEmpty(snapshotPath))
        {
            Debug.LogWarning("Load skipped: snapshot path is empty.");
            return false;
        }

        if (!File.Exists(snapshotPath))
        {
            Debug.LogWarning("Load skipped: snapshot file does not exist: " + snapshotPath);
            return false;
        }

        MapManager mapManager = environmentManager.mapManager != null ? environmentManager.mapManager : grassManager.mapManager;
        if (mapManager == null)
        {
            Debug.LogWarning("Load skipped: MapManager reference is missing.");
            return false;
        }

        try
        {
            string json = File.ReadAllText(snapshotPath);
            SimulationSnapshot snapshot = JsonUtility.FromJson<SimulationSnapshot>(json);
            if (snapshot == null)
            {
                Debug.LogError("Load failed: snapshot JSON is empty or invalid.");
                return false;
            }

            if (snapshot.schemaVersion != SimulationSnapshotConstants.SchemaVersion)
            {
                Debug.LogError("Load failed: schema mismatch. Snapshot " + snapshot.schemaVersion + ", expected " + SimulationSnapshotConstants.SchemaVersion + ".");
                return false;
            }

            if (snapshot.map == null || snapshot.tiles == null)
            {
                Debug.LogError("Load failed: snapshot is missing map or tile data.");
                return false;
            }

            ApplySnapshot(snapshot, timeController, mapManager, environmentManager, grassManager, seedManager);
            SetSimulationNameFromSaveName(snapshotPath);
            Debug.Log("Load complete: " + snapshotPath);
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError("Load failed: " + ex.Message);
            return false;
        }
    }

    private static void ApplySnapshot(
        SimulationSnapshot snapshot,
        TimeController timeController,
        MapManager mapManager,
        EnvironmentManager environmentManager,
        GrassManager grassManager,
        SeedManager seedManager)
    {
        ApplyEnvironmentSettings(snapshot.environment, environmentManager);
        mapManager.ReinitializeForLoad(snapshot.map.width, snapshot.map.height);
        ApplyGrassSettings(snapshot.grassSettings, grassManager, seedManager);

        if (snapshot.time != null && snapshot.time.daysPerYear > 0)
        {
            SeasonManager.daysPerYear = snapshot.time.daysPerYear;
        }

        grassManager.activeGrassInstances.Clear();
        seedManager.activeSeeds.Clear();

        long loadedPlantCount = 0;
        long loadedSeedCount = 0;

        Dictionary<long, TileState> tileLookup = BuildTileLookup(snapshot.tiles);
        List<Vector3Int> allTiles = mapManager.GetAllTilePositions();

        GeneManager geneManager = UnityEngine.Object.FindFirstObjectByType<GeneManager>();
        if (geneManager == null)
        {
            Debug.LogWarning("GeneManager not found during load. Loaded genomes will use fallback phenotype defaults.");
        }

        for (int i = 0; i < allTiles.Count; i++)
        {
            Vector3Int tilePos = allTiles[i];
            tileLookup.TryGetValue(ToTileKey(tilePos.x, tilePos.y), out TileState tileState);

            bool isWater = tileState == null || tileState.isWater;
            mapManager.SetTileWaterStateForLoad(tilePos, isWater);

            EnvironmentData env = mapManager.GetTileEnvironmentData(tilePos);
            if (tileState != null)
            {
                env.oxygen = tileState.oxygen;
            }
            env.grass = null;

            if (tileState != null && tileState.grass != null)
            {
                if (TryGetGenomeStateInvalidReason(tileState.grass.genome, out _))
                {
                }
                else
                {
                    GrassInstance grass = DeserializeGrass(tileState.grass, tilePos, geneManager);
                    if (grass != null)
                    {
                        env.grass = grass;
                        grassManager.activeGrassInstances[tilePos] = grass;
                        loadedPlantCount++;
                    }
                }
            }

            mapManager.UpdateGrassTile(env, tilePos);

            if (tileState != null && tileState.seeds != null && tileState.seeds.Count > 0)
            {
                List<Seed> seeds = new List<Seed>(tileState.seeds.Count);
                for (int s = 0; s < tileState.seeds.Count; s++)
                {
                    SeedState seedState = tileState.seeds[s];
                    if (seedState == null)
                        continue;

                    if (TryGetGenomeStateInvalidReason(seedState.carriedGenome, out _))
                    {
                        continue;
                    }

                    Seed seed = DeserializeSeed(seedState, geneManager);
                    if (seed.carriedGenome == null)
                    {
                        continue;
                    }
                    seeds.Add(seed);
                    loadedSeedCount++;
                }

                if (seeds.Count > 0)
                {
                    seedManager.activeSeeds[tilePos] = seeds;
                }
            }
        }

        grassManager.plantCount = loadedPlantCount;
        seedManager.seedCount = loadedSeedCount;

        if (snapshot.counters != null)
        {
            if (snapshot.counters.plantCount != loadedPlantCount)
            {
                Debug.LogWarning("Loaded plant count mismatch. Snapshot counter: " + snapshot.counters.plantCount + ", reconstructed: " + loadedPlantCount + ".");
            }
            if (snapshot.counters.seedCount != loadedSeedCount)
            {
                Debug.LogWarning("Loaded seed count mismatch. Snapshot counter: " + snapshot.counters.seedCount + ", reconstructed: " + loadedSeedCount + ".");
            }
        }

        if (snapshot.time != null)
        {
            float loadedBaseLight = snapshot.environment != null ? snapshot.environment.baseLightLevel : environmentManager.baseLightLevel;
            timeController.ApplyLoadedTimeState(
                snapshot.time.simDaysSinceStart,
                snapshot.time.accumulatedAge,
                snapshot.time.isNight,
                snapshot.time.totalSimulatedTimeSeconds,
                snapshot.time.noonTimeHours,
                loadedBaseLight);
        }

        // Recompute tile-level environment from restored base settings/topology instead of loading cached per-tile values.
        environmentManager.UpdateTileEnvironment(null, true);
        environmentManager.VisualUpdate();
    }

    private static void ApplyEnvironmentSettings(EnvironmentSettingsState source, EnvironmentManager target)
    {
        if (source == null || target == null)
            return;

        target.baseTemperature = source.baseTemperature;
        target.hydroOffset = source.hydroOffset;
        target.lightOffset = source.lightOffset;
        target.seasonalTemperatureOffset = source.seasonalTemperatureOffset;
        target.minTemperature = source.minTemperature;
        target.maxTemperature = source.maxTemperature;
        target.equatorialBoost = source.equatorialBoost;
        target.polarPenalty = source.polarPenalty;
        target.baseLandHydration = source.baseLandHydration;
        target.waterRadiation = source.waterRadiation;
        target.landRadiation = source.landRadiation;
        target.waterOxygen = source.waterOxygen;
        target.landOxygen = source.landOxygen;
        target.dayNightCycleSpeed = source.dayNightCycleSpeed;
        target.baseLightLevel = source.baseLightLevel;
        target.waterMaxLightLevel = source.waterMaxLightLevel;
        target.landMaxLightLevel = source.landMaxLightLevel;
    }

    private static void ApplyGrassSettings(GrassSettingsState source, GrassManager target, SeedManager seedTarget)
    {
        if (source == null || target == null)
            return;

        target.metabolismCostPerAllele = source.metabolismCostPerAllele;
        target.plantAging = source.plantAging;
        target.agingRate = source.agingRate;
        target.baseGrowthCost = source.baseGrowthCost;
        target.flowerLimit = source.flowerLimit;
        seedTarget.maxSeedsPerTile = source.maxSeedsPerTile;
    }

    private static Dictionary<long, TileState> BuildTileLookup(List<TileState> tiles)
    {
        Dictionary<long, TileState> lookup = new Dictionary<long, TileState>(tiles != null ? tiles.Count : 0);
        if (tiles == null)
            return lookup;

        for (int i = 0; i < tiles.Count; i++)
        {
            TileState tile = tiles[i];
            if (tile == null)
                continue;
            lookup[ToTileKey(tile.x, tile.y)] = tile;
        }
        return lookup;
    }

    private static long ToTileKey(int x, int y)
    {
        return ((long)x << 32) | (uint)y;
    }

    private static GrassInstance DeserializeGrass(GrassState source, Vector3Int tilePos, GeneManager geneManager)
    {
        if (source == null)
            return null;

        if (TryGetGenomeStateInvalidReason(source.genome, out _))
            return null;

        PlantGenome genome = DeserializeGenome(source.genome, geneManager);
        if (genome == null)
            return null;

        GrassInstance grass = new GrassInstance(tilePos, genome, source.energyStored);
        grass.energyStored = source.energyStored;
        grass.grassDensity = source.grassDensity;
        grass.age = source.age;
        grass.ageEffect = source.ageEffect;
        grass.dormancyState = (DormancyState)source.dormancyState;
        grass.dormancyEffect = source.dormancyEffect;
        grass.dormancyTimer = source.dormancyTimer;
        grass.maxDormantTime = source.maxDormantTime;
        grass.floweringDays = source.floweringDays;
        grass.reproductiveState = (ReproductiveState)source.reproductiveState;
        grass.flowersThisMonth = source.flowersThisMonth;
        grass.lastFlowerMonth = source.lastFlowerMonth;
        grass.maturityThreshold = source.maturityThreshold;
        grass.seedingEnergy = source.seedingEnergy;
        grass.usedPhotosynthesisLastTick = source.usedPhotosynthesisLastTick;
        grass.usedRespirationLastTick = source.usedRespirationLastTick;
        grass.seedGenome = DeserializeGenome(source.seedGenome, geneManager);
        return grass;
    }

    private static Seed DeserializeSeed(SeedState source, GeneManager geneManager)
    {
        if (source == null)
            return default;

        if (TryGetGenomeStateInvalidReason(source.carriedGenome, out _))
            return default;

        return new Seed
        {
            parentGenus = source.parentGenus,
            storedEnergy = source.storedEnergy,
            germinationEnergy = source.germinationEnergy,
            germinationStage = source.germinationStage,
            sproutStage = source.sproutStage,
            carriedGenome = DeserializeGenome(source.carriedGenome, geneManager)
        };
    }

    private static PlantGenome DeserializeGenome(PlantGenomeState source, GeneManager geneManager)
    {
        if (TryGetGenomeStateInvalidReason(source, out _))
            return null;

        PlantGenome genome = new PlantGenome
        {
            genus = source.genus,
            species = source.species,
            origin = source.origin,
            generation = source.generation,
            genusColor = source.genusColor != null
                ? new Color(source.genusColor.r, source.genusColor.g, source.genusColor.b, source.genusColor.a)
                : Color.white
        };

        genome.maternalDNA = DeserializeAlleles(source.maternalDNA);
        genome.paternalDNA = DeserializeAlleles(source.paternalDNA);

        bool useMaternalDominant = source.dominantDNA != 1;
        genome.SetDominantDNA(useMaternalDominant);

        if (geneManager != null)
        {
            geneManager.RebuildLoadedGenomeDerivedData(genome);
        }
        else
        {
            genome.phenotypes = new float[TraitId.SeedingProportion + 1];
        }

        return genome;
    }

    private static bool TryGetGenomeStateInvalidReason(PlantGenomeState source, out string reason)
    {
        if (source == null)
        {
            reason = "genome payload is null";
            return true;
        }

        if (string.IsNullOrWhiteSpace(source.origin) && (string.IsNullOrWhiteSpace(source.genus) || string.IsNullOrWhiteSpace(source.species)))
        {
            reason = "origin and taxonomy are blank";
            return true;
        }

        bool hasMaternal = source.maternalDNA != null && source.maternalDNA.Count > 0;
        bool hasPaternal = source.paternalDNA != null && source.paternalDNA.Count > 0;
        if (!hasMaternal && !hasPaternal)
        {
            reason = "both maternal and paternal DNA are empty";
            return true;
        }

        reason = null;
        return false;
    }

    private static List<Allele> DeserializeAlleles(List<AlleleState> source)
    {
        if (source == null || source.Count == 0)
            return new List<Allele>();

        List<Allele> result = new List<Allele>(source.Count);
        for (int i = 0; i < source.Count; i++)
        {
            AlleleState a = source[i];
            result.Add(new Allele(a.value, a.dominance, a.traitId));
        }
        return result;
    }

    private static string GetAutosaveDirectoryPath()
    {
        return Path.Combine(Application.persistentDataPath, SimulationSnapshotConstants.AutosaveFolder);
    }

    private static string GetManualSaveDirectoryPath()
    {
        return Path.Combine(Application.persistentDataPath, SimulationSnapshotConstants.ManualSaveFolder);
    }

    private static bool TryResolveSnapshotPath(string fileNameOrPath, out string resolvedPath)
    {
        resolvedPath = null;
        if (string.IsNullOrWhiteSpace(fileNameOrPath))
            return false;

        string input = fileNameOrPath.Trim();
        string withExtension = input.EndsWith(SimulationSnapshotConstants.SnapshotFileExtension, StringComparison.OrdinalIgnoreCase)
            ? input
            : input + SimulationSnapshotConstants.SnapshotFileExtension;

        if (Path.IsPathRooted(input))
        {
            if (File.Exists(input))
            {
                resolvedPath = input;
                return true;
            }
            if (File.Exists(withExtension))
            {
                resolvedPath = withExtension;
                return true;
            }
            return false;
        }

        if (ContainsDirectorySeparator(input))
        {
            string rootedRelative = Path.Combine(Application.persistentDataPath, input);
            if (File.Exists(rootedRelative))
            {
                resolvedPath = rootedRelative;
                return true;
            }

            string rootedRelativeWithExtension = Path.Combine(Application.persistentDataPath, withExtension);
            if (File.Exists(rootedRelativeWithExtension))
            {
                resolvedPath = rootedRelativeWithExtension;
                return true;
            }
        }

        string autosaveCandidate = Path.Combine(GetAutosaveDirectoryPath(), withExtension);
        if (File.Exists(autosaveCandidate))
        {
            resolvedPath = autosaveCandidate;
            return true;
        }

        string manualCandidate = Path.Combine(GetManualSaveDirectoryPath(), withExtension);
        if (File.Exists(manualCandidate))
        {
            resolvedPath = manualCandidate;
            return true;
        }

        return false;
    }

    private static bool ContainsDirectorySeparator(string input)
    {
        return input.IndexOf(Path.DirectorySeparatorChar) >= 0 || input.IndexOf(Path.AltDirectorySeparatorChar) >= 0;
    }

}

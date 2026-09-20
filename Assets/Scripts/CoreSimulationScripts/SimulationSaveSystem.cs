using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// JSON snapshot writer for full simulation state.
/// Save entry points only; load behavior lives in SimulationLoadSystem.
/// </summary>
public static class SimulationSaveSystem
{
    public static bool SaveAutoSnapshot(
        TimeController timeController,
        EnvironmentManager environmentManager,
        GrassManager grassManager,
        SeedManager seedManager,
        float totalSimulatedTimeSeconds)
    {
        if (timeController == null || environmentManager == null || grassManager == null || seedManager == null)
        {
            Debug.LogWarning("Autosave skipped: one or more required manager references are missing.");
            return false;
        }

        MapManager mapManager = environmentManager.mapManager != null ? environmentManager.mapManager : grassManager.mapManager;
        if (mapManager == null)
        {
            Debug.LogWarning("Autosave skipped: MapManager reference is missing.");
            return false;
        }

        try
        {
            SimulationSnapshot snapshot = BuildSnapshot(
                timeController,
                mapManager,
                environmentManager,
                grassManager,
                seedManager,
                totalSimulatedTimeSeconds);

            string autosavePath = BuildTimestampedAutosavePath(DateTime.Now);
            WriteSnapshotFile(autosavePath, snapshot);
            PruneAutosavesToLimit(SimulationSnapshotConstants.MaxAutosaveFiles);
            Debug.Log("Autosave complete: " + autosavePath);
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError("Autosave failed: " + ex.Message);
            return false;
        }
    }

    public static void PruneAutosavesToCurrentLimit()
    {
        PruneAutosavesToLimit(SimulationSnapshotConstants.MaxAutosaveFiles);
    }

    public static bool SaveManualSnapshot(
        TimeController timeController,
        EnvironmentManager environmentManager,
        GrassManager grassManager,
        SeedManager seedManager,
        float totalSimulatedTimeSeconds,
        string playerSaveName)
    {
        if (timeController == null || environmentManager == null || grassManager == null || seedManager == null)
        {
            Debug.LogWarning("Manual save skipped: one or more required manager references are missing.");
            return false;
        }

        string sanitizedName = SanitizeFileName(playerSaveName);
        if (string.IsNullOrWhiteSpace(sanitizedName))
        {
            Debug.LogWarning("Manual save skipped: save name is empty or invalid.");
            return false;
        }

        MapManager mapManager = environmentManager.mapManager != null ? environmentManager.mapManager : grassManager.mapManager;
        if (mapManager == null)
        {
            Debug.LogWarning("Manual save skipped: MapManager reference is missing.");
            return false;
        }

        try
        {
            SimulationSnapshot snapshot = BuildSnapshot(
                timeController,
                mapManager,
                environmentManager,
                grassManager,
                seedManager,
                totalSimulatedTimeSeconds);

            string manualPath = BuildManualSavePath(sanitizedName);
            WriteSnapshotFile(manualPath, snapshot);
            Debug.Log("Manual save complete: " + manualPath);
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError("Manual save failed: " + ex.Message);
            return false;
        }
    }

    private static SimulationSnapshot BuildSnapshot(
        TimeController timeController,
        MapManager mapManager,
        EnvironmentManager environmentManager,
        GrassManager grassManager,
        SeedManager seedManager,
        float totalSimulatedTimeSeconds)
    {
        SimulationSnapshot snapshot = new SimulationSnapshot
        {
            schemaVersion = SimulationSnapshotConstants.SchemaVersion,
            savedAtUtc = DateTime.UtcNow.ToString("O"),
            map = new MapState
            {
                width = mapManager.width,
                height = mapManager.height
            },
            environment = new EnvironmentSettingsState
            {
                baseTemperature = environmentManager.baseTemperature,
                hydroOffset = environmentManager.hydroOffset,
                lightOffset = environmentManager.lightOffset,
                seasonalTemperatureOffset = environmentManager.seasonalTemperatureOffset,
                minTemperature = environmentManager.minTemperature,
                maxTemperature = environmentManager.maxTemperature,
                equatorialBoost = environmentManager.equatorialBoost,
                polarPenalty = environmentManager.polarPenalty,
                baseLandHydration = environmentManager.baseLandHydration,
                waterRadiation = environmentManager.waterRadiation,
                landRadiation = environmentManager.landRadiation,
                waterOxygen = environmentManager.waterOxygen,
                landOxygen = environmentManager.landOxygen,
                dayNightCycleSpeed = environmentManager.dayNightCycleSpeed,
                baseLightLevel = environmentManager.baseLightLevel,
                waterMaxLightLevel = environmentManager.waterMaxLightLevel,
                landMaxLightLevel = environmentManager.landMaxLightLevel
            },
            time = new TimeState
            {
                simDaysSinceStart = TimeController.simDaysSinceStart,
                accumulatedAge = TimeController.accumulatedAge,
                isNight = TimeController.isNight,
                totalSimulatedTimeSeconds = totalSimulatedTimeSeconds,
                noonTimeHours = timeController.noonTime,
                daysPerYear = SeasonManager.daysPerYear
            },
            counters = new CounterState(),
            grassSettings = new GrassSettingsState
            {
                metabolismCostPerAllele = grassManager.metabolismCostPerAllele,
                plantAging = grassManager.plantAging,
                agingRate = grassManager.agingRate,
                baseGrowthCost = grassManager.baseGrowthCost,
                flowerLimit = grassManager.flowerLimit,
                maxSeedsPerTile = seedManager.maxSeedsPerTile
            },
            tiles = new List<TileState>(mapManager.GetAllTilePositions().Count)
        };

        List<Vector3Int> allTiles = mapManager.GetAllTilePositions();
        long serializedPlantCount = 0;
        long serializedSeedCount = 0;
        for (int i = 0; i < allTiles.Count; i++)
        {
            Vector3Int tilePos = allTiles[i];
            EnvironmentData env = mapManager.GetTileEnvironmentData(tilePos);
            TileState tile = new TileState
            {
                x = tilePos.x,
                y = tilePos.y,
                isWater = env.isWater,
                oxygen = env.oxygen
            };

            if (grassManager.activeGrassInstances != null
                && grassManager.activeGrassInstances.TryGetValue(tilePos, out GrassInstance liveGrass)
                && liveGrass != null)
            {
                if (!TryGetGenomeSnapshotInvalidReason(liveGrass.genome, out _))
                {
                    tile.grass = SerializeGrass(liveGrass);
                    serializedPlantCount++;
                }
            }

            if (seedManager.activeSeeds != null && seedManager.activeSeeds.TryGetValue(tilePos, out List<Seed> seeds) && seeds != null && seeds.Count > 0)
            {
                List<SeedState> serializedSeeds = new List<SeedState>(seeds.Count);
                for (int s = 0; s < seeds.Count; s++)
                {
                    if (TryGetGenomeSnapshotInvalidReason(seeds[s].carriedGenome, out _))
                    {
                        continue;
                    }

                    SeedState serializedSeed = SerializeSeed(seeds[s]);
                    if (serializedSeed == null)
                    {
                        continue;
                    }

                    serializedSeeds.Add(serializedSeed);
                    serializedSeedCount++;
                }

                if (serializedSeeds.Count > 0)
                {
                    tile.seeds = serializedSeeds;
                }
            }

            snapshot.tiles.Add(tile);
        }

        snapshot.counters.plantCount = serializedPlantCount;
        snapshot.counters.seedCount = serializedSeedCount;

        return snapshot;
    }

    private static GrassState SerializeGrass(GrassInstance grass)
    {
        return new GrassState
        {
            tileX = grass.tilePosition.x,
            tileY = grass.tilePosition.y,
            energyStored = grass.energyStored,
            grassDensity = grass.grassDensity,
            age = grass.age,
            ageEffect = grass.ageEffect,
            dormancyState = (int)grass.dormancyState,
            dormancyEffect = grass.dormancyEffect,
            dormancyTimer = grass.dormancyTimer,
            maxDormantTime = grass.maxDormantTime,
            floweringDays = grass.floweringDays,
            reproductiveState = (int)grass.reproductiveState,
            flowersThisMonth = grass.flowersThisMonth,
            lastFlowerMonth = grass.lastFlowerMonth,
            maturityThreshold = grass.maturityThreshold,
            seedingEnergy = grass.seedingEnergy,
            usedPhotosynthesisLastTick = grass.usedPhotosynthesisLastTick,
            usedRespirationLastTick = grass.usedRespirationLastTick,
            genome = SerializeGenome(grass.genome),
            seedGenome = SerializeGenome(grass.seedGenome)
        };
    }

    private static SeedState SerializeSeed(Seed seed)
    {
        if (seed.carriedGenome == null || TryGetGenomeSnapshotInvalidReason(seed.carriedGenome, out _))
        {
            return null;
        }

        return new SeedState
        {
            parentGenus = seed.parentGenus,
            storedEnergy = seed.storedEnergy,
            germinationEnergy = seed.germinationEnergy,
            germinationStage = seed.germinationStage,
            sproutStage = seed.sproutStage,
            carriedGenome = SerializeGenome(seed.carriedGenome)
        };
    }

    private static PlantGenomeState SerializeGenome(PlantGenome genome)
    {
        if (TryGetGenomeSnapshotInvalidReason(genome, out _))
            return null;

        PlantGenomeState state = new PlantGenomeState
        {
            genus = genome.genus,
            species = genome.species,
            origin = genome.origin,
            generation = genome.generation,
            dominantDNA = genome.dominantDNA,
            genusColor = new ColorState
            {
                r = genome.genusColor.r,
                g = genome.genusColor.g,
                b = genome.genusColor.b,
                a = genome.genusColor.a
            }
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

    private static bool TryGetGenomeSnapshotInvalidReason(PlantGenome genome, out string reason)
    {
        if (genome == null)
        {
            reason = "genome is null";
            return true;
        }

        if (string.IsNullOrWhiteSpace(genome.origin) && (string.IsNullOrWhiteSpace(genome.genus) || string.IsNullOrWhiteSpace(genome.species)))
        {
            reason = "origin and taxonomy are blank";
            return true;
        }

        bool hasMaternal = genome.maternalDNA != null && genome.maternalDNA.Count > 0;
        bool hasPaternal = genome.paternalDNA != null && genome.paternalDNA.Count > 0;
        if (!hasMaternal && !hasPaternal)
        {
            reason = "both maternal and paternal DNA are empty";
            return true;
        }

        reason = null;
        return false;
    }

    private static string GetAutosaveDirectoryPath()
    {
        return Path.Combine(Application.persistentDataPath, SimulationSnapshotConstants.AutosaveFolder);
    }

    private static string GetManualSaveDirectoryPath()
    {
        return Path.Combine(Application.persistentDataPath, SimulationSnapshotConstants.ManualSaveFolder);
    }

    private static string BuildTimestampedAutosavePath(DateTime saveTimeLocal)
    {
        string safeTimestamp = saveTimeLocal.ToString(SimulationSnapshotConstants.TimestampFormat);
        string fileName = SimulationSnapshotConstants.AutosaveFilePrefix + safeTimestamp + SimulationSnapshotConstants.SnapshotFileExtension;
        string fullPath = Path.Combine(GetAutosaveDirectoryPath(), fileName);
        int suffix = 1;
        while (File.Exists(fullPath))
        {
            fileName = SimulationSnapshotConstants.AutosaveFilePrefix + safeTimestamp + "_" + suffix + SimulationSnapshotConstants.SnapshotFileExtension;
            fullPath = Path.Combine(GetAutosaveDirectoryPath(), fileName);
            suffix++;
        }
        return fullPath;
    }

    private static string BuildManualSavePath(string baseName)
    {
        string fileName = baseName + SimulationSnapshotConstants.SnapshotFileExtension;
        return Path.Combine(GetManualSaveDirectoryPath(), fileName);
    }

    private static void WriteSnapshotFile(string path, SimulationSnapshot snapshot)
    {
        string directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string json = JsonUtility.ToJson(snapshot, false);
        File.WriteAllText(path, json);
    }

    private static void PruneAutosavesToLimit(int maxFiles)
    {
        if (maxFiles <= 0)
            return;

        string directory = GetAutosaveDirectoryPath();
        if (!Directory.Exists(directory))
            return;

        string pattern = SimulationSnapshotConstants.AutosaveFilePrefix + "*" + SimulationSnapshotConstants.SnapshotFileExtension;
        string[] files = Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly);
        if (files.Length <= maxFiles)
            return;

        Array.Sort(files, (a, b) => File.GetLastWriteTimeUtc(a).CompareTo(File.GetLastWriteTimeUtc(b)));
        int filesToDelete = files.Length - maxFiles;
        for (int i = 0; i < filesToDelete; i++)
        {
            File.Delete(files[i]);
        }
    }

    private static string SanitizeFileName(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        char[] invalid = Path.GetInvalidFileNameChars();
        string result = input.Trim();
        for (int i = 0; i < invalid.Length; i++)
        {
            result = result.Replace(invalid[i], '_');
        }
        return result;
    }
}

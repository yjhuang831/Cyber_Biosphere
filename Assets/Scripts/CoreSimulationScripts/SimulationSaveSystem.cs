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
        SeedManager seedManager)
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
                seedManager);

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
                seedManager);

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
        SeedManager seedManager)
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
            environment = CaptureEnvironmentSettings(environmentManager),
            time = new TimeState
            {
                daysPerYear = SeasonManager.daysPerYear
            },
            counters = new CounterState(),
            grassSettings = CaptureGrassSettings(grassManager, seedManager),
            tiles = new List<TileState>(mapManager.GetAllTilePositions().Count),
            genomes = new List<PlantGenomeState>(),
            plants = new List<GrassState>(),
            seeds = new List<SeedState>()
        };

        timeController.WriteTimeState(snapshot.time);
        SeasonManager seasonManager = UnityEngine.Object.FindFirstObjectByType<SeasonManager>();
        snapshot.time.currentSeason = seasonManager != null ? seasonManager.CurrentSeason : 0;

        // Genomes are written once each; reference equality dedupes seeds sharing a parent's seed genome
        GenomeTableWriter genomeTable = new GenomeTableWriter(snapshot.genomes);

        List<Vector3Int> allTiles = mapManager.GetAllTilePositions();
        for (int i = 0; i < allTiles.Count; i++)
        {
            Vector3Int tilePos = allTiles[i];
            EnvironmentData env = mapManager.GetTileEnvironmentData(tilePos);
            snapshot.tiles.Add(new TileState
            {
                x = tilePos.x,
                y = tilePos.y,
                isWater = env.isWater,
                oxygen = env.oxygen,
                litterCarbon = env.litterCarbon,
                availableNitrogen = env.availableNitrogen,
                availablePhosphorus = env.availablePhosphorus,
                availablePotassium = env.availablePotassium
            });

            if (grassManager.activeGrassInstances != null
                && grassManager.activeGrassInstances.TryGetValue(tilePos, out GrassInstance liveGrass)
                && liveGrass != null)
            {
                int genomeIndex = genomeTable.IndexOf(liveGrass.genome);
                if (genomeIndex >= 0)
                {
                    snapshot.plants.Add(SerializeGrass(liveGrass, genomeIndex, genomeTable.IndexOf(liveGrass.seedGenome)));
                }
                else
                {
                    Debug.LogWarning("Save: skipped plant at " + tilePos + " because its genome has no DNA.");
                }
            }

            if (seedManager.activeSeeds != null && seedManager.activeSeeds.TryGetValue(tilePos, out List<Seed> seeds) && seeds != null)
            {
                for (int s = 0; s < seeds.Count; s++)
                {
                    int genomeIndex = genomeTable.IndexOf(seeds[s].carriedGenome);
                    if (genomeIndex >= 0)
                    {
                        snapshot.seeds.Add(SerializeSeed(seeds[s], tilePos, genomeIndex));
                    }
                }
            }
        }

        snapshot.counters.plantCount = snapshot.plants.Count;
        snapshot.counters.seedCount = snapshot.seeds.Count;

        snapshot.nextGenusId = grassManager.genusTracker.NextGenusId;
        snapshot.nextGenusNameIndex = grassManager.genusTracker.NextGenusNameIndex;
        snapshot.genera = new List<GenusState>(grassManager.genusTracker.Count);
        foreach (GenusRecord genus in grassManager.genusTracker.Genera)
        {
            GenusState state = new GenusState
            {
                id = genus.id,
                name = genus.name,
                isLibrary = genus.isLibrary,
                species = new List<SpeciesState>(genus.species.Count),
                nextSpeciesIndex = genus.nextSpeciesIndex,
                parentGenusId = genus.parentGenusId,
                foundedDay = genus.foundedDay,
                founderColor = new ColorState { r = genus.founderColor.r, g = genus.founderColor.g, b = genus.founderColor.b, a = genus.founderColor.a },
                representativeGenomeIndices = new List<int>(GenusTracker.REPRESENTATIVES)
            };
            for (int i = 0; i < genus.species.Count; i++)
            {
                state.species.Add(new SpeciesState { name = genus.species[i].name, speciesGuid = genus.species[i].speciesGuid });
            }
            for (int i = 0; i < genus.representatives.Length; i++)
            {
                int genomeIndex = genomeTable.IndexOf(genus.representatives[i]);
                if (genomeIndex >= 0)
                {
                    state.representativeGenomeIndices.Add(genomeIndex);
                }
            }
            snapshot.genera.Add(state);
        }

        return snapshot;
    }

    /// <summary>
    /// Environment settings as saved in snapshots; also recorded in research run metadata.
    /// </summary>
    internal static EnvironmentSettingsState CaptureEnvironmentSettings(EnvironmentManager environmentManager)
    {
        return new EnvironmentSettingsState
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
            landMaxLightLevel = environmentManager.landMaxLightLevel,
            atmosphericCarbon = environmentManager.atmosphericCarbon
        };
    }

    /// <summary>
    /// Plant/seed rule settings as saved in snapshots; also recorded in research run metadata.
    /// </summary>
    internal static GrassSettingsState CaptureGrassSettings(GrassManager grassManager, SeedManager seedManager)
    {
        return new GrassSettingsState
        {
            metabolismCostPerAllele = grassManager.metabolismCostPerAllele,
            plantAging = grassManager.plantAging,
            agingRate = grassManager.agingRate,
            baseGrowthCost = grassManager.baseGrowthCost,
            flowerLimit = grassManager.flowerLimit,
            maxSeedsPerTile = seedManager.maxSeedsPerTile,
            maxCloneAttemptsPerDay = grassManager.maxCloneAttemptsPerDay
        };
    }

    /// <summary>
    /// Assigns each distinct genome object one slot in the snapshot's genome table.
    /// </summary>
    private class GenomeTableWriter
    {
        private readonly List<PlantGenomeState> table;
        private readonly Dictionary<PlantGenome, int> indices = new Dictionary<PlantGenome, int>();

        internal GenomeTableWriter(List<PlantGenomeState> table)
        {
            this.table = table;
        }

        /// <summary>Returns the genome's table index, adding it if new; -1 for null or DNA-less genomes.</summary>
        internal int IndexOf(PlantGenome genome)
        {
            if (genome == null)
                return -1;
            if (indices.TryGetValue(genome, out int index))
                return index;

            PlantGenomeState state = SerializeGenome(genome);
            index = state != null ? table.Count : -1;
            if (state != null)
            {
                table.Add(state);
            }
            indices[genome] = index;
            return index;
        }
    }

    private static GrassState SerializeGrass(GrassInstance grass, int genomeIndex, int seedGenomeIndex)
    {
        return new GrassState
        {
            tileX = grass.tilePosition.x,
            tileY = grass.tilePosition.y,
            genomeIndex = genomeIndex,
            seedGenomeIndex = seedGenomeIndex,
            nightEffect = grass.nightEffect,
            structuralCarbon = grass.structuralCarbon,
            availableCarbon = grass.availableCarbon,
            starchCapacity = grass.starchCapacity,
            starchStored = grass.starchStored,
            lastCloneDay = grass.lastCloneDay,
            cloneAttemptsToday = grass.cloneAttemptsToday,
            ageingExposureToday = grass.ageingExposureToday,
            dormantTimeToday = grass.dormantTimeToday,
            daylightPotentialSum = grass.daylightPotentialSum,
            daylightSampleTime = grass.daylightSampleTime,
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
            usedRespirationLastTick = grass.usedRespirationLastTick
        };
    }

    private static SeedState SerializeSeed(Seed seed, Vector3Int tilePos, int genomeIndex)
    {
        return new SeedState
        {
            tileX = tilePos.x,
            tileY = tilePos.y,
            genomeIndex = genomeIndex,
            parentGenus = seed.parentGenus,
            storedEnergy = seed.storedEnergy,
            germinationEnergy = seed.germinationEnergy,
            germinationStage = seed.germinationStage,
            sproutStage = seed.sproutStage,
            germinationCue = seed.germinationCue,
            coldPreservation = seed.coldPreservation
        };
    }

    internal static PlantGenomeState SerializeGenome(PlantGenome genome)
    {
        if (TryGetGenomeSnapshotInvalidReason(genome, out _))
            return null;

        PlantGenomeState state = new PlantGenomeState
        {
            genus = genome.genus,
            species = genome.species,
            origin = genome.origin,
            speciesGuid = genome.speciesGuid,
            genusId = genome.genusId,
            generation = genome.generation,
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

        // Names are optional labels; DNA is what makes a genome restorable
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
        string json = JsonUtility.ToJson(snapshot, false);
        SaveFileUtility.WriteAllTextAtomic(path, json);
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

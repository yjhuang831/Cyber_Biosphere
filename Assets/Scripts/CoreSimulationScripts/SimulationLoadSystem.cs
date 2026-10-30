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

        // Phase 1: parse, validate, migrate, and rebuild genomes without touching the scene.
        // Anything that fails here leaves the running simulation untouched.
        SimulationSnapshot snapshot;
        PlantGenome[] genomes;
        try
        {
            string json = File.ReadAllText(snapshotPath);
            snapshot = JsonUtility.FromJson<SimulationSnapshot>(json);
            if (snapshot == null)
            {
                Debug.LogError("Load failed: snapshot JSON is empty or invalid.");
                return false;
            }

            if (snapshot.schemaVersion < SimulationSnapshotConstants.MinimumSupportedSchemaVersion
                || snapshot.schemaVersion > SimulationSnapshotConstants.SchemaVersion)
            {
                Debug.LogError("Load failed: unsupported schema " + snapshot.schemaVersion + ". Supported schemas: "
                    + SimulationSnapshotConstants.MinimumSupportedSchemaVersion + " to " + SimulationSnapshotConstants.SchemaVersion + ".");
                return false;
            }

            if (snapshot.map == null || snapshot.tiles == null || snapshot.map.width <= 0 || snapshot.map.height <= 0)
            {
                Debug.LogError("Load failed: snapshot is missing map or tile data.");
                return false;
            }

            if (snapshot.schemaVersion < 8)
            {
                MigrateInlineGenomes(json, snapshot);
            }

            GeneManager geneManager = UnityEngine.Object.FindFirstObjectByType<GeneManager>();
            if (geneManager == null)
            {
                Debug.LogWarning("GeneManager not found during load. Loaded genomes will use fallback phenotype defaults.");
            }
            genomes = DeserializeGenomeTable(snapshot.genomes, geneManager);
        }
        catch (Exception ex)
        {
            Debug.LogError("Load failed while reading " + snapshotPath + ": " + ex.Message);
            return false;
        }

        // Phase 2: apply to the scene.
        try
        {
            ApplySnapshot(snapshot, genomes, timeController, mapManager, environmentManager, grassManager, seedManager);
            SetSimulationNameFromSaveName(snapshotPath);
            Debug.Log("Load complete: " + snapshotPath);
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError("Load failed while applying " + snapshotPath + " (simulation may be partially loaded): " + ex);
            return false;
        }
    }

    /// <summary>
    /// Schema 6/7 stored grass and seeds inside each tile with a full genome copy per object.
    /// Re-reads the file through legacy views and converts it to the schema 8 lists + genome table.
    /// </summary>
    private static void MigrateInlineGenomes(string json, SimulationSnapshot snapshot)
    {
        snapshot.genomes = new List<PlantGenomeState>();
        snapshot.plants = new List<GrassState>();
        snapshot.seeds = new List<SeedState>();

        LegacySnapshotPayload legacy = JsonUtility.FromJson<LegacySnapshotPayload>(json);
        if (legacy == null || legacy.tiles == null)
            return;

        for (int i = 0; i < legacy.tiles.Count; i++)
        {
            LegacyTileState tile = legacy.tiles[i];
            if (tile == null)
                continue;

            LegacyGrassState grass = tile.grass;
            if (grass != null && !TryGetGenomeStateInvalidReason(grass.genome, out _))
            {
                grass.tileX = tile.x;
                grass.tileY = tile.y;
                grass.genomeIndex = AddLegacyGenome(snapshot.genomes, grass.genome);
                grass.seedGenomeIndex = AddLegacyGenome(snapshot.genomes, grass.seedGenome);
                grass.nightEffect = 1f;
                snapshot.plants.Add(grass);
            }

            if (tile.seeds == null)
                continue;

            for (int s = 0; s < tile.seeds.Count; s++)
            {
                LegacySeedState seed = tile.seeds[s];
                if (seed == null || TryGetGenomeStateInvalidReason(seed.carriedGenome, out _))
                    continue;

                seed.tileX = tile.x;
                seed.tileY = tile.y;
                seed.genomeIndex = AddLegacyGenome(snapshot.genomes, seed.carriedGenome);
                seed.germinationCue = 0f;
                seed.coldPreservation = 0f;
                snapshot.seeds.Add(seed);
            }
        }
    }

    private static int AddLegacyGenome(List<PlantGenomeState> table, PlantGenomeState genome)
    {
        if (TryGetGenomeStateInvalidReason(genome, out _))
            return -1;
        table.Add(genome);
        return table.Count - 1;
    }

    private static PlantGenome[] DeserializeGenomeTable(List<PlantGenomeState> table, GeneManager geneManager)
    {
        if (table == null)
            return new PlantGenome[0];

        PlantGenome[] result = new PlantGenome[table.Count];
        for (int i = 0; i < table.Count; i++)
        {
            result[i] = DeserializeGenome(table[i], geneManager);
        }
        return result;
    }

    /// <summary>
    /// Saves before schema 11 have no named genera; their plants are grouped at the next daily update.
    /// </summary>
    private static void RestoreGenera(SimulationSnapshot snapshot, PlantGenome[] genomes, GenusTracker tracker)
    {
        tracker.Clear();
        if (snapshot.schemaVersion < 11 || snapshot.genera == null)
            return;

        tracker.RestoreCounters(snapshot.nextGenusId, snapshot.nextGenusNameIndex);
        for (int i = 0; i < snapshot.genera.Count; i++)
        {
            GenusState state = snapshot.genera[i];
            GenusRecord genus = new GenusRecord
            {
                id = state.id,
                name = state.name,
                isLibrary = state.isLibrary,
                nextSpeciesIndex = state.nextSpeciesIndex,
                parentGenusId = state.parentGenusId,
                foundedDay = state.foundedDay,
                founderColor = state.founderColor != null
                    ? new Color(state.founderColor.r, state.founderColor.g, state.founderColor.b, state.founderColor.a)
                    : Color.white
            };
            if (state.species != null)
            {
                for (int s = 0; s < state.species.Count; s++)
                {
                    string guid = state.species[s].speciesGuid;
                    genus.species.Add(new SpeciesRecord { name = state.species[s].name, speciesGuid = string.IsNullOrEmpty(guid) ? null : guid });
                }
            }
            if (state.representativeGenomeIndices != null)
            {
                int slot = 0;
                for (int r = 0; r < state.representativeGenomeIndices.Count && slot < GenusTracker.REPRESENTATIVES; r++)
                {
                    PlantGenome representative = LookupGenome(genomes, state.representativeGenomeIndices[r]);
                    if (representative != null)
                    {
                        genus.representatives[slot++] = representative;
                    }
                }
            }
            tracker.Restore(genus);
        }
    }

    private static PlantGenome LookupGenome(PlantGenome[] genomes, int index)
    {
        return index >= 0 && index < genomes.Length ? genomes[index] : null;
    }

    private static void ApplySnapshot(
        SimulationSnapshot snapshot,
        PlantGenome[] genomes,
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
        RestoreGenera(snapshot, genomes, grassManager.genusTracker);

        // Terrain and per-tile environment
        Dictionary<long, TileState> tileLookup = BuildTileLookup(snapshot.tiles);
        List<Vector3Int> allTiles = mapManager.GetAllTilePositions();
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
                if (snapshot.schemaVersion >= 7)
                {
                    env.soilOrganicCarbon = Math.Max(0.0, tileState.soilOrganicCarbon);
                    env.dissolvedCarbon = Math.Max(0.0, tileState.dissolvedCarbon);
                    env.availableNitrogen = Math.Max(0.0, tileState.availableNitrogen);
                    env.availablePhosphorus = Math.Max(0.0, tileState.availablePhosphorus);
                    env.availablePotassium = Math.Max(0.0, tileState.availablePotassium);
                }
                if (snapshot.schemaVersion >= 9)
                {
                    env.litterCarbon = Math.Max(0.0, tileState.litterCarbon);
                }
            }
            env.grass = null;
        }

        // Before schema 9 the atmosphere started empty and had no effect; now CO2 drives photosynthesis,
        // so top old saves up to the reference level rather than starving every plant.
        if (snapshot.schemaVersion < 9 && environmentManager.AtmosphericCarbonPerTile < environmentManager.referenceAtmosphericCarbonPerTile)
        {
            double before = environmentManager.atmosphericCarbon;
            environmentManager.ResetAtmosphereForNewMap();
            Debug.Log("Legacy save: atmospheric carbon raised from " + before.ToString("F1") + " to " + environmentManager.atmosphericCarbon.ToString("F1") + " (CO2 now affects photosynthesis).");
        }

        // Plants
        long loadedPlantCount = 0;
        if (snapshot.plants != null)
        {
            for (int i = 0; i < snapshot.plants.Count; i++)
            {
                GrassState state = snapshot.plants[i];
                if (state == null)
                    continue;

                Vector3Int tilePos = new Vector3Int(state.tileX, state.tileY, 0);
                PlantGenome genome = LookupGenome(genomes, state.genomeIndex);
                if (genome == null || !mapManager.IsValidTilePosition(tilePos))
                    continue;

                EnvironmentData env = mapManager.GetTileEnvironmentData(tilePos);
                if (env.grass != null)
                    continue; // Duplicate entry for a tile; keep the first

                GrassInstance grass = DeserializeGrass(state, tilePos, genome, LookupGenome(genomes, state.seedGenomeIndex));
                env.grass = grass;
                grassManager.activeGrassInstances[tilePos] = grass;
                loadedPlantCount++;
            }
        }

        for (int i = 0; i < allTiles.Count; i++)
        {
            mapManager.UpdateGrassTile(mapManager.GetTileEnvironmentData(allTiles[i]), allTiles[i]);
        }

        // Seeds
        long loadedSeedCount = 0;
        if (snapshot.seeds != null)
        {
            for (int i = 0; i < snapshot.seeds.Count; i++)
            {
                SeedState state = snapshot.seeds[i];
                if (state == null)
                    continue;

                Vector3Int tilePos = new Vector3Int(state.tileX, state.tileY, 0);
                PlantGenome genome = LookupGenome(genomes, state.genomeIndex);
                if (genome == null || !mapManager.IsValidTilePosition(tilePos))
                    continue;

                if (!seedManager.activeSeeds.TryGetValue(tilePos, out List<Seed> seeds))
                {
                    seeds = new List<Seed>();
                    seedManager.activeSeeds[tilePos] = seeds;
                }
                seeds.Add(DeserializeSeed(state, genome));
                loadedSeedCount++;
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
            bool hasExactDayPhase = snapshot.schemaVersion >= 8;
            float loadedBaseLight = snapshot.environment != null ? snapshot.environment.baseLightLevel : environmentManager.baseLightLevel;
            timeController.ApplyLoadedTimeState(snapshot.time, loadedBaseLight, hasExactDayPhase);

            SeasonManager seasonManager = UnityEngine.Object.FindFirstObjectByType<SeasonManager>();
            if (seasonManager != null)
            {
                int season = hasExactDayPhase
                    ? snapshot.time.currentSeason
                    : seasonManager.FindClosestSeasonByTemperature(environmentManager.seasonalTemperatureOffset);
                seasonManager.ApplyLoadedSeason(season);
            }
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
        target.atmosphericCarbon = Math.Max(0.0, source.atmosphericCarbon);
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
        target.maxCloneAttemptsPerDay = Mathf.Max(0, source.maxCloneAttemptsPerDay);
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

    private static GrassInstance DeserializeGrass(GrassState source, Vector3Int tilePos, PlantGenome genome, PlantGenome seedGenome)
    {
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
        grass.nightEffect = source.nightEffect > 0f ? source.nightEffect : 1f;
        grass.carbon = Mathf.Max(0f, source.carbon); // 0 for pre-schema-9 saves; refilled by photosynthesis
        grass.lastCloneDay = source.lastCloneDay;
        grass.cloneAttemptsToday = Mathf.Max(0, source.cloneAttemptsToday);
        grass.ageingExposureToday = Mathf.Max(0f, source.ageingExposureToday);
        grass.dormantTimeToday = Mathf.Max(0f, source.dormantTimeToday);
        grass.daylightPotentialSum = Mathf.Max(0f, source.daylightPotentialSum);
        grass.daylightSampleTime = Mathf.Max(0f, source.daylightSampleTime);
        grass.usedPhotosynthesisLastTick = source.usedPhotosynthesisLastTick;
        grass.usedRespirationLastTick = source.usedRespirationLastTick;
        grass.seedGenome = seedGenome;
        if (grass.IsSeeding && grass.seedGenome == null)
        {
            grass.reproductiveState = ReproductiveState.None; // A seeding plant without a seed genome could never disperse
        }
        return grass;
    }

    private static Seed DeserializeSeed(SeedState source, PlantGenome genome)
    {
        return new Seed
        {
            carriedGenome = genome,
            parentGenus = source.parentGenus,
            storedEnergy = source.storedEnergy,
            germinationEnergy = source.germinationEnergy,
            germinationStage = source.germinationStage,
            sproutStage = source.sproutStage,
            germinationCue = source.germinationCue,
            coldPreservation = source.coldPreservation
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
            speciesGuid = string.IsNullOrWhiteSpace(source.speciesGuid) ? null : source.speciesGuid,
            genusId = source.genusId,
            generation = source.generation,
            genusColor = source.genusColor != null
                ? new Color(source.genusColor.r, source.genusColor.g, source.genusColor.b, source.genusColor.a)
                : Color.white
        };

        genome.maternalDNA = DeserializeAlleles(source.maternalDNA);
        genome.paternalDNA = DeserializeAlleles(source.paternalDNA);

        if (geneManager != null)
        {
            geneManager.RebuildLoadedGenomeDerivedData(genome);
        }
        else
        {
            genome.phenotypes = new float[TraitId.Count];
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

        // Names are optional labels; DNA is what makes a genome restorable
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

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Writes research data for one simulation session to persistentDataPath/ResearchLogs/run_yyyyMMdd_HHmmss/:
///   run_meta.json - settings, map, and trait definitions (once, when the session starts)
///   daily.jsonl   - one population / event / environment summary per sim day
///   traits.jsonl  - trait distribution snapshot every N sim days
/// ".jsonl" is JSON Lines: one JSON object per line, appended as the run goes (a crash loses at most the
/// current day). Python: pandas.read_json(path, lines=True). Rows carry the absolute sim day, so sessions
/// continued from a save can be stitched together.
/// </summary>
internal class ResearchLogger
{
    internal const string LogFolder = "ResearchLogs";
    private const int LOG_FORMAT_VERSION = 1;
    private const string MetaFileName = "run_meta.json";
    private const string DailyFileName = "daily.jsonl";
    private const string TraitsFileName = "traits.jsonl";

    private readonly TimeController timeController;
    private readonly EnvironmentManager environmentManager;
    private readonly MapManager mapManager;
    private readonly GrassManager grassManager;
    private readonly SeedManager seedManager;
    private readonly SeasonManager seasonManager;
    private readonly GenusManager genusManager;
    private readonly TraitDatabase traitDatabase;
    private readonly int traitSnapshotIntervalDays;
    private readonly string dailyPath;
    private readonly string traitsPath;
    private bool failed;

    internal string RunDirectory { get; }

    private ResearchLogger(string runDirectory, TimeController timeController, EnvironmentManager environmentManager,
        GrassManager grassManager, SeedManager seedManager, int traitSnapshotIntervalDays)
    {
        RunDirectory = runDirectory;
        dailyPath = Path.Combine(runDirectory, DailyFileName);
        traitsPath = Path.Combine(runDirectory, TraitsFileName);
        this.timeController = timeController;
        this.environmentManager = environmentManager;
        this.grassManager = grassManager;
        this.seedManager = seedManager;
        this.traitSnapshotIntervalDays = Mathf.Max(0, traitSnapshotIntervalDays);
        mapManager = environmentManager.mapManager != null ? environmentManager.mapManager : grassManager.mapManager;
        seasonManager = UnityEngine.Object.FindFirstObjectByType<SeasonManager>();
        genusManager = UnityEngine.Object.FindFirstObjectByType<GenusManager>();
        traitDatabase = TraitDatabase.I != null ? TraitDatabase.I : UnityEngine.Object.FindFirstObjectByType<TraitDatabase>();
    }

    /// <summary>
    /// Creates the run folder and writes run_meta.json. Returns null (logging off for this session) on failure.
    /// </summary>
    internal static ResearchLogger StartRun(string source, TimeController timeController, EnvironmentManager environmentManager,
        GrassManager grassManager, SeedManager seedManager, int traitSnapshotIntervalDays)
    {
        if (timeController == null || environmentManager == null || grassManager == null || seedManager == null)
        {
            Debug.LogWarning("Research logging disabled: a manager reference is missing.");
            return null;
        }

        // Discard anything counted before the run started (e.g. while a save was being applied)
        ResearchCounters.TakeAndReset();

        try
        {
            string runDirectory = CreateRunDirectory();
            ResearchLogger logger = new ResearchLogger(runDirectory, timeController, environmentManager, grassManager, seedManager, traitSnapshotIntervalDays);
            SaveFileUtility.WriteAllTextAtomic(Path.Combine(runDirectory, MetaFileName), JsonUtility.ToJson(logger.BuildMetadata(source), true));
            Debug.Log("Research logging to: " + runDirectory);
            return logger;
        }
        catch (Exception e)
        {
            Debug.LogError("Research logging disabled: could not start run log: " + e.Message);
            return null;
        }
    }

    /// <summary>
    /// Called at dawn for the day that just ended. Always consumes the day's counters.
    /// </summary>
    internal void OnDayCompleted(long day)
    {
        DailyEventCounts events = ResearchCounters.TakeAndReset();
        if (failed)
            return;

        try
        {
            File.AppendAllText(dailyPath, JsonUtility.ToJson(BuildDailySummary(day, events)) + "\n");
            if (traitSnapshotIntervalDays > 0 && day % traitSnapshotIntervalDays == 0)
            {
                File.AppendAllText(traitsPath, JsonUtility.ToJson(BuildTraitSnapshot(day)) + "\n");
            }
        }
        catch (Exception e)
        {
            failed = true; // One error message, not one per day
            Debug.LogError("Research logging stopped: " + e.Message);
        }
    }

    private static string CreateRunDirectory()
    {
        string root = Path.Combine(Application.persistentDataPath, LogFolder);
        string baseName = "run_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string path = Path.Combine(root, baseName);
        for (int suffix = 2; Directory.Exists(path); suffix++)
        {
            path = Path.Combine(root, baseName + "_" + suffix);
        }
        Directory.CreateDirectory(path);
        return path;
    }

    private RunMetadata BuildMetadata(string source)
    {
        RunMetadata meta = new RunMetadata
        {
            logFormatVersion = LOG_FORMAT_VERSION,
            runId = Path.GetFileName(RunDirectory),
            startedAtUtc = DateTime.UtcNow.ToString("O"),
            source = source,
            startDay = TimeController.simDaysSinceStart,
            mapWidth = mapManager != null ? mapManager.width : 0,
            mapHeight = mapManager != null ? mapManager.height : 0,
            dayLengthSimSeconds = TimeController.fullDayLength,
            daysPerYear = SeasonManager.daysPerYear,
            germinationStepsPerDay = timeController.germinationStepsPerDay,
            traitSnapshotIntervalDays = traitSnapshotIntervalDays,
            environment = SimulationSaveSystem.CaptureEnvironmentSettings(environmentManager),
            grassSettings = SimulationSaveSystem.CaptureGrassSettings(grassManager, seedManager),
            traits = new List<TraitInfo>(),
            notes = "daily.jsonl: plant/seed/environment values are sampled at dawn; events are totals for the whole day. " +
                    "traits.jsonl: phenotype values over all living plants (plants without loci for a trait use its base value)."
        };

        if (traitDatabase != null && traitDatabase.traits != null)
        {
            for (int i = 0; i < traitDatabase.traits.Length; i++)
            {
                TraitDefinition def = traitDatabase.traits[i];
                if (def == null)
                    continue;
                meta.traits.Add(new TraitInfo
                {
                    id = def.id >= 0 ? def.id : i,
                    name = def.traitName,
                    minValue = def.minValue,
                    maxValue = def.maxValue,
                    baseValue = def.baseValue,
                    minLoci = def.minLoci,
                    maxLoci = def.maxLoci
                });
            }
        }
        return meta;
    }

    private DailySummary BuildDailySummary(long day, DailyEventCounts events)
    {
        int daysPerMonth = 30;
        int daysPerYear = Mathf.Max(1, SeasonManager.daysPerYear);
        DailySummary summary = new DailySummary
        {
            day = day,
            year = (int)(day / daysPerYear),
            month = (int)(day % daysPerYear / daysPerMonth) + 1,
            dayOfMonth = (int)(day % daysPerMonth) + 1,
            season = seasonManager != null ? seasonManager.CurrentSeason : -1,
            seasonalTemperatureOffset = environmentManager.seasonalTemperatureOffset,
            atmosphericCarbon = environmentManager.atmosphericCarbon,
            co2PhotosynthesisFactor = environmentManager.GetCO2PhotosynthesisFactor(),
            events = events,
            lineages = new List<LineageCount>()
        };

        AddPlantStats(summary);
        AddSeedStats(summary);
        AddGenusStats(summary);
        AddEnvironmentStats(summary);
        summary.totalCarbon = summary.atmosphericCarbon + summary.structuralCarbon + summary.litterCarbon;
        return summary;
    }

    private void AddPlantStats(DailySummary summary)
    {
        double energy = 0, density = 0, age = 0, generation = 0, loci = 0, capacity = 0, starchCapacity = 0, starchStored = 0;
        Dictionary<string, LineageCount> lineages = new Dictionary<string, LineageCount>();

        foreach (GrassInstance grass in grassManager.activeGrassInstances.Values)
        {
            summary.plants++;
            if (grass.IsDormant) summary.plantsDormant++;
            else if (grass.IsSeeding) summary.plantsSeeding++;
            else if (grass.IsFlowering) summary.plantsFlowering++;
            else summary.plantsActive++;

            if (mapManager != null && mapManager.GetTileEnvironmentData(grass.tilePosition).isWater)
                summary.plantsInWater++;

            energy += grass.energyStored;
            capacity += grass.EnergyCapacity;
            starchCapacity += grass.starchCapacity;
            starchStored += grass.starchStored;
            summary.structuralCarbon += grass.structuralCarbon;
            summary.availableCarbon += grass.availableCarbon;
            density += grass.grassDensity;
            age += grass.age;
            generation += grass.genome.generation;
            loci += grass.genome.expressedLocusCount;
            summary.maxGeneration = Mathf.Max(summary.maxGeneration, grass.genome.generation);

            string guid = grass.genome.speciesGuid;
            if (!string.IsNullOrEmpty(guid))
            {
                if (!lineages.TryGetValue(guid, out LineageCount lineage))
                {
                    lineage = new LineageCount { speciesGuid = guid, name = ResolveLineageName(guid) };
                    lineages[guid] = lineage;
                }
                lineage.plants++;
                if (string.IsNullOrEmpty(lineage.name) && !string.IsNullOrWhiteSpace(grass.genome.species))
                {
                    lineage.name = (grass.genome.genus + " " + grass.genome.species).Trim();
                }
            }
        }

        if (summary.plants > 0)
        {
            summary.meanEnergy = (float)(energy / summary.plants);
            summary.meanEnergyCapacity = (float)(capacity / summary.plants);
            summary.meanStarchCapacity = (float)(starchCapacity / summary.plants);
            summary.meanStarchStored = (float)(starchStored / summary.plants);
            summary.meanDensity = (float)(density / summary.plants);
            summary.meanAgeDays = (float)(age / summary.plants);
            summary.meanGeneration = (float)(generation / summary.plants);
            summary.meanLoci = (float)(loci / summary.plants);
        }

        summary.lineages.AddRange(lineages.Values);
        summary.lineages.Sort((a, b) => b.plants.CompareTo(a.plants));
        foreach (LineageCount lineage in summary.lineages)
        {
            summary.plantsInLibraryLineages += lineage.plants;
        }
    }

    private string ResolveLineageName(string guid)
    {
        if (genusManager != null && genusManager.TryGetSpeciesEntry(guid, out GenusManager.SpeciesEntry entry))
            return entry.DisplayName;
        return null;
    }

    private void AddGenusStats(DailySummary summary)
    {
        summary.genera = new List<GenusCount>(grassManager.genusTracker.Count);
        foreach (GenusRecord genus in grassManager.genusTracker.Genera)
        {
            summary.genera.Add(new GenusCount
            {
                id = genus.id,
                name = genus.name,
                isLibrary = genus.isLibrary,
                speciesCount = genus.species.Count,
                parentGenusId = genus.parentGenusId,
                foundedDay = genus.foundedDay,
                plants = genus.plants,
                seeds = genus.seeds
            });
        }
        summary.genera.Sort((a, b) => b.plants.CompareTo(a.plants));
        summary.generaAlive = summary.genera.Count;
    }

    private void AddSeedStats(DailySummary summary)
    {
        double seedEnergy = 0;
        foreach (List<Seed> seeds in seedManager.activeSeeds.Values)
        {
            if (seeds.Count == 0)
                continue;
            summary.tilesWithSeeds++;
            for (int i = 0; i < seeds.Count; i++)
            {
                summary.seeds++;
                seedEnergy += seeds[i].storedEnergy;
            }
        }
        if (summary.seeds > 0)
        {
            summary.meanSeedEnergy = (float)(seedEnergy / summary.seeds);
        }
    }

    private void AddEnvironmentStats(DailySummary summary)
    {
        if (mapManager == null)
            return;

        double landTemp = 0, waterTemp = 0, landHydration = 0, landOxygen = 0, waterOxygen = 0;
        double landN = 0, landP = 0, landK = 0, waterN = 0, waterP = 0, waterK = 0;

        foreach (Vector3Int pos in mapManager.GetAllTilePositions())
        {
            EnvironmentData data = mapManager.GetTileEnvironmentData(pos);
            summary.litterCarbon += data.litterCarbon;
            if (data.isWater)
            {
                summary.waterTiles++;
                waterTemp += data.temperature;
                waterOxygen += data.oxygen;
                waterN += data.availableNitrogen;
                waterP += data.availablePhosphorus;
                waterK += data.availablePotassium;
            }
            else
            {
                summary.landTiles++;
                landTemp += data.temperature;
                landHydration += data.hydration;
                landOxygen += data.oxygen;
                landN += data.availableNitrogen;
                landP += data.availablePhosphorus;
                landK += data.availablePotassium;
            }
        }

        if (summary.landTiles > 0)
        {
            float n = summary.landTiles;
            summary.landMeanTemperature = (float)(landTemp / n);
            summary.landMeanHydration = (float)(landHydration / n);
            summary.landMeanOxygen = (float)(landOxygen / n);
            summary.landMeanNitrogen = (float)(landN / n);
            summary.landMeanPhosphorus = (float)(landP / n);
            summary.landMeanPotassium = (float)(landK / n);
        }
        if (summary.waterTiles > 0)
        {
            float n = summary.waterTiles;
            summary.waterMeanTemperature = (float)(waterTemp / n);
            summary.waterMeanOxygen = (float)(waterOxygen / n);
            summary.waterMeanNitrogen = (float)(waterN / n);
            summary.waterMeanPhosphorus = (float)(waterP / n);
            summary.waterMeanPotassium = (float)(waterK / n);
        }
    }

    private TraitSnapshot BuildTraitSnapshot(long day)
    {
        TraitSnapshot snapshot = new TraitSnapshot { day = day, traits = new List<TraitStat>() };

        List<GrassInstance> plants = new List<GrassInstance>(grassManager.activeGrassInstances.Values);
        snapshot.plants = plants.Count;
        if (plants.Count == 0)
            return snapshot;

        int traitCount = TraitId.Count;
        for (int p = 0; p < plants.Count; p++)
        {
            float[] phenotypes = plants[p].genome.phenotypes;
            if (phenotypes != null)
                traitCount = Mathf.Max(traitCount, phenotypes.Length);
        }

        // Which plants carry at least one locus for each trait (on either strand; both are expressed)
        int[] plantsWithLoci = new int[traitCount];
        bool[] seen = new bool[traitCount];
        for (int p = 0; p < plants.Count; p++)
        {
            Array.Clear(seen, 0, seen.Length);
            CountTraitsOnStrand(plants[p].genome.maternalDNA, seen, plantsWithLoci);
            CountTraitsOnStrand(plants[p].genome.paternalDNA, seen, plantsWithLoci);
        }

        float[] values = new float[plants.Count];
        for (int traitId = 0; traitId < traitCount; traitId++)
        {
            double sum = 0;
            for (int p = 0; p < plants.Count; p++)
            {
                values[p] = plants[p].genome.GetTraitValue(traitId);
                sum += values[p];
            }
            float mean = (float)(sum / plants.Count);

            double squares = 0;
            for (int p = 0; p < plants.Count; p++)
            {
                double d = values[p] - mean;
                squares += d * d;
            }

            Array.Sort(values);
            snapshot.traits.Add(new TraitStat
            {
                id = traitId,
                name = ResolveTraitName(traitId),
                plantsWithLoci = plantsWithLoci[traitId],
                mean = mean,
                sd = (float)Math.Sqrt(squares / plants.Count),
                min = values[0],
                p10 = Percentile(values, 0.1f),
                median = Percentile(values, 0.5f),
                p90 = Percentile(values, 0.9f),
                max = values[values.Length - 1]
            });
        }
        return snapshot;
    }

    private static void CountTraitsOnStrand(List<Allele> strand, bool[] seen, int[] plantsWithLoci)
    {
        if (strand == null)
            return;
        for (int i = 0; i < strand.Count; i++)
        {
            int traitId = strand[i].traitId;
            if (traitId >= 0 && traitId < seen.Length && !seen[traitId])
            {
                seen[traitId] = true;
                plantsWithLoci[traitId]++;
            }
        }
    }

    private static float Percentile(float[] sorted, float fraction)
    {
        return sorted[Mathf.Clamp(Mathf.RoundToInt(fraction * (sorted.Length - 1)), 0, sorted.Length - 1)];
    }

    private string ResolveTraitName(int traitId)
    {
        if (traitDatabase != null && traitDatabase.traits != null)
        {
            for (int i = 0; i < traitDatabase.traits.Length; i++)
            {
                TraitDefinition def = traitDatabase.traits[i];
                if (def != null && (def.id >= 0 ? def.id : i) == traitId)
                    return def.traitName;
            }
        }
        return "Trait " + traitId;
    }
}

// ---- Record types; field names are the JSON keys ----

[Serializable]
internal class RunMetadata
{
    public int logFormatVersion;
    public string runId;
    public string startedAtUtc;
    public string source;               // "new" or "load:<save name>"
    public long startDay;
    public int mapWidth;
    public int mapHeight;
    public float dayLengthSimSeconds;
    public int daysPerYear;
    public int germinationStepsPerDay;
    public int traitSnapshotIntervalDays;
    public EnvironmentSettingsState environment;
    public GrassSettingsState grassSettings;
    public List<TraitInfo> traits;
    public string notes;
}

[Serializable]
internal class TraitInfo
{
    public int id;
    public string name;
    public float minValue;
    public float maxValue;
    public float baseValue;
    public int minLoci;
    public int maxLoci;
}

[Serializable]
internal class DailySummary
{
    public long day;
    public int year;
    public int month;
    public int dayOfMonth;
    public int season;                  // 0 Spring, 1 Summer, 2 Fall, 3 Winter
    public float seasonalTemperatureOffset;

    public int plants;
    public int plantsActive;
    public int plantsDormant;
    public int plantsFlowering;
    public int plantsSeeding;
    public int plantsInWater;
    public float meanEnergy;
    public float meanDensity;
    public float meanAgeDays;
    public float meanGeneration;
    public int maxGeneration;
    public float meanLoci;
    public int plantsInLibraryLineages;
    public List<LineageCount> lineages;
    public int generaAlive;
    public List<GenusCount> genera;

    public int seeds;
    public int tilesWithSeeds;
    public float meanSeedEnergy;

    public int landTiles;
    public int waterTiles;
    public float landMeanTemperature;
    public float landMeanHydration;
    public float landMeanOxygen;
    public float landMeanNitrogen;
    public float landMeanPhosphorus;
    public float landMeanPotassium;
    public float waterMeanTemperature;
    public float waterMeanOxygen;
    public float waterMeanNitrogen;
    public float waterMeanPhosphorus;
    public float waterMeanPotassium;

    // Carbon budget (map totals). totalCarbon should only change when the map is reseeded; drift means a bug.
    public double atmosphericCarbon;
    public double structuralCarbon;
    public double availableCarbon;      // Budget within structuralCarbon; not part of totalCarbon
    public double litterCarbon;
    public double totalCarbon;
    public float co2PhotosynthesisFactor;
    public float meanEnergyCapacity;
    public float meanStarchCapacity;
    public float meanStarchStored;

    public DailyEventCounts events;
}

[Serializable]
internal class GenusCount
{
    public int id;
    public string name;
    public bool isLibrary;
    public int speciesCount;
    public int parentGenusId;   // -1 for original lineages
    public long foundedDay;
    public int plants;
    public int seeds;
}

[Serializable]
internal class LineageCount
{
    public string speciesGuid;
    public string name;
    public int plants;
}

[Serializable]
internal class TraitSnapshot
{
    public long day;
    public int plants;
    public List<TraitStat> traits;
}

[Serializable]
internal class TraitStat
{
    public int id;
    public string name;
    public int plantsWithLoci;
    public float mean;
    public float sd;
    public float min;
    public float p10;
    public float median;
    public float p90;
    public float max;
}

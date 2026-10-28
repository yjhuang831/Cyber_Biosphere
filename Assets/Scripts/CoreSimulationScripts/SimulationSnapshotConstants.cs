using System;
using System.Collections.Generic;

internal static class SimulationSnapshotConstants
{
    public const int SchemaVersion = 8;
    public const int MinimumSupportedSchemaVersion = 6;
    public const string AutosaveFolder = "Autosaves";
    public const string ManualSaveFolder = "ManualSaves";
    public const string AutosaveFilePrefix = "sim_autosave_";
    public const string SnapshotFileExtension = ".json";
    public static int MaxAutosaveFiles = 10;
    public const string TimestampFormat = "yyyyMMdd_HHmmss";
}

// Schema 8 layout: tiles hold environment only; plants and seeds are separate lists keyed by tile coordinates,
// and every genome is stored once in `genomes` and referenced by index (seeds from one parent share a genome).
// JsonUtility cannot write null objects, so "no genome" is always an explicit index of -1.
[Serializable]
internal class SimulationSnapshot
{
    public int schemaVersion;
    public string savedAtUtc;
    public MapState map;
    public EnvironmentSettingsState environment;
    public TimeState time;
    public CounterState counters;
    public GrassSettingsState grassSettings;
    public List<TileState> tiles;
    public List<PlantGenomeState> genomes;
    public List<GrassState> plants;
    public List<SeedState> seeds;
}

[Serializable]
internal class MapState
{
    public int width;
    public int height;
}

[Serializable]
internal class EnvironmentSettingsState
{
    public float baseTemperature;
    public float hydroOffset;
    public float lightOffset;
    public float seasonalTemperatureOffset;
    public float minTemperature;
    public float maxTemperature;
    public float equatorialBoost;
    public float polarPenalty;
    public float baseLandHydration;
    public float waterRadiation;
    public float landRadiation;
    public float waterOxygen;
    public float landOxygen;
    public float dayNightCycleSpeed;
    public float baseLightLevel;
    public float waterMaxLightLevel;
    public float landMaxLightLevel;
    public float atmosphericCarbon;
}

[Serializable]
internal class TimeState
{
    public long simDaysSinceStart;
    public long accumulatedAge;
    public bool isNight;
    public double totalSimulatedTimeSeconds; // Informational; the clock is rebuilt from simDaysSinceStart + daySimTime
    public float noonTimeHours;
    public int daysPerYear;
    // Schema 8+: exact day phase, so loads resume mid-day instead of reconstructing from the light level
    public bool isDay;
    public bool dayNightSwitch;
    public float dayTimeAccumulator;
    public float switchAccumulator;
    public float daySimTime;
    public int germinationStepsDoneToday;
    public int currentSeason;
}

[Serializable]
internal class CounterState
{
    public long plantCount;
    public long seedCount;
}

[Serializable]
internal class GrassSettingsState
{
    public float metabolismCostPerAllele;
    public bool plantAging;
    public float agingRate;
    public float baseGrowthCost;
    public int flowerLimit;
    public int maxSeedsPerTile;
    public int maxCloneAttemptsPerDay; // 0 = unlimited (also what older saves load as)
}

[Serializable]
internal class TileState
{
    public int x;
    public int y;
    public bool isWater;
    public float oxygen;
    public float soilOrganicCarbon;
    public float dissolvedCarbon;
    public float availableNitrogen;
    public float availablePhosphorus;
    public float availablePotassium;
}

[Serializable]
internal class GrassState
{
    public int tileX;
    public int tileY;
    public int genomeIndex = -1;
    public int seedGenomeIndex = -1;
    public float energyStored;
    public float grassDensity;
    public long age;
    public float ageEffect;
    public int dormancyState;
    public float dormancyEffect;
    public long dormancyTimer;
    public int maxDormantTime;
    public long floweringDays;
    public int reproductiveState;
    public long flowersThisMonth;
    public long lastFlowerMonth;
    public float maturityThreshold;
    public float seedingEnergy;
    public float nightEffect = 1f;
    public long lastCloneDay = -1;
    public int cloneAttemptsToday;
    public float ageingExposureToday;   // Today's time-based ageing/dormancy accounting so far
    public float dormantTimeToday;
    public float daylightPotentialSum;  // Today's dormancy sample so far
    public float daylightSampleTime;
    public bool usedPhotosynthesisLastTick;
    public bool usedRespirationLastTick;
}

[Serializable]
internal class SeedState
{
    public int tileX;
    public int tileY;
    public int genomeIndex = -1;
    public string parentGenus;
    public float storedEnergy;
    public float germinationEnergy;
    public byte germinationStage;
    public byte sproutStage;
    public float germinationCue;
    public float coldPreservation;
}

[Serializable]
internal class PlantGenomeState
{
    public string genus;
    public string species;
    public string origin;
    public string speciesGuid;
    public int generation;
    public ColorState genusColor;
    public List<AlleleState> maternalDNA;
    public List<AlleleState> paternalDNA;
    // 0 = maternal, 1 = paternal
    public int dominantDNA;
}

[Serializable]
internal class AlleleState
{
    public byte value;
    public bool dominance;
    public int traitId;
}

[Serializable]
internal class ColorState
{
    public float r;
    public float g;
    public float b;
    public float a;
}

// ---- Read-only views of schema 6/7 files, used only to migrate their per-tile inline genomes to the schema 8 layout ----

[Serializable]
internal class LegacySnapshotPayload
{
    public List<LegacyTileState> tiles;
}

[Serializable]
internal class LegacyTileState
{
    public int x;
    public int y;
    public LegacyGrassState grass;
    public List<LegacySeedState> seeds;
}

[Serializable]
internal class LegacyGrassState : GrassState
{
    public PlantGenomeState genome;
    public PlantGenomeState seedGenome;
}

[Serializable]
internal class LegacySeedState : SeedState
{
    public PlantGenomeState carriedGenome;
}

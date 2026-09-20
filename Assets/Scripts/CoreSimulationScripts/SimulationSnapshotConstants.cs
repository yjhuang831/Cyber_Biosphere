using System;
using System.Collections.Generic;

internal static class SimulationSnapshotConstants
{
    public const int SchemaVersion = 6;
    public const string AutosaveFolder = "Autosaves";
    public const string ManualSaveFolder = "ManualSaves";
    public const string AutosaveFilePrefix = "sim_autosave_";
    public const string SnapshotFileExtension = ".json";
    public static int MaxAutosaveFiles = 10;
    public const string TimestampFormat = "yyyyMMdd_HHmmss";
}

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
}

[Serializable]
internal class TimeState
{
    public long simDaysSinceStart;
    public long accumulatedAge;
    public bool isNight;
    public float totalSimulatedTimeSeconds;
    public float noonTimeHours;
    public int daysPerYear;
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
}

[Serializable]
internal class TileState
{
    public int x;
    public int y;
    public bool isWater;
    public float oxygen;
    public GrassState grass;
    public List<SeedState> seeds;
}

[Serializable]
internal class GrassState
{
    public int tileX;
    public int tileY;
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
    public bool usedPhotosynthesisLastTick;
    public bool usedRespirationLastTick;
    public PlantGenomeState genome;
    public PlantGenomeState seedGenome;
}

[Serializable]
internal class SeedState
{
    public string parentGenus;
    public float storedEnergy;
    public float germinationEnergy;
    public byte germinationStage;
    public byte sproutStage;
    public PlantGenomeState carriedGenome;
}

[Serializable]
internal class PlantGenomeState
{
    public string genus;
    public string species;
    public string origin;
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



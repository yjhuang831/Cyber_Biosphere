/// <summary>
/// Centralized trait id mappings for genome phenotype indices.
/// Keep this in sync with TraitDatabase ordering/ids.
/// </summary>
public static class TraitId
{
    public const int GrowthRate = 0;
    public const int RespirationRate = 1;
    public const int RespirationTrigger = 2;
    public const int DormancyRate = 3;
    public const int DormancyTrigger = 4;
    public const int NightGrowthEfficiency = 5;
    public const int TemperatureTolerance = 6;
    public const int HydrationRequirement = 7;
    public const int LightRequirement = 8;
    public const int SpreadCost = 9;
    public const int ReproductionEnergyThreshold = 10;
    public const int MaturityThreshold = 11;
    public const int ColorR = 12;
    public const int ColorG = 13;
    public const int ColorB = 14;
    public const int SeedCount = 15;
    public const int SeedEnergyThreshold = 16;
    public const int GerminationEnergy = 17;
    public const int SproutStage = 18;
    public const int FloweringEnergyThreshold = 19;
    public const int PollenRange = 20;
    public const int SeedDispersalRange = 21;
    public const int SeedingProportion = 22;
    public const int GerminationCue = 23;        // Min habitat suitability (0..1) for a seed to progress germination; 0 = no check
    public const int SeedColdPreservation = 24;  // Share of daily seed decay avoided in the cold; 0 = none

    // Number of trait ids; used for fallback phenotype arrays when no TraitDatabase is available
    public const int Count = 25;
}

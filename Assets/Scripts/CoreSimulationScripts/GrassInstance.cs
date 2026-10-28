using UnityEngine;

/// <summary>
/// Represents an individual grass instance on a tile.
/// Manages per-grass state, genetics, energy, density, and reproduction mechanics.
/// </summary>
[System.Serializable]
public class GrassInstance
{
    public Vector3Int tilePosition;
    
    // Genetic and biological properties
    public PlantGenome genome;
    public float energyStored;
    public float grassDensity;
    public long age = 0;
    public float ageEffect = 0f;
    internal DormancyState dormancyState = DormancyState.Active; // Indicates if the grass is currently dormant due to environmental stress
    internal float dormancyEffect = 1f;
    internal long dormancyTimer = 0;
    internal int maxDormantTime = 0;
    internal long floweringDays = 0;
    internal float nightEffect = 1f; // Multiplier for growth efficiency during night, based on genome's nightGrowthEfficiency trait
    internal ReproductiveState reproductiveState = ReproductiveState.None;
    internal long flowersThisMonth = 0;
    internal long lastFlowerMonth = -1;
    internal bool usedPhotosynthesisLastTick = false;
    internal bool usedRespirationLastTick = false;
    internal float ageingExposureToday = 0f;   // Sim seconds lived today; dormant time weighted by dormancyEffect
    internal float dormantTimeToday = 0f;      // Sim seconds spent dormant today
    internal float daylightPotentialSum = 0f;  // Dormancy measure integrated over today's daylight (sim seconds)
    internal float daylightSampleTime = 0f;    // Daylight sim seconds sampled today
    internal long lastCloneDay = -1;       // Sim day of the most recent clone attempt
    internal int cloneAttemptsToday = 0;   // Clone attempts on lastCloneDay
    
    // [OPTIMIZED] Cached plant type for performance (never changes per genome)
    private string _cachedPlantType;
    private bool _plantTypeCached = false;
    
    // Reproduction settings
    public float maturityThreshold; // Grass can reproduce when density reaches this threshold
    internal float seedingEnergy;
    internal PlantGenome seedGenome;

    public bool IsDormant => dormancyState == DormancyState.Dormant;
    public bool IsFlowering => reproductiveState == ReproductiveState.Flowering;
    public bool IsSeeding => reproductiveState == ReproductiveState.Seeding;
    
    /// <summary>
    /// Creates a new grass instance with initial genetic traits.
    /// </summary>
    public GrassInstance(Vector3Int position, PlantGenome plantGenome, float initialEnergy = 50f)
    {
        tilePosition = position;
        genome = plantGenome;
        energyStored = initialEnergy;
        grassDensity = 0.01f;
        maturityThreshold = genome.GetTraitValue(TraitId.MaturityThreshold);
        maxDormantTime = Mathf.RoundToInt(0.5f * SeasonManager.daysPerYear);

        // Cache the plant type immediately since it never changes for a given genome
        _cachedPlantType = genome.GetPlantType();
        _plantTypeCached = true;
    }

    /// <summary>
    /// Gets the cached plant type. Avoids repeated string comparisons.
    /// </summary>
    public string GetCachedPlantType()
    {
        if (!_plantTypeCached && genome != null)
        {
            _cachedPlantType = genome.GetPlantType();
            _plantTypeCached = true;
        }
        return _cachedPlantType;
    }

    /// <summary>
    /// Checks if this grass has reached maturity and can reproduce.
    /// Reproduction is enabled when density reaches the maturity threshold.
    /// </summary>
    public bool CanReproduce()
    {
        return grassDensity >= maturityThreshold;
    }

    /// <summary>
    /// Inputs and result of the photosynthesis formula for a tile, without side effects.
    /// </summary>
    internal struct PhotosynthesisFactors
    {
        public float lightEfficiency;       // 1 = enough light; drops (can go negative) with excess light
        public float hydrationEfficiency;   // 1 = enough water; drops with excess water
        public float temperatureMultiplier; // 0..1.5, see TemperatureMultiplier
        public float basePotential;         // 2 × water × temperature, before light and size; compared with DormancyTrigger
        public float rate;                  // Energy per sim second at the current light and size
    }

    /// <summary>
    /// Pure photosynthesis calculation shared by metabolism and the info panel.
    /// </summary>
    internal PhotosynthesisFactors CalculatePhotosynthesis(EnvironmentData environmentData)
    {
        // [OPTIMIZED] Cache trait lookups within method scope to reduce repeated calls
        float lightRequirement = genome.GetTraitValue(TraitId.LightRequirement);
        float hydrationRequirement = genome.GetTraitValue(TraitId.HydrationRequirement);
        float temperatureTolerance = genome.GetTraitValue(TraitId.TemperatureTolerance);

        PhotosynthesisFactors factors = new PhotosynthesisFactors();

        // Calculate photosynthesis based on environment
        factors.lightEfficiency = Mathf.Clamp01(environmentData.light / lightRequirement);
        factors.hydrationEfficiency = Mathf.Clamp01(environmentData.hydration / hydrationRequirement);

        // Penalty for excess light
        if (environmentData.light > lightRequirement + 0.1f)
        {
            factors.lightEfficiency -= environmentData.light - lightRequirement - 0.1f;
        }

        // Penalty for excess hydration
        if (environmentData.hydration > hydrationRequirement)
        {
            factors.hydrationEfficiency -= (environmentData.hydration - hydrationRequirement) / 80f;
        }

        // Temperature stress affects energy generation
        factors.temperatureMultiplier = TemperatureMultiplier(environmentData.temperature, temperatureTolerance);

        factors.basePotential = 2f * factors.hydrationEfficiency * factors.temperatureMultiplier;
        factors.rate = factors.basePotential * factors.lightEfficiency * grassDensity;
        return factors;
    }

    /// <summary>
    /// Average dormancy measure (PhotosynthesisFactors.basePotential) over today's daylight so far; -1 if no daylight yet.
    /// </summary>
    internal float DaylightPotentialAverage => daylightSampleTime > 0f ? daylightPotentialSum / daylightSampleTime : -1f;

    /// <summary>
    /// Once per sim day: adds ageing in proportion to the time actually lived (dormant time counts at dormancyEffect,
    /// a plant born late in the day ages only for its part of the day) and advances the dormancy timer by whole
    /// dormant days. Clears the day's accumulators.
    /// </summary>
    internal void ApplyDailyAgeing(bool plantAging, float agingRate, float dayLengthSimSeconds)
    {
        float dayLength = Mathf.Max(0.0001f, dayLengthSimSeconds);
        if (plantAging)
        {
            ageEffect += agingRate * Mathf.Clamp01(ageingExposureToday / dayLength);
        }

        // The timer counts days spent mostly dormant; it resets when the plant wakes (ExitDormancy)
        if (dormantTimeToday >= 0.5f * dayLength)
        {
            dormancyTimer++;
        }

        ageingExposureToday = 0f;
        dormantTimeToday = 0f;
    }

    /// <summary>
    /// Daylight average must beat DormancyTrigger by this factor to wake; the gap keeps plants from flipping on the line.
    /// </summary>
    internal const float DORMANCY_EXIT_MARGIN = 1.1f;

    /// <summary>
    /// Once per sim day (GrassManager's daily tick, at dawn): enter or leave dormancy based on the previous day's
    /// daylight average, then start a new sample. Deciding on a day's average rather than the instant keeps
    /// dormancy a response to sustained conditions (cold spells, winter) instead of a nightly flicker.
    /// </summary>
    internal void DailyDormancyDecision()
    {
        float average = DaylightPotentialAverage;
        daylightPotentialSum = 0f;
        daylightSampleTime = 0f;

        float dormancyTrigger = genome.GetTraitValue(TraitId.DormancyTrigger);
        if (average < 0f || dormancyTrigger <= 0.0001f)
            return; // No daylight sampled (e.g. born after dusk), or no dormancy gene

        if (!IsDormant && average < dormancyTrigger && grassDensity >= maturityThreshold)
        {
            EnterDormancy(genome.GetTraitValue(TraitId.DormancyRate));
        }
        else if (IsDormant && average > dormancyTrigger * DORMANCY_EXIT_MARGIN)
        {
            ExitDormancy();
        }
    }

    /// <summary>
    /// Energy gained per sim second by respiration (it burns tile oxygen), without side effects.
    /// </summary>
    internal float CalculateRespirationRate(EnvironmentData environmentData)
    {
        return grassDensity * environmentData.oxygen * genome.GetTraitValue(TraitId.RespirationRate) * 0.05f * dormancyEffect;
    }

    /// <summary>
    /// Per-sim-second energy flows at the plant's current state, mirroring PerformMetabolism without changing anything.
    /// Gains and costs are both positive numbers.
    /// </summary>
    internal struct EnergyBudget
    {
        public bool photosynthesisOn;
        public bool respirationOn;
        public float photosynthesis;
        public float respiration;
        public float geneUpkeep;
        public float ageing;
        public float growth;
        public float seedProvisioning;
        public float Gains => photosynthesis + respiration;
        public float Costs => geneUpkeep + ageing + growth + seedProvisioning;
        public float Net => Gains - Costs;
    }

    internal EnergyBudget EstimateEnergyBudget(EnvironmentData eData, float metabolismCostPerAllele, float baseGrowthCost)
    {
        EnergyBudget budget = new EnergyBudget();

        if (energyStored < 100f && !IsDormant)
        {
            if (eData.light > 0.01f)
            {
                budget.photosynthesisOn = true;
                budget.photosynthesis = CalculatePhotosynthesis(eData).rate;
            }
            else if (energyStored < genome.GetTraitValue(TraitId.RespirationTrigger))
            {
                budget.respirationOn = true;
                budget.respiration = CalculateRespirationRate(eData);
            }
        }

        float costScale = dormancyEffect * nightEffect * grassDensity;
        budget.geneUpkeep = metabolismCostPerAllele * genome.GetDominantDNALocusCount() * costScale;
        budget.ageing = ageEffect * costScale;

        float growthExpense = baseGrowthCost * grassDensity * nightEffect;
        if (grassDensity < 1f)
        {
            growthExpense += 0.1f * Mathf.Cos(grassDensity * Mathf.PI) + 0.1f;
        }
        float seedingProportion = genome.GetTraitValue(TraitId.SeedingProportion);
        if (!IsDormant && grassDensity < 1f)
        {
            budget.growth = IsSeeding ? growthExpense * (1 - seedingProportion) : growthExpense;
        }
        if (!IsDormant && IsSeeding)
        {
            budget.seedProvisioning = growthExpense * seedingProportion;
        }

        return budget;
    }

    /// <summary>
    /// Photosynthesis temperature factor: up to 1.5x boost at the tolerance point, declining when hotter, penalized when colder.
    /// Shared with seed germination checks and the info panel so they stay consistent.
    /// </summary>
    internal static float TemperatureMultiplier(float temperature, float temperatureTolerance)
    {
        float tempDifference = temperature - temperatureTolerance;
        if (tempDifference >= 0f)
        {
            return Mathf.Clamp(1.5f - (tempDifference / 10f), 0f, 1.5f); // Boost for optimal warmth, but penalize if too hot
        }
        return Mathf.Clamp01(1f + (tempDifference / 10f)); // Penalize for cold
    }

    private float Respiration(EnvironmentData environmentData, float time)
    {
        float energyGenerated = 0f;
        float oxygenConsumption = CalculateRespirationRate(environmentData); // Base oxygen consumption for respiration

        energyGenerated += oxygenConsumption;
        environmentData.oxygen -= oxygenConsumption * time; // Reduce oxygen based on consumption and time
        environmentData.oxygen = Mathf.Clamp(environmentData.oxygen, 0f, 100f);

        return energyGenerated;
    }

    /// <summary>
    /// Performs all metabolic calculations for the grass: energy decay, photosynthesis, and density updates.
    /// Returns the amount of oxygen generated.
    /// </summary>
    public void PerformMetabolism(EnvironmentData eData, float metabolismCostPerAllele, float time, float baseGrowthCost, bool isNight)
    {
        float energyGain = 0f;
        usedPhotosynthesisLastTick = false;
        usedRespirationLastTick = false;

        // [OPTIMIZED] Cache trait lookups for frequently accessed values to reduce repeated GetTraitValue() calls
        float nightGrowthEfficiency = genome.GetTraitValue(TraitId.NightGrowthEfficiency);
        float respirationTrigger = genome.GetTraitValue(TraitId.RespirationTrigger);
        float seedingProportion = genome.GetTraitValue(TraitId.SeedingProportion);
        float growthRate = genome.GetTraitValue(TraitId.GrowthRate);

        if (nightGrowthEfficiency > 0.0001f && nightEffect >= 0.99f && isNight)
        {
            nightEffect = nightGrowthEfficiency; // Apply night growth efficiency from genome
        }
        else if (nightEffect != 1f && !isNight)
        {
            nightEffect = 1f; // Reset night effect during day
        }

        // Time-based daily accounting, settled by ApplyDailyAgeing: what matters is how long the plant actually
        // spent dormant, not its state at the instant the day rolls over
        ageingExposureToday += IsDormant ? time * dormancyEffect : time;
        if (IsDormant)
        {
            dormantTimeToday += time;
        }

        bool isDaylight = eData.light > 0.01f;
        PhotosynthesisFactors factors = default;
        if (isDaylight)
        {
            // Sampled whether dormant or not; DailyDormancyDecision averages it over the day's daylight
            factors = CalculatePhotosynthesis(eData);
            daylightPotentialSum += factors.basePotential * time;
            daylightSampleTime += time;
        }

        if (energyStored < 100f && !IsDormant)
        {
            if (isDaylight)
            {
                energyGain += factors.rate;
                usedPhotosynthesisLastTick = true;
                if (eData.oxygen < 100f)
                {
                    eData.oxygen += energyGain * time * 2f; // Increase oxygen based on energy generated and time
                    eData.oxygen = Mathf.Clamp(eData.oxygen, 0f, 100f);
                }
            }
            // [OPTIMIZED] Use cached respirationTrigger instead of repeated GetTraitValue() call
            else if (energyStored < respirationTrigger)
            {
                energyGain += Respiration(eData, time);
                usedRespirationLastTick = true;
            }

            energyStored += energyGain * time;
        }

        // [OPTIMIZED] Compute metabolism cost inline to avoid method call overhead
        float actualMetabolismCost = (metabolismCostPerAllele * genome.GetDominantDNALocusCount() + ageEffect) * dormancyEffect * nightEffect;
        energyStored -= actualMetabolismCost * grassDensity * time;

        // [OPTIMIZED] Compute growth expense inline
        float growthExpense = baseGrowthCost * grassDensity * nightEffect * time;
        if (grassDensity < 1f)
        {
            growthExpense += (0.1f * Mathf.Cos(grassDensity * Mathf.PI) + 0.1f) * time;
        }
        // Update grass density if not at maximum
        if (!IsDormant && grassDensity < 1f)
        {
            float densityGrowthCost = growthExpense;
            if (IsSeeding)
            {
                // [OPTIMIZED] Use cached seedingProportion instead of repeated GetTraitValue() call
                // Reduced growth when seeding
                densityGrowthCost *= (1 - seedingProportion); // Adjust energy cost by seeding proportion
            }

            energyStored -= densityGrowthCost;
            // [OPTIMIZED] Use cached growthRate instead of repeated GetTraitValue() call
            // Update grass density based on energy cost
            grassDensity += densityGrowthCost * growthRate * 0.01f;
            grassDensity = Mathf.Clamp01(grassDensity);

            maxDormantTime = Mathf.RoundToInt(Mathf.Clamp(grassDensity * SeasonManager.daysPerYear, 0.5f * SeasonManager.daysPerYear, SeasonManager.daysPerYear));
        }

        if (!IsDormant && IsSeeding)
        {
            // [OPTIMIZED] Use cached seedingProportion instead of repeated GetTraitValue() call
            float seedCost = growthExpense * seedingProportion;

            energyStored -= seedCost;
            seedingEnergy += seedCost;
        }

        if (energyStored < 5f && age > 1)
        {
            if (IsDormant)
            {
                energyStored = 0f;
                return;
            }

            // If energy is critically low, apply a penalty to density to simulate wilting
            grassDensity -= 0.01f * time; // Wilting rate
            grassDensity = Mathf.Clamp01(grassDensity);
            energyStored = 5f;
        }
        // Clamp energy to valid range
        energyStored = Mathf.Clamp(energyStored, 0f, 100f);
    }

    /// <summary>
    /// Checks if the grass is still alive (has energy above minimum threshold).
    /// </summary>
    public bool IsAlive()
    {
        if (dormancyTimer > maxDormantTime || energyStored < 0.05f || grassDensity < 0.001f)
        {
            return false;
        }
        return true;
    }

    /// <summary>
    /// Why IsAlive() returned false (checked in the same order).
    /// </summary>
    internal DeathCause GetDeathCause()
    {
        if (dormancyTimer > maxDormantTime)
            return DeathCause.DormancyTimeout;
        if (energyStored < 0.05f)
            return DeathCause.Starvation;
        if (grassDensity < 0.001f)
            return DeathCause.Withered;
        return DeathCause.Other;
    }

    /// <summary>
    /// Checks if this grass instance is ready to reproduce.
    /// If conditions are met (mature and energy sufficient), deducts reproduction cost and returns true.
    /// Returns false if grass is not mature enough or lacks sufficient energy.
    /// </summary>
    public bool CheckAndHandleSpread()
    {
        // Check spread availability
        if (grassDensity != 1f)
            return false;

        // Check genetic capability
        if (genome == null || genome.GetTraitValue(TraitId.ReproductionEnergyThreshold) < 0.0001f)
        {
            return false;
        }

        // Check energy threshold
        if (energyStored < genome.GetTraitValue(TraitId.ReproductionEnergyThreshold))
            return false;

        return true;
    }

    public bool CheckFlowering()
    {
        // Check genetic capability
        if (genome.GetTraitValue(TraitId.FloweringEnergyThreshold) < 0.0001f)
            return false;
        // Check maturity
        if (grassDensity < genome.GetTraitValue(TraitId.MaturityThreshold))
            return false;
        // Check flowering threshold
        if (energyStored < genome.GetTraitValue(TraitId.FloweringEnergyThreshold))
            return false;
        
        return true;
    }

    public void Die()
    {
        if (genome == null)
            return;
    }

    internal void EnterDormancy(float dormancyRate)
    {
        dormancyState = DormancyState.Dormant;
        ResearchCounters.Today.dormancyEntries++;
        if (reproductiveState == ReproductiveState.Flowering)
        {
            reproductiveState = ReproductiveState.None;
        }
        dormancyEffect = dormancyRate;
    }

    internal void ExitDormancy()
    {
        dormancyState = DormancyState.Active;
        ResearchCounters.Today.dormancyExits++;
        dormancyEffect = 1f;
        dormancyTimer = 0;
    }
}

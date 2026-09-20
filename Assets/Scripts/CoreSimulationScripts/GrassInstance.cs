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
    private float nightEffect = 1f; // Multiplier for growth efficiency during night, based on genome's nightGrowthEfficiency trait
    internal ReproductiveState reproductiveState = ReproductiveState.None;
    internal long flowersThisMonth = 0;
    internal long lastFlowerMonth = -1;
    internal bool usedPhotosynthesisLastTick = false;
    internal bool usedRespirationLastTick = false;
    
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
    /// Static helper that calculates photosynthesis energy for an environment.
    /// Gets the genome from the environment data's grass instance.
    /// If competitor is provided, calculates photosynthesis using the competitor's traits instead.
    /// </summary>
    private float Photosynthesis(EnvironmentData environmentData)
    {
        // [OPTIMIZED] Cache trait lookups within method scope to reduce repeated calls
        PlantGenome genome = environmentData.grass.genome;
        float lightRequirement = genome.GetTraitValue(TraitId.LightRequirement);
        float hydrationRequirement = genome.GetTraitValue(TraitId.HydrationRequirement);
        float temperatureTolerance = genome.GetTraitValue(TraitId.TemperatureTolerance);

        // Calculate photosynthesis based on environment
        float lightEfficiency = Mathf.Clamp01(environmentData.light / lightRequirement);
        float hydrationEfficiency = Mathf.Clamp01(environmentData.hydration / hydrationRequirement);

        // Penalty for excess light
        if (environmentData.light > lightRequirement + 0.1f)
        {
            lightEfficiency -= environmentData.light - lightRequirement - 0.1f;
        }
        
        // Penalty for excess hydration
        if (environmentData.hydration > hydrationRequirement)
        {
            hydrationEfficiency -= (environmentData.hydration - hydrationRequirement) / 80f;
        }

        // Temperature stress affects energy generation
        float tempDifference = environmentData.temperature - temperatureTolerance;
        float temperatureMultiplier = 1f;
        if (tempDifference >= 0f)
        {
            temperatureMultiplier = Mathf.Clamp01(1.5f - (tempDifference / 10f)); // Boost for optimal warmth, but penalize if too hot
        }
        else if (tempDifference < 0f)
        {
            temperatureMultiplier = Mathf.Clamp01(1f + (tempDifference / 10f)); // Penalize for cold
        }

        float energyGenerated = 2f * hydrationEfficiency * temperatureMultiplier;

        float dormancyTrigger = genome.GetTraitValue(TraitId.DormancyTrigger);
        if (dormancyTrigger > 0.0001f && !IsDormant && energyGenerated < dormancyTrigger && grassDensity >= maturityThreshold)
        {
            float dormancyRate = genome.GetTraitValue(TraitId.DormancyRate);
            EnterDormancy(dormancyRate);
        }

        energyGenerated *= lightEfficiency;
        energyGenerated *= environmentData.grass.grassDensity;

        return energyGenerated;
    }

    private float Respiration(EnvironmentData environmentData, float time)
    {
        PlantGenome genome = environmentData.grass.genome;
        float energyGenerated = 0f;
        float oxygenConsumption = environmentData.grass.grassDensity * environmentData.oxygen * genome.GetTraitValue(TraitId.RespirationRate) * 0.05f * dormancyEffect; // Base oxygen consumption for respiration

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

        if (energyStored < 100f && !IsDormant)
        {
            if (eData.light > 0.01f)
            {
                energyGain += Photosynthesis(eData);
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
        else if (IsDormant)
        {
            DormancyRecover(eData);
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

    private void DormancyRecover(EnvironmentData data)
    {
        float potentialEnergy = Photosynthesis(data);

        float dormancyTrigger = genome.GetTraitValue(TraitId.DormancyTrigger);
        if (potentialEnergy > dormancyTrigger && dormancyTrigger > 0.0001f)
        {
            ExitDormancy();
        }
    }

    internal void EnterDormancy(float dormancyRate)
    {
        dormancyState = DormancyState.Dormant;
        if (reproductiveState == ReproductiveState.Flowering)
        {
            reproductiveState = ReproductiveState.None;
        }
        dormancyEffect = dormancyRate;
    }

    internal void ExitDormancy()
    {
        dormancyState = DormancyState.Active;
        dormancyEffect = 1f;
        dormancyTimer = 0;
    }
}

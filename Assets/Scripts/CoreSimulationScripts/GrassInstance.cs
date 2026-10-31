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
    // structuralCarbon is all the carbon the plant holds: photosynthesis adds to it, respiration releases it, and it goes to
    // litter at death. availableCarbon is an internal budget within that total, not extra carbon: photosynthesis
    // adds to it and building spends it (the carbon stays in the plant), so only structuralCarbon counts towards map totals.
    internal float structuralCarbon = 0f;
    internal float availableCarbon = 0f;
    internal float starchCapacity = 0f;    // Storage developed so far; grows after maturity, up to MaxStarchCapacity
    internal float starchStored = 0f;      // Energy held as starch: filled by surplus photosynthesis, spent by respiration

    // ---- Energy storage, scaled by size ----
    internal const float MAX_ENERGY_CAPACITY = 100f;        // Capacity at full size; energy thresholds (traits) are written against this
    internal const float MIN_ENERGY_CAPACITY_FRACTION = 0.25f; // Seedlings can still hold a clone's starting energy (SpreadCost <= 25)

    /// <summary>
    /// Energy the plant can bank at its current size: 25 at size 0, 100 at full size. Photosynthesis and respiration
    /// stop adding energy at this level. Energy already above it (seed reserves, or a plant that shrank) is kept and spent down.
    /// </summary>
    internal float EnergyCapacity => MAX_ENERGY_CAPACITY * (MIN_ENERGY_CAPACITY_FRACTION + (1f - MIN_ENERGY_CAPACITY_FRACTION) * grassDensity);

    /// <summary>
    /// Scales an energy threshold trait (defined on the full-size 0-100 scale) to the plant's current capacity,
    /// so a smaller plant needs proportionally less. Identical to the raw trait value at full size.
    /// </summary>
    internal float ScaledEnergyThreshold(float traitThreshold)
    {
        return traitThreshold * EnergyCapacity / MAX_ENERGY_CAPACITY;
    }

    // ---- Starch storage ----
    private const float DEFAULT_MAX_STARCH = 10f;      // Without the trait's gene (or the trait in the database)
    private const float RESPIRATION_OXYGEN_PER_ENERGY = 0.1f; // Tile oxygen used per unit of energy respired
    private const float STARCH_DEVELOP_ENERGY = 95f;   // Storage develops, and sugar backs up, above this energy (0-100 scale)
    private const float STARCH_FEEDBACK_START = 0.5f;  // Starch fill level where photosynthesis begins to slow
    private const float STARCH_FEEDBACK_MIN = 0.25f;   // Photosynthesis multiplier with storage completely full

    // Growth runs only on surplus energy (0-100 scale). Young plants have no starch, so they must reach dusk with
    // full energy to survive the night; without this floor growth spends it all and seedlings starve overnight.
    private const float GROWTH_MIN_ENERGY = 85f;

    internal bool HasEnergyToGrow => energyStored > ScaledEnergyThreshold(GROWTH_MIN_ENERGY);

    internal float MaxStarchCapacity
    {
        get
        {
            float trait = genome.GetTraitValue(TraitId.StarchStorage);
            return trait > 0f ? trait : DEFAULT_MAX_STARCH;
        }
    }

    /// <summary>
    /// Photosynthesis slows as storage fills, but only while energy is near full (sugar backing up with nowhere to go).
    /// A plant low on energy can't draw on starch by day, so it keeps photosynthesising at full speed.
    /// </summary>
    internal float StarchPhotosynthesisMultiplier
    {
        get
        {
            if (starchCapacity <= 0f || energyStored < ScaledEnergyThreshold(STARCH_DEVELOP_ENERGY))
                return 1f;

            float fill = starchStored / starchCapacity;
            if (fill <= STARCH_FEEDBACK_START)
                return 1f;
            return Mathf.Lerp(1f, STARCH_FEEDBACK_MIN, (fill - STARCH_FEEDBACK_START) / (1f - STARCH_FEEDBACK_START));
        }
    }

    private bool CanDevelopStarchStorage =>
        !IsDormant
        && grassDensity >= maturityThreshold
        && starchCapacity < MaxStarchCapacity
        && energyStored > ScaledEnergyThreshold(STARCH_DEVELOP_ENERGY);

    /// <summary>
    /// Settings for a plant's carbon exchange with the atmosphere, supplied by GrassManager each update.
    /// </summary>
    internal struct PlantCarbonContext
    {
        public EnvironmentManager environment; // Atmosphere and decay pool (null disables carbon accounting)
        public float co2Factor;                // Photosynthesis multiplier from atmospheric CO2
        public float carbonPerEnergy;          // Carbon fixed per unit of energy photosynthesis stores, and needed per unit of energy spent on building
        public float respirationCarbonPerEnergy; // Carbon released per unit of starch respired
    }
    
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
        public float basePotential;         // 2 × water × temperature, before light, CO2 and size; compared with DormancyTrigger
        public float co2Multiplier;         // From atmospheric CO2 (1 at the reference level)
        public float starchMultiplier;      // Slowdown from full starch storage (1 = none)
        public float rate;                  // Energy per sim second at the current light, CO2, starch and size
    }

    /// <summary>
    /// Pure photosynthesis calculation shared by metabolism and the info panel.
    /// </summary>
    internal PhotosynthesisFactors CalculatePhotosynthesis(EnvironmentData environmentData, float co2Factor = 1f)
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
        factors.co2Multiplier = co2Factor;
        factors.starchMultiplier = StarchPhotosynthesisMultiplier;
        factors.rate = factors.basePotential * factors.lightEfficiency * co2Factor * factors.starchMultiplier * grassDensity;
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
        public float storageDevelopment;
        public float Gains => photosynthesis + respiration;
        public float Costs => geneUpkeep + ageing + growth + seedProvisioning + storageDevelopment;
        public float Net => Gains - Costs;
    }

    internal EnergyBudget EstimateEnergyBudget(EnvironmentData eData, float metabolismCostPerAllele, float baseGrowthCost, float co2Factor = 1f)
    {
        EnergyBudget budget = new EnergyBudget();

        if (!IsDormant)
        {
            if (eData.light > 0.01f)
            {
                budget.photosynthesisOn = true;
                budget.photosynthesis = CalculatePhotosynthesis(eData, co2Factor).rate;
            }
            else if (CanRespire)
            {
                budget.respirationOn = true;
                budget.respiration = CalculateRespirationRate(eData);
            }
        }

        float costScale = dormancyEffect * nightEffect * grassDensity;
        budget.geneUpkeep = metabolismCostPerAllele * genome.expressedLocusCount * costScale;
        budget.ageing = ageEffect * costScale;

        float growthExpense = baseGrowthCost * grassDensity * nightEffect;
        if (grassDensity < 1f)
        {
            growthExpense += 0.1f * Mathf.Cos(grassDensity * Mathf.PI) + 0.1f;
        }
        float seedingProportion = genome.GetTraitValue(TraitId.SeedingProportion);
        bool hasCarbon = availableCarbon > 0f;
        if (!IsDormant && grassDensity < 1f && HasEnergyToGrow && hasCarbon)
        {
            budget.growth = IsSeeding ? growthExpense * (1 - seedingProportion) : growthExpense;
        }
        if (!IsDormant && IsSeeding && hasCarbon)
        {
            budget.seedProvisioning = growthExpense * seedingProportion;
        }
        if (CanDevelopStarchStorage && hasCarbon)
        {
            budget.storageDevelopment = Mathf.Min(growthExpense, MaxStarchCapacity - starchCapacity);
        }

        return budget;
    }

    internal const float TEMPERATURE_PEAK_MULTIPLIER = 1.5f; // Photosynthesis boost at exactly the ideal temperature
    internal const float HEAT_FALLOFF_PER_DEGREE = 0.1f;     // Lost per degree above ideal (reaches 0 at +15 C)
    internal const float COLD_FALLOFF_PER_DEGREE = 0.1f;     // Lost per degree below ideal (reaches 0 at -15 C)

    /// <summary>
    /// Photosynthesis temperature factor: a continuous peak of 1.5x at the plant's ideal temperature (TemperatureTolerance),
    /// falling off linearly on both sides; still above 1x within 5 C of ideal. The slopes are separate so the curve
    /// can be made asymmetric. Shared with seed germination checks and the info panel so they stay consistent.
    /// </summary>
    internal static float TemperatureMultiplier(float temperature, float temperatureTolerance)
    {
        float tempDifference = temperature - temperatureTolerance;
        float falloffPerDegree = tempDifference >= 0f ? HEAT_FALLOFF_PER_DEGREE : COLD_FALLOFF_PER_DEGREE;
        return Mathf.Clamp(TEMPERATURE_PEAK_MULTIPLIER - Mathf.Abs(tempDifference) * falloffPerDegree, 0f, TEMPERATURE_PEAK_MULTIPLIER);
    }

    private bool CanRespire =>
        starchStored > 0f
        && energyStored < EnergyCapacity
        && energyStored < ScaledEnergyThreshold(genome.GetTraitValue(TraitId.RespirationTrigger));

    /// <summary>
    /// Burns starch with tile oxygen to make energy. Returns the energy made this update.
    /// </summary>
    private float Respiration(EnvironmentData environmentData, float time)
    {
        float energy = Mathf.Min(CalculateRespirationRate(environmentData) * time, starchStored, EnergyCapacity - energyStored);
        starchStored -= energy;
        environmentData.oxygen -= energy * RESPIRATION_OXYGEN_PER_ENERGY;
        environmentData.oxygen = Mathf.Clamp(environmentData.oxygen, 0f, 100f);
        return energy;
    }

    /// <summary>
    /// Performs all metabolic calculations for the grass: energy gains and costs, density updates, and carbon exchange
    /// (photosynthesis fixes atmospheric CO2 into biomass; respiration releases it).
    /// </summary>
    internal void PerformMetabolism(EnvironmentData eData, float metabolismCostPerAllele, float time, float baseGrowthCost, bool isNight, PlantCarbonContext carbonContext)
    {
        usedPhotosynthesisLastTick = false;
        usedRespirationLastTick = false;

        // [OPTIMIZED] Cache trait lookups for frequently accessed values to reduce repeated GetTraitValue() calls
        float nightGrowthEfficiency = genome.GetTraitValue(TraitId.NightGrowthEfficiency);
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
            factors = CalculatePhotosynthesis(eData, carbonContext.co2Factor);
            daylightPotentialSum += factors.basePotential * time;
            daylightSampleTime += time;
        }

        float photosynthesisEnergy = 0f;
        float respirationEnergy = 0f;
        if (!IsDormant)
        {
            if (isDaylight)
            {
                usedPhotosynthesisLastTick = true;
                photosynthesisEnergy = StorePhotosynthesis(factors.rate * time);
                if (eData.oxygen < 100f)
                {
                    eData.oxygen += photosynthesisEnergy * 2f; // Increase oxygen based on energy generated
                    eData.oxygen = Mathf.Clamp(eData.oxygen, 0f, 100f);
                }
            }
            else if (CanRespire)
            {
                respirationEnergy = Respiration(eData, time);
                energyStored += respirationEnergy;
                usedRespirationLastTick = true;
            }
        }

        // [OPTIMIZED] Compute metabolism cost inline to avoid method call overhead
        float actualMetabolismCost = (metabolismCostPerAllele * genome.expressedLocusCount + ageEffect) * dormancyEffect * nightEffect;
        energyStored -= actualMetabolismCost * grassDensity * time;

        ExchangeCarbon(carbonContext, photosynthesisEnergy, respirationEnergy);

        // [OPTIMIZED] Compute growth expense inline
        float growthExpense = baseGrowthCost * grassDensity * nightEffect * time;
        if (grassDensity < 1f)
        {
            growthExpense += (0.1f * Mathf.Cos(grassDensity * Mathf.PI) + 0.1f) * time;
        }
        // Update grass density if not at maximum
        if (!IsDormant && grassDensity < 1f && HasEnergyToGrow)
        {
            float densityGrowthCost = growthExpense;
            if (IsSeeding)
            {
                // [OPTIMIZED] Use cached seedingProportion instead of repeated GetTraitValue() call
                // Reduced growth when seeding
                densityGrowthCost *= (1 - seedingProportion); // Adjust energy cost by seeding proportion
            }

            if (TrySpendCarbon(densityGrowthCost, carbonContext))
            {
                energyStored -= densityGrowthCost;
                // [OPTIMIZED] Use cached growthRate instead of repeated GetTraitValue() call
                // Update grass density based on energy cost
                grassDensity += densityGrowthCost * growthRate * 0.01f;
                grassDensity = Mathf.Clamp01(grassDensity);

                maxDormantTime = Mathf.RoundToInt(Mathf.Clamp(grassDensity * SeasonManager.daysPerYear, 0.5f * SeasonManager.daysPerYear, SeasonManager.daysPerYear));
            }
        }

        if (!IsDormant && IsSeeding)
        {
            // [OPTIMIZED] Use cached seedingProportion instead of repeated GetTraitValue() call
            float seedCost = growthExpense * seedingProportion;

            if (TrySpendCarbon(seedCost, carbonContext))
            {
                energyStored -= seedCost;
                seedingEnergy += seedCost;
            }
        }

        if (CanDevelopStarchStorage)
        {
            // Paid from the same budget as seeds; one unit of energy builds room for one unit of starch
            float storageCost = Mathf.Min(growthExpense, MaxStarchCapacity - starchCapacity);
            if (TrySpendCarbon(storageCost, carbonContext))
            {
                energyStored -= storageCost;
                starchCapacity += storageCost;
            }
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
        // Gains are capped at capacity above, so only the floor needs clamping here
        energyStored = Mathf.Max(energyStored, 0f);
    }

    /// <summary>
    /// Fills energy first (up to the size-scaled capacity; energy already above it, like seed reserves, is kept) and
    /// sends the overflow to starch. Returns the energy actually stored. Negative input (too much light) is a plain loss.
    /// </summary>
    private float StorePhotosynthesis(float gained)
    {
        if (gained <= 0f)
        {
            energyStored += gained;
            return 0f;
        }

        float toEnergy = Mathf.Min(gained, Mathf.Max(0f, EnergyCapacity - energyStored));
        float toStarch = Mathf.Min(gained - toEnergy, starchCapacity - starchStored);
        energyStored += toEnergy;
        starchStored += toStarch;
        return toEnergy + toStarch;
    }

    /// <summary>
    /// Photosynthesis fixes atmospheric CO2 into the plant, limited only by what the atmosphere has. Respiration releases
    /// it, faster per unit of energy than photosynthesis takes it in, and takes the same amount from the budget if any is left.
    /// Carbon only moves between pools, so the map total is conserved.
    /// </summary>
    private void ExchangeCarbon(PlantCarbonContext context, float photosynthesisEnergy, float respirationEnergy)
    {
        if (context.environment == null)
            return;

        if (photosynthesisEnergy > 0f)
        {
            float fixedCarbon = (float)context.environment.TakeAtmosphericCarbon(photosynthesisEnergy * context.carbonPerEnergy);
            structuralCarbon += fixedCarbon;
            availableCarbon += fixedCarbon;
        }

        float released = Mathf.Min(structuralCarbon, respirationEnergy * context.respirationCarbonPerEnergy);
        if (released > 0f)
        {
            structuralCarbon -= released;
            availableCarbon = Mathf.Max(0f, availableCarbon - released);
            context.environment.ReleaseAtmosphericCarbon(released);
        }
    }

    /// <summary>
    /// Everything a plant builds (growth, starch storage, seeds, and any future building) goes through here: it needs
    /// enough available carbon for its energy cost and spends it. The carbon stays in the plant, so structuralCarbon
    /// is unchanged. Returns false, spending nothing, if there isn't enough.
    /// </summary>
    private bool TrySpendCarbon(float energyCost, PlantCarbonContext context)
    {
        if (context.environment == null)
            return true;

        float carbonCost = energyCost * context.carbonPerEnergy;
        if (availableCarbon < carbonCost)
            return false;

        availableCarbon -= carbonCost;
        return true;
    }

    /// <summary>
    /// Adds carbon a new plant starts with (from its seed, parent, or the air): ready to build with, so a plant starting
    /// with energy above its capacity can grow before photosynthesis has anywhere to store energy.
    /// </summary>
    internal void ReceiveCarbon(float carbon)
    {
        structuralCarbon += carbon;
        availableCarbon += carbon;
    }

    /// <summary>
    /// Removes up to the requested carbon for a clone, from the plant and its budget. Returns the amount removed.
    /// </summary>
    internal float TakeCarbon(float requested)
    {
        float taken = Mathf.Clamp(requested, 0f, structuralCarbon);
        structuralCarbon -= taken;
        availableCarbon = Mathf.Max(0f, availableCarbon - taken);
        return taken;
    }

    /// <summary>
    /// Removes the carbon built into seeds. The budget already paid for it while seeding (see TrySpendCarbon),
    /// so only the plant's total drops. Returns the amount removed.
    /// </summary>
    internal float TakeSeedCarbon(float requested)
    {
        float taken = Mathf.Clamp(requested, 0f, structuralCarbon);
        structuralCarbon -= taken;
        availableCarbon = Mathf.Min(availableCarbon, structuralCarbon);
        return taken;
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

        // Check energy threshold (scaled to capacity; equals the trait value at full size, which cloning requires)
        if (energyStored < ScaledEnergyThreshold(genome.GetTraitValue(TraitId.ReproductionEnergyThreshold)))
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
        // Check flowering threshold (scaled to capacity, so a plant flowering below full size needs proportionally less)
        if (energyStored < ScaledEnergyThreshold(genome.GetTraitValue(TraitId.FloweringEnergyThreshold)))
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

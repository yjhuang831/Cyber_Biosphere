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
    internal bool usedPhotosynthesisLastTick = false;
    internal bool usedRespirationLastTick = false;
    internal float ageingExposureToday = 0f;   // Sim seconds lived today; dormant time weighted by dormancyEffect
    internal float dormantTimeToday = 0f;      // Sim seconds spent dormant today
    internal float daylightPotentialSum = 0f;  // Dormancy measure integrated over today's daylight (sim seconds)
    internal float daylightSampleTime = 0f;    // Daylight sim seconds sampled today
    internal long lastCloneDay = -1;       // Sim day of the most recent clone attempt
    internal int cloneAttemptsToday = 0;   // Clone attempts on lastCloneDay
    // Nutrients taken up from the plant's tile (see UpdateNutrients). Leaf nitrogen isn't spent: its concentration sets how
    // well the plant photosynthesises, growth dilutes it, and turnover keeps draining it, so the plant must keep feeding.
    // Phosphorus pays for flowering. Part of both returns to the tile at death.
    internal float leafNitrogen = 0f;
    internal float phosphorusReserve = 0f;
    internal float nitrogenFactor = 1f;    // Photosynthesis multiplier from leaf nitrogen; set by UpdateNutrients
    internal float starchCapacity = 0f;    // Storage developed so far; grows after maturity, up to MaxStarchCapacity
    internal float starchStored = 0f;      // Energy held as starch: filled by surplus photosynthesis, spent by respiration
    internal float shadeMultiplier = 1f;   // Share of tile light reaching the plant past taller neighbours; set by GrassManager.UpdateShade
    internal int drawnAppearance = -1;     // Appearance MapManager last drew for this plant

    // Height is genetic; what a plant casts over its neighbours also depends on how grown it is
    internal float Height => genome.GetTraitValue(TraitId.Height);
    internal float CanopyHeight => Height * grassDensity;
    private const float SHORTEST_HEIGHT = 0.1f;
    private const float DEFAULT_HEIGHT = 0.5f;
    private const float TALLEST_HEIGHT = 1f;

    /// <summary>
    /// Growth energy multiplier: x1 at default height, sliding to the given multipliers at the shortest and tallest heights.
    /// </summary>
    internal float HeightGrowthEnergyMultiplier(float atShortest, float atTallest)
    {
        float height = Height;
        return height < DEFAULT_HEIGHT
            ? Mathf.Lerp(atShortest, 1f, Mathf.InverseLerp(SHORTEST_HEIGHT, DEFAULT_HEIGHT, height))
            : Mathf.Lerp(1f, atTallest, Mathf.InverseLerp(DEFAULT_HEIGHT, TALLEST_HEIGHT, height));
    }

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
    /// Growth settings supplied by GrassManager each update.
    /// </summary>
    internal struct PlantGrowthContext
    {
        public float shortestGrowthEnergy;     // Energy cost of growth at the shortest height, relative to default height
        public float tallestGrowthEnergy;      // Energy cost of growth at the tallest height, relative to default height
    }
    
    // [OPTIMIZED] Cached plant type for performance (never changes per genome)
    private string _cachedPlantType;
    private bool _plantTypeCached = false;
    
    // Reproduction settings
    public float maturityThreshold; // Grass can reproduce when density reaches this threshold
    internal float seedingEnergy;
    internal float seedPhosphorus = 0f;    // Phosphorus spent on flowering, held for the seeds; split among them at dispersal
    internal bool established = false;     // Has grown past ESTABLISH_SIZE at least once; set on load from size
    internal float flowerCooldownLeft = 0f; // Sim seconds until the plant may flower again; runs down once flowering and seeding are over
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
        public float starchMultiplier;      // Slowdown from full starch storage (1 = none)
        public float nitrogenMultiplier;    // From leaf nitrogen concentration (see UpdateNutrients)
        public float rate;                  // Energy per sim second at the current light, starch, nitrogen and size
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
        // Shade dims the light itself, so a lower LightRequirement doubles as shade tolerance
        float light = environmentData.light * shadeMultiplier;
        factors.lightEfficiency = Mathf.Clamp01(light / lightRequirement);
        factors.hydrationEfficiency = Mathf.Clamp01(environmentData.hydration / hydrationRequirement);

        // Penalty for excess light
        if (light > lightRequirement + 0.1f)
        {
            factors.lightEfficiency -= light - lightRequirement - 0.1f;
        }

        // Penalty for excess hydration
        if (environmentData.hydration > hydrationRequirement)
        {
            factors.hydrationEfficiency -= (environmentData.hydration - hydrationRequirement) / 80f;
        }

        // Temperature stress affects energy generation
        factors.temperatureMultiplier = TemperatureMultiplier(environmentData.temperature, temperatureTolerance);

        factors.basePotential = 2f * factors.hydrationEfficiency * factors.temperatureMultiplier;
        factors.starchMultiplier = StarchPhotosynthesisMultiplier;
        factors.nitrogenMultiplier = nitrogenFactor;
        factors.rate = factors.basePotential * factors.lightEfficiency * factors.starchMultiplier * nitrogenFactor * grassDensity;
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

    internal EnergyBudget EstimateEnergyBudget(EnvironmentData eData, float metabolismCostPerAllele, float baseGrowthCost, float heightGrowthEnergy = 1f)
    {
        EnergyBudget budget = new EnergyBudget();

        if (!IsDormant)
        {
            if (eData.light > 0.01f)
            {
                budget.photosynthesisOn = true;
                budget.photosynthesis = CalculatePhotosynthesis(eData).rate;
            }
            else if (CanRespire)
            {
                budget.respirationOn = true;
                budget.respiration = CalculateRespirationRate(eData);
            }
        }
        else if (CanRespire)
        {
            budget.respirationOn = true;
            budget.respiration = CalculateRespirationRate(eData);
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
        if (!IsDormant && grassDensity < 1f && HasEnergyToGrow)
        {
            budget.growth = (IsSeeding ? growthExpense * (1 - seedingProportion) : growthExpense) * heightGrowthEnergy;
        }
        if (!IsDormant && IsSeeding)
        {
            budget.seedProvisioning = growthExpense * seedingProportion;
        }
        if (CanDevelopStarchStorage)
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
    /// Performs all metabolic calculations for the grass: energy gains and costs, and density updates.
    /// </summary>
    internal void PerformMetabolism(EnvironmentData eData, float metabolismCostPerAllele, float time, float baseGrowthCost, bool isNight, PlantGrowthContext growthContext)
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
            factors = CalculatePhotosynthesis(eData);
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
        else if (CanRespire)
        {
            // Dormant plants live off their starch, day or night, at the slowed rate dormancyEffect gives
            respirationEnergy = Respiration(eData, time);
            energyStored += respirationEnergy;
            usedRespirationLastTick = true;
        }

        // [OPTIMIZED] Compute metabolism cost inline to avoid method call overhead
        float actualMetabolismCost = (metabolismCostPerAllele * genome.expressedLocusCount + ageEffect) * dormancyEffect * nightEffect;
        energyStored -= actualMetabolismCost * grassDensity * time;


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

            // Taller plants pay more energy for the same gain in size
            energyStored -= densityGrowthCost * HeightGrowthEnergyMultiplier(growthContext.shortestGrowthEnergy, growthContext.tallestGrowthEnergy);
            // [OPTIMIZED] Use cached growthRate instead of repeated GetTraitValue() call
            // Update grass density based on energy cost
            grassDensity += densityGrowthCost * growthRate * 0.01f;
            grassDensity = Mathf.Clamp01(grassDensity);
            if (grassDensity >= ESTABLISH_SIZE)
            {
                established = true;
            }

            maxDormantTime = Mathf.RoundToInt(Mathf.Clamp(grassDensity * SeasonManager.daysPerYear, 0.5f * SeasonManager.daysPerYear, SeasonManager.daysPerYear));
        }

        if (!IsDormant && IsSeeding)
        {
            // [OPTIMIZED] Use cached seedingProportion instead of repeated GetTraitValue() call
            float seedCost = growthExpense * seedingProportion;

            energyStored -= seedCost;
            seedingEnergy += seedCost;
        }

        if (CanDevelopStarchStorage)
        {
            // Paid from the same budget as seeds; one unit of energy builds room for one unit of starch
            float storageCost = Mathf.Min(growthExpense, MaxStarchCapacity - starchCapacity);
            energyStored -= storageCost;
            starchCapacity += storageCost;
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
    /// Settings for one nutrient pass, already scaled to the time since the last pass.
    /// </summary>
    internal struct NutrientContext
    {
        public float maxUptake;           // Most of each nutrient a full-size, hungry, well-fed plant takes this pass
        public float tileHalfSaturation;  // Tile level at which uptake runs at half speed
        public float uptakeEnergyCost;    // Energy per unit taken up
        public float leafNitrogenTarget;  // Leaf nitrogen a full-size plant wants
        public float phosphorusTarget;    // Phosphorus a full-size plant that can flower wants
        public float turnover;            // Share of leaf nitrogen lost this pass
        public float turnoverReturnShare; // Share of that loss returned to the tile
        public float starvedOutput;       // Photosynthesis multiplier with no leaf nitrogen
        public float outputGain;          // Rise above that as concentration climbs (saturating)
        public float outputHalfRatio;     // Concentration, as a share of target, at half that rise
    }

    // Uptake runs on energy: full speed down to this share of capacity, slowing to nothing as the plant starves
    private const float UPTAKE_FULL_POWER_ENERGY = 0.5f;

    /// <summary>
    /// Leaf nitrogen relative to what this plant's size wants: 1 = on target, lower as growth or turnover dilutes it.
    /// </summary>
    internal float LeafNitrogenRatio(float leafNitrogenTarget)
    {
        return leafNitrogen / Mathf.Max(0.0001f, leafNitrogenTarget * grassDensity);
    }

    /// <summary>
    /// One nutrient pass: turnover drains leaf nitrogen (slowed by dormancy), then the plant feeds from its own tile.
    /// Uptake is demand-driven, like real roots: it scales with hunger (how far the plant is below its target), the tile's
    /// richness and the plant's size, and runs on energy, so a starving plant feeds poorly. Dormant plants don't feed.
    /// </summary>
    internal void UpdateNutrients(EnvironmentData tile, in NutrientContext context)
    {
        float lost = leafNitrogen * context.turnover * (IsDormant ? dormancyEffect : 1f);
        leafNitrogen -= lost;
        tile.availableNitrogen += lost * context.turnoverReturnShare;

        if (!IsDormant)
        {
            // Same floor as energy capacity, so seedlings can still feed
            float sizeShare = EnergyCapacity / MAX_ENERGY_CAPACITY;
            float power = Mathf.Clamp01(energyStored / (EnergyCapacity * UPTAKE_FULL_POWER_ENERGY));
            float capacity = context.maxUptake * sizeShare * power;

            float nitrogenTaken = TakeNutrient(ref tile.availableNitrogen, leafNitrogen, context.leafNitrogenTarget * grassDensity, capacity, context.tileHalfSaturation);
            leafNitrogen += nitrogenTaken;

            // Plants that can't flower have no use for phosphorus
            float phosphorusTarget = genome.GetTraitValue(TraitId.FloweringEnergyThreshold) > 0.0001f ? context.phosphorusTarget * sizeShare : 0f;
            float phosphorusTaken = TakeNutrient(ref tile.availablePhosphorus, phosphorusReserve, phosphorusTarget, capacity, context.tileHalfSaturation);
            phosphorusReserve += phosphorusTaken;

            energyStored = Mathf.Max(0f, energyStored - (nitrogenTaken + phosphorusTaken) * context.uptakeEnergyCost);
        }

        float ratio = LeafNitrogenRatio(context.leafNitrogenTarget);
        nitrogenFactor = context.starvedOutput + context.outputGain * ratio / (ratio + context.outputHalfRatio);
    }

    private static float TakeNutrient(ref double pool, float held, float target, float capacity, float halfSaturation)
    {
        if (held >= target)
            return 0f;

        double available = System.Math.Max(0.0, pool);
        float hunger = 1f - held / target;
        float taken = (float)System.Math.Min(capacity * hunger * available / (available + halfSaturation), System.Math.Min(available, target - held));
        pool -= taken;
        return taken;
    }

    private const int ESTABLISH_DAYS = 3;
    internal const float ESTABLISH_SIZE = 0.02f;
    private const float ESTABLISH_STARVING_ENERGY = 6f; // Just above the wilting floor (5), where a plant earning nothing sits

    /// <summary>
    /// A plant still tiny after a few days and starving won't recover; it dies rather than holding its tile.
    /// Being below the growth floor isn't enough: a seedling growing as fast as it can always spends down to it.
    /// Only plants that never grew count: an established plant wilting away dies of starvation as usual.
    /// </summary>
    private bool FailedToEstablish => !established && age >= ESTABLISH_DAYS && grassDensity < ESTABLISH_SIZE && energyStored <= ESTABLISH_STARVING_ENERGY && !IsDormant;

    /// <summary>
    /// Checks if the grass is still alive (has energy above minimum threshold).
    /// </summary>
    public bool IsAlive()
    {
        if (dormancyTimer > maxDormantTime || energyStored < 0.05f || grassDensity < 0.001f || FailedToEstablish)
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
        if (FailedToEstablish)
            return DeathCause.FailedToEstablish;
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

using UnityEngine;
using System.Collections.Generic;
using System.Reflection;

public class EnvironmentManager : MonoBehaviour
{
    [Header("Temperature Settings")]
    public float baseTemperature = 20f;
    public float hydroOffset = -5f;      // Temperature change at 100% hydration (e.g., -5 degrees)
    public float lightOffset = -5f;      // Temperature change at 0% light/complete darkness (e.g., -5 degrees at night)
    internal float seasonalTemperatureOffset;

    public float minTemperature = -80f;
    public float maxTemperature = 60f;
    public float equatorialBoost = 5f;
    public float polarPenalty = -5f;

    [Header("Hydration Settings")]
    public float baseLandHydration = 20f;

    [Header("Other Factors")]
    public float waterLightLevel = 0f;
    public float landLightLevel = 0f;
    public float waterMaxLightLevel = 0.8f;
    public float landMaxLightLevel = 1f;
    public float waterRadiation = 0.1f;
    public float landRadiation = 0.2f;
    public float waterOxygen = 80f;
    public float landOxygen = 50f;
    [Tooltip("Scales the season's daylight hours (capped so night never goes below 0). A day always stays 24 sim hours; this only shifts the daylight/night ratio.")]
    public float dayNightCycleSpeed = 1f;
    public float baseLightLevel = 0f;

    [Header("Map Nutrient Pools (N/P/K not yet linked to plants)")]
    public float landNitrogen = 35f;
    public float waterNitrogen = 12f;
    public float landPhosphorus = 24f;
    public float waterPhosphorus = 10f;
    public float landPotassium = 40f;
    public float waterPotassium = 16f;
    [Tooltip("Atmospheric carbon (map total, relative map-carbon units). Feeds photosynthesis; does not affect temperature. " +
             "Double precision: the pool takes tiny per-plant exchanges every update.")]
    public double atmosphericCarbon = 0.0;

    [Header("Atmospheric CO2 and Photosynthesis")]
    [Tooltip("Atmospheric carbon per map tile at which photosynthesis runs at its normal rate (x1). New maps start at this level.")]
    [Min(0.01f)] public float referenceAtmosphericCarbonPerTile = 50f;
    [Tooltip("Half-saturation of the CO2 response, as a fraction of the reference level. Photosynthesis is x1 at the reference, " +
             "falls toward 0 as CO2 runs out, and approaches x(1 + this) when CO2 is plentiful.")]
    [Min(0.01f)] public float co2HalfSaturationFraction = 0.5f;

    [Header("Litter (Decay Pool)")]
    [Tooltip("Share of each tile's litter (carbon from dead plants) that decays into atmospheric CO2 per sim second.")]
    [Min(0f)] public float litterDecayRate = 0.0005f;
    [Tooltip("Relative nitrogen deposition per simulated second.")]
    [Min(0f)] public float nitrogenDepositionRate = 0.00001f;
    [Tooltip("Relative phosphorus weathering per simulated second.")]
    [Min(0f)] public float phosphorusWeatheringRate = 0.000002f;
    [Tooltip("Relative potassium weathering per simulated second.")]
    [Min(0f)] public float potassiumWeatheringRate = 0.000005f;
    [Tooltip("Fraction of available land nutrients carried to adjacent water per simulated second.")]
    [Min(0f)] public float nutrientLeachingRate = 0.0001f;

    [Header("References")]
    public MapManager mapManager;
    public GrassManager grassManager;
    public EnvironmentVisualizer environmentVisualizer;

    [Header("Oxygen Diffusion")]
    [Tooltip("Share of an oxygen difference moved to each lower neighbour per simulated second, before the land (0.2) / water (0.1) rate.")]
    [Min(0f)] public float oxygenDiffusionCoefficient = 0.01f;

    private float nutrientTimeAccumulator;
    private const float NUTRIENT_UPDATE_INTERVAL = 10f;
    private float oxygenTimeAccumulator;
    private const float LAND_OXYGEN_DIFFUSION_RATE = 0.2f;
    private const float WATER_OXYGEN_DIFFUSION_RATE = 0.1f;
    // Stability bound for the explicit scheme: per sub-step a tile may pass at most this share of a difference to all neighbours combined
    private const float MAX_OXYGEN_OUTFLOW_PER_STEP = 0.5f;
    private const int MAX_OXYGEN_SUBSTEPS = 64;

    void Awake()
    {
        baseTemperature = 20f;
        if (SimulationStartupSettings.TryGet(out _, out _, out float startupWaterRadiation, out float startupLandRadiation, out _))
        {
            waterRadiation = startupWaterRadiation;
            landRadiation = startupLandRadiation;
        }
    }

    internal void UpdateTileEnvironment(Vector3Int? centerPosition = null, bool fullUpdate = false, bool distributeOxygen = false, float simTime = 0f, bool skipHydrationUpdate = false)
    {
        // Compute light levels for both tile types
        waterLightLevel = Mathf.Min(baseLightLevel, waterMaxLightLevel);
        landLightLevel = Mathf.Min(baseLightLevel, landMaxLightLevel);

        float mapHeight = mapManager.height;

        using (var pooled = grassManager.GetPooledPositions())
        {
            List<Vector3Int> tilesToCheck = pooled.List;
            if (centerPosition.HasValue)
            {
                // Only check tiles within a certain radius of the center position for efficiency
                int radius = 6; // Example radius, can be adjusted
                for (int x = -radius; x <= radius; x++)
                {
                    for (int y = -radius; y <= radius; y++)
                    {
                        Vector3Int tilePos = centerPosition.Value + new Vector3Int(x, y, 0);
                        if (mapManager.IsValidTilePosition(tilePos))
                        {
                            tilesToCheck.Add(tilePos);
                        }
                    }
                }
            }
            else
            {
                // Check all tiles if no center position is provided
                tilesToCheck.AddRange(mapManager.GetAllTilePositions());
            }

            foreach (var tilePos in tilesToCheck)
            {
                EnvironmentData data = mapManager.GetTileEnvironmentData(tilePos);

                // Update light based on tile type (water vs land)
                float targetLight = data.isWater ? waterLightLevel : landLightLevel;
                if (!Mathf.Approximately(data.light, targetLight))
                {
                    data.light = Mathf.Clamp01(targetLight);
                }

                // Recalculate hydration and land tile colors (unless explicitly skipped)
                if (!skipHydrationUpdate)
                {
                    RecalculateHydrationAndRadiation(tilePos, data);
                }

                // Recalculate temperature
                RecalculateTemperature(tilePos, data, mapHeight);

            }
        }

        if (simTime > 0f)
        {
            nutrientTimeAccumulator += simTime;
            if (nutrientTimeAccumulator >= NUTRIENT_UPDATE_INTERVAL)
            {
                float elapsed = nutrientTimeAccumulator;
                nutrientTimeAccumulator = 0f;
                UpdateNutrientPools(elapsed);
            }
        }

        // Diffusion runs less often than this update, so integrate over all the time elapsed since the last pass
        if (simTime > 0f)
            oxygenTimeAccumulator += simTime;
        if (distributeOxygen && oxygenTimeAccumulator > 0f)
        {
            DistributeOxygen(oxygenTimeAccumulator);
            oxygenTimeAccumulator = 0f;
        }
    }

    internal void InitializeNutrientPools(EnvironmentData data, bool isWater)
    {
        if (data == null)
            return;

        data.litterCarbon = 0.0;
        data.availableNitrogen = System.Math.Max(0.0, isWater ? waterNitrogen : landNitrogen);
        data.availablePhosphorus = System.Math.Max(0.0, isWater ? waterPhosphorus : landPhosphorus);
        data.availablePotassium = System.Math.Max(0.0, isWater ? waterPotassium : landPotassium);
    }

    // ---- Atmospheric CO2 ----
    // Carbon only moves between pools (atmosphere, plants, litter); it is never created or destroyed,
    // except when a new map is seeded with its starting atmosphere (and legacy saves are topped up on load).

    private int TileCount => mapManager != null ? Mathf.Max(1, mapManager.GetAllTilePositions().Count) : 1;

    internal double AtmosphericCarbonPerTile => atmosphericCarbon / TileCount;

    /// <summary>
    /// Photosynthesis multiplier from atmospheric CO2 (saturating, normalised to x1 at the reference level).
    /// </summary>
    internal float GetCO2PhotosynthesisFactor()
    {
        double concentration = System.Math.Max(0.0, AtmosphericCarbonPerTile);
        double reference = System.Math.Max(0.01, referenceAtmosphericCarbonPerTile);
        double halfSaturation = reference * System.Math.Max(0.01, co2HalfSaturationFraction);
        double response = concentration / (concentration + halfSaturation);
        double referenceResponse = reference / (reference + halfSaturation);
        return (float)(response / referenceResponse);
    }

    /// <summary>
    /// Removes up to the requested carbon from the atmosphere (for photosynthesis) and returns how much was available.
    /// </summary>
    internal double TakeAtmosphericCarbon(double requested)
    {
        double granted = System.Math.Min(System.Math.Max(0.0, requested), System.Math.Max(0.0, atmosphericCarbon));
        atmosphericCarbon -= granted;
        return granted;
    }

    internal void ReleaseAtmosphericCarbon(double amount)
    {
        if (amount > 0.0)
            atmosphericCarbon += amount;
    }

    /// <summary>
    /// Starting atmosphere for a freshly generated map (not for loads): the reference level on every tile.
    /// </summary>
    internal void ResetAtmosphereForNewMap()
    {
        atmosphericCarbon = (double)referenceAtmosphericCarbonPerTile * TileCount;
    }

    /// <summary>
    /// Puts carbon from a dead plant into its tile's decay pool; it returns to the air over time.
    /// </summary>
    internal void AddLitter(EnvironmentData tile, double carbon)
    {
        if (tile != null && carbon > 0.0)
            tile.litterCarbon += carbon;
    }

    private void UpdateNutrientPools(float simTime)
    {
        List<Vector3Int> positions = mapManager.GetAllTilePositions();
        double carbonEmitted = 0.0;
        double litterFraction = System.Math.Min(1.0, System.Math.Max(0.0, litterDecayRate) * simTime);

        // Apply litter decay and weathering/deposition per tile.
        foreach (Vector3Int pos in positions)
        {
            EnvironmentData data = mapManager.GetTileEnvironmentData(pos);

            // Decay pool: dead plant matter respires to CO2
            double litterDecayed = data.litterCarbon * litterFraction;
            data.litterCarbon -= litterDecayed;
            carbonEmitted += litterDecayed;

            double landFactor = data.isWater ? 0.25 : 1.0;
            data.availableNitrogen += System.Math.Max(0f, nitrogenDepositionRate) * landFactor * simTime;
            if (!data.isWater)
            {
                data.availablePhosphorus += System.Math.Max(0f, phosphorusWeatheringRate) * simTime;
                data.availablePotassium += System.Math.Max(0f, potassiumWeatheringRate) * simTime;
            }

            data.nitrogenTransportDelta = 0.0;
            data.phosphorusTransportDelta = 0.0;
            data.potassiumTransportDelta = 0.0;
        }

        atmosphericCarbon = System.Math.Max(0.0, atmosphericCarbon + carbonEmitted);

        // Runoff carries a small share of dissolved land nutrients into adjacent water.
        foreach (Vector3Int pos in positions)
        {
            EnvironmentData source = mapManager.GetTileEnvironmentData(pos);
            if (source.isWater || source.immediateNeighbors == null || source.immediateNeighbors.Count == 0)
                continue;

            int waterNeighbors = 0;
            foreach (Vector3Int neighborPos in source.immediateNeighbors)
            {
                if (mapManager.GetTileEnvironmentData(neighborPos).isWater)
                    waterNeighbors++;
            }
            if (waterNeighbors == 0)
                continue;

            double runoff = System.Math.Min(1.0, System.Math.Max(0f, nutrientLeachingRate) * simTime
                * Mathf.Clamp01(source.hydration / 100f) * waterNeighbors / source.immediateNeighbors.Count);
            TransferNutrient(source, runoff, waterNeighbors);
        }

        foreach (Vector3Int pos in positions)
        {
            EnvironmentData data = mapManager.GetTileEnvironmentData(pos);
            data.availableNitrogen = ClampNutrient(data.availableNitrogen + data.nitrogenTransportDelta);
            data.availablePhosphorus = ClampNutrient(data.availablePhosphorus + data.phosphorusTransportDelta);
            data.availablePotassium = ClampNutrient(data.availablePotassium + data.potassiumTransportDelta);
        }
    }

    private static double ClampNutrient(double value)
    {
        return System.Math.Min(System.Math.Max(value, 0.0), 100.0);
    }

    private void TransferNutrient(EnvironmentData source, double fraction, int waterNeighbors)
    {
        double nitrogen = source.availableNitrogen * fraction;
        double phosphorus = source.availablePhosphorus * fraction;
        double potassium = source.availablePotassium * fraction;
        source.availableNitrogen -= nitrogen;
        source.availablePhosphorus -= phosphorus;
        source.availablePotassium -= potassium;

        double divisor = System.Math.Max(1, waterNeighbors);
        foreach (Vector3Int neighborPos in source.immediateNeighbors)
        {
            EnvironmentData neighbor = mapManager.GetTileEnvironmentData(neighborPos);
            if (!neighbor.isWater)
                continue;
            neighbor.nitrogenTransportDelta += nitrogen / divisor;
            neighbor.phosphorusTransportDelta += phosphorus / divisor;
            neighbor.potassiumTransportDelta += potassium / divisor;
        }
    }

    /// <summary>
    /// [OPTIMIZED] Recalculate temperature based on current light, hydration, and cached latitude.
    /// Only recalculates dynamic parts - light and hydration effects.
    /// Must run every frame since light changes continuously with day-night cycle.
    /// </summary>
    private void RecalculateTemperature(Vector3Int tilePos, EnvironmentData data, float mapHeight)
    {
        // [OPTIMIZED] Calculate and cache static temperature offset (latitude + PerlinNoise) only once
        // This is completely static and doesn't need to recalculate every frame
        if (!data.staticTempOffsetCalculated)
        {
            Vector3Int mapCenterOffset = new Vector3Int(mapManager.width / 2, mapManager.height / 2, 0);
            float latitude = Mathf.Abs(tilePos.y + mapCenterOffset.y) / mapHeight;
            float latitudeEffect = Mathf.PerlinNoise(tilePos.x * 0.1f, tilePos.y * 0.1f) * 2 - 1;
            data.cachedStaticTempOffset = (equatorialBoost * (1 - latitude)) + (polarPenalty * latitude) + latitudeEffect;
            data.staticTempOffsetCalculated = true;
        }
        
        // Base temperature from seasonal and static offsets
        float temperature = baseTemperature + data.cachedStaticTempOffset + seasonalTemperatureOffset;

        // Apply temperature offsets proportionally based on hydration and light
        // Hydration effect: 100% hydration = 100% of hydroOffset applied
        float hydrationEffect = (data.hydration / 100f) * hydroOffset;
        // Light effect: 0% light (night) = 100% of lightOffset applied
        // data.light is always current since we update it first every frame
        float lightEffect = (1f - data.light) * lightOffset;
        data.temperature = temperature + hydrationEffect + lightEffect;

        // Clamp temperature to valid range
        data.temperature = Mathf.Clamp(data.temperature, minTemperature, maxTemperature);
    }

    /// <summary>
    /// [UPDATED] Recalculate hydration, radiation, and visual feedback.
    /// Called only when environment topology changes (not every frame).
    /// </summary>
    private void RecalculateHydrationAndRadiation(Vector3Int tilePos, EnvironmentData data)
    {
        if (data.isWater)
        {
            data.hydration = 100f; // Water is always fully hydrated
            data.radiation = waterRadiation;
        }
        else
        {
            // Hydration calculation for land tiles
            float hydration = CalculateLandHydration(tilePos);
            data.hydration = hydration;
            
            // Apply visual feedback based on hydration - brightness indicates dryness/wetness
            float t = Mathf.Clamp01(hydration / 100f);
            float brightness = Mathf.Lerp(1f, 0.4f, t);
            Color tileColor = new Color(brightness, brightness, brightness);
            
            // Only call SetColor if the color actually changed (avoid redundant tilemap updates)
            if (data.cachedTileColor != tileColor)
            {
                mapManager.terrainTilemap.SetColor(tilePos, tileColor);
                data.cachedTileColor = tileColor;
            }
            
            data.radiation = landRadiation;
        }
        
        // Clamp values to valid ranges
        data.hydration = Mathf.Clamp(data.hydration, 0f, 100f);
        data.radiation = Mathf.Clamp01(data.radiation);
    }

    public float CalculateLandHydration(Vector3Int tilePos)
    {
        int searchRadius = 4;
        float closestWaterDistance = float.MaxValue;

        // Find closest water tile within search radius
        for (int x = -searchRadius; x <= searchRadius; x++)
        {
            for (int y = -searchRadius; y <= searchRadius; y++)
            {
                if (x == 0 && y == 0) continue;

                int squaredDistance = x * x + y * y;
                // Only consider tiles within circular radius
                if (squaredDistance > searchRadius * searchRadius)
                    continue;

                Vector3Int checkPos = tilePos + new Vector3Int(x, y, 0);
                if (!mapManager.IsValidTilePosition(checkPos))
                    continue;

                EnvironmentData neighborData = mapManager.GetTileEnvironmentData(checkPos);
                if (neighborData.isWater)
                {
                    float distance = Mathf.Sqrt(squaredDistance);
                    if (distance < closestWaterDistance)
                    {
                        closestWaterDistance = distance;
                    }
                }
            }
        }

        float result;
        if (closestWaterDistance == float.MaxValue)
        {
            // No water found within radius - use base land hydration
            result = baseLandHydration;
        }
        else if (closestWaterDistance <= 1f)
        {
            // Adjacent to water - use max coastal hydration
            result = Mathf.Min(baseLandHydration + 40f, 100f);
        }
        else
        {
            // Linear interpolation based on distance
            // At distance 1: maxCoastalHydration
            // At distance ~4: baseLandHydration
            float t = Mathf.Clamp01((closestWaterDistance - 1f) / 3f);
            float coastalHydration = Mathf.Min(baseLandHydration + 40f, 100f);
            result = Mathf.Lerp(coastalHydration, baseLandHydration, t);
        }

        return result;
    }

    /// <summary>
    /// Diffuses oxygen between same-type neighbours over the elapsed time, split into sub-steps small enough
    /// that no tile can overshoot its neighbours (the explicit scheme otherwise oscillates for large steps).
    /// </summary>
    private void DistributeOxygen(float elapsed)
    {
        float maxOutflowPerSecond = LAND_OXYGEN_DIFFUSION_RATE * Mathf.Max(0f, oxygenDiffusionCoefficient) * 8f; // 8 neighbours
        int subSteps = Mathf.Clamp(Mathf.CeilToInt(maxOutflowPerSecond * elapsed / MAX_OXYGEN_OUTFLOW_PER_STEP), 1, MAX_OXYGEN_SUBSTEPS);
        float stepTime = elapsed / subSteps;
        for (int i = 0; i < subSteps; i++)
        {
            DiffuseOxygenStep(stepTime);
        }
    }

    private void DiffuseOxygenStep(float simTime)
    {
        List<Vector3Int> positions = mapManager.GetAllTilePositions();
        foreach (Vector3Int pos in positions)
        {
            EnvironmentData data = mapManager.GetTileEnvironmentData(pos);
            data.oxygenBeforeDiffusion = Mathf.Max(0f, data.oxygen);
            data.oxygenDiffusionDelta = 0f;
        }

        foreach (Vector3Int pos in positions)
        {
            EnvironmentData source = mapManager.GetTileEnvironmentData(pos);
            if (source.immediateNeighbors == null || source.immediateNeighbors.Count == 0)
                continue;

            float coefficient = (source.isWater ? WATER_OXYGEN_DIFFUSION_RATE : LAND_OXYGEN_DIFFUSION_RATE)
                * Mathf.Max(0f, oxygenDiffusionCoefficient) * simTime;
            float totalRequested = 0f;
            foreach (Vector3Int neighborPos in source.immediateNeighbors)
            {
                EnvironmentData neighbor = mapManager.GetTileEnvironmentData(neighborPos);
                if (source.isWater == neighbor.isWater && neighbor.oxygenBeforeDiffusion < source.oxygenBeforeDiffusion)
                    totalRequested += (source.oxygenBeforeDiffusion - neighbor.oxygenBeforeDiffusion) * coefficient;
            }

            float scale = totalRequested > source.oxygenBeforeDiffusion && totalRequested > 0f
                ? source.oxygenBeforeDiffusion / totalRequested
                : 1f;
            foreach (Vector3Int neighborPos in source.immediateNeighbors)
            {
                EnvironmentData neighbor = mapManager.GetTileEnvironmentData(neighborPos);
                if (source.isWater != neighbor.isWater || neighbor.oxygenBeforeDiffusion >= source.oxygenBeforeDiffusion)
                    continue;

                float transfer = (source.oxygenBeforeDiffusion - neighbor.oxygenBeforeDiffusion) * coefficient * scale;
                source.oxygenDiffusionDelta -= transfer;
                neighbor.oxygenDiffusionDelta += transfer;
            }
        }

        foreach (Vector3Int pos in positions)
        {
            EnvironmentData data = mapManager.GetTileEnvironmentData(pos);
            data.oxygen = Mathf.Clamp(data.oxygenBeforeDiffusion + data.oxygenDiffusionDelta, 0f, 100f);
        }
    }

    internal void VisualUpdate()
    {
        if (environmentVisualizer != null && !string.IsNullOrEmpty(environmentVisualizer.activeVisualization))
        {
            foreach (var tilePos in mapManager.GetAllTilePositions())
            {
                EnvironmentData data = mapManager.GetTileEnvironmentData(tilePos);
            
                // [OPTIMIZED] Calculate visualization color from current data
                Color tileColor = environmentVisualizer.VisualizeFactor(data);
                
                // [OPTIMIZED] Only update overlay if color has changed (avoid redundant SetColor calls)
                // This is critical because VisualUpdate is called 50x per second
                if (data.cachedVisualizationColor != tileColor)
                {
                    mapManager.SetOverlayColor(tilePos, tileColor, data);
                    data.cachedVisualizationColor = tileColor;
                }
            }
        }
    }

    public void ParameterSlider(string parameterName, float newValue)
    {
        // Example of using reflection to dynamically adjust parameters via UI sliders
        // This allows for easy tweaking without hardcoding each parameter
        // Sliders should be set up to call this method with the parameter name and new value

        FieldInfo field = typeof(EnvironmentManager).GetField(
            parameterName,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase
        ); // Only allow public instance fields to be modified for safety
        
        if (field != null && field.FieldType == typeof(float))
        {
            field.SetValue(this, newValue);
            // The periodic update skips hydration/radiation, so push setting changes to every tile now.
            if (mapManager != null && mapManager.GetAllTilePositions().Count > 0)
            {
                UpdateTileEnvironment(null, true);
            }
        }
        else
        {
            Debug.LogWarning($"Parameter '{parameterName}' not found or is not a float.");
        }
    }

    internal float GetParameterValue(string parameterName)
    {
        FieldInfo field = typeof(EnvironmentManager).GetField(
            parameterName,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase
        );
        
        if (field != null && field.FieldType == typeof(float))
        {
            return (float)field.GetValue(this);
        }
        else
        {
            Debug.LogWarning($"Parameter '{parameterName}' not found or is not a float.");
            return 0f;
        }
    }
}

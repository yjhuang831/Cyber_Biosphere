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
    public float dayNightCycleSpeed = 1f;
    public float baseLightLevel = 0f;

    [Header("References")]
    public MapManager mapManager;
    public GrassManager grassManager;
    public EnvironmentVisualizer environmentVisualizer;

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

                // [UPDATED] Oxygen distribution as separate step when requested
                if (distributeOxygen)
                {
                    DistributeOxygen(tilePos, data, simTime);
                }
            }
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

    private void DistributeOxygen(Vector3Int tilePos, EnvironmentData data, float simTime)
    {
        float totalTransferred = 0f;
        float diffusionRate = 0f;
        if (data.isWater)
        {
            diffusionRate = 0.1f; // Slower diffusion in water
        }
        else
        {
            diffusionRate = 0.2f; // Faster diffusion on land
        }

        foreach (var neighborPos in data.immediateNeighbors)
        {
            EnvironmentData neighborData = mapManager.GetTileEnvironmentData(neighborPos);

            // Only average with same terrain type to reduce unrealistic mixing
            if (data.isWater == neighborData.isWater && neighborData.oxygen < data.oxygen)
            {
                float oxygenDifference = data.oxygen - neighborData.oxygen;
                float oxygenToTransfer = oxygenDifference * diffusionRate * 0.2f * simTime;
                neighborData.oxygen += oxygenToTransfer;
                totalTransferred += oxygenToTransfer;
            }
        }

        data.oxygen -= totalTransferred; // Reduce from source tile after distributing
    }

    internal void VisualUpdate()
    {
        if (environmentVisualizer.activeVisualization != "")
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

using UnityEngine;

public static class SimulationStartupSettings
{
    private static bool hasSettings;
    private static int mapWidth;
    private static int mapHeight;
    private static float waterRadiation;
    private static float landRadiation;
    private static int maxSeedsPerTile;

    public static void Configure(int width, int height, float waterRad, float landRad, int seedLimitPerTile)
    {
        mapWidth = Mathf.Max(1, width);
        mapHeight = Mathf.Max(1, height);
        waterRadiation = Mathf.Clamp01(waterRad);
        landRadiation = Mathf.Clamp01(landRad);
        maxSeedsPerTile = Mathf.Max(0, seedLimitPerTile);
        hasSettings = true;
    }

    public static bool TryGet(out int width, out int height, out float waterRad, out float landRad, out int seedLimitPerTile)
    {
        width = mapWidth;
        height = mapHeight;
        waterRad = waterRadiation;
        landRad = landRadiation;
        seedLimitPerTile = maxSeedsPerTile;
        return hasSettings;
    }

    public static void Clear()
    {
        hasSettings = false;
    }
}

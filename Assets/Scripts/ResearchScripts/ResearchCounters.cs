using System;

/// <summary>
/// Event tallies for the current sim day. Simulation code increments ResearchCounters.Today;
/// ResearchLogger writes it out at dawn and starts a fresh one. Counting is cheap, so it runs even when logging is off.
/// </summary>
internal static class ResearchCounters
{
    internal static DailyEventCounts Today = new DailyEventCounts();

    internal static DailyEventCounts TakeAndReset()
    {
        DailyEventCounts completed = Today;
        Today = new DailyEventCounts();
        return completed;
    }

    internal static void RecordBirth(BirthCause cause)
    {
        switch (cause)
        {
            case BirthCause.Clone: Today.birthsClone++; break;
            case BirthCause.Seedling: Today.birthsSeedling++; break;
            default: Today.birthsPlayer++; break;
        }
    }

    internal static void RecordDeath(DeathCause cause)
    {
        switch (cause)
        {
            case DeathCause.Starvation: Today.deathsStarvation++; break;
            case DeathCause.DormancyTimeout: Today.deathsDormancyTimeout++; break;
            case DeathCause.Withered: Today.deathsWithered++; break;
            case DeathCause.DisplacedBySeedling: Today.deathsDisplacedBySeedling++; break;
            case DeathCause.TerrainChange: Today.deathsTerrainChange++; break;
            default: Today.deathsOther++; break;
        }
    }
}

internal enum BirthCause
{
    Player,
    Clone,
    Seedling
}

internal enum DeathCause
{
    Other,
    Starvation,          // Energy ran out
    DormancyTimeout,     // Dormant longer than the plant can survive
    Withered,            // Size (density) shrank to nothing
    DisplacedBySeedling, // Weak or dormant plant replaced by a sprouting seed
    TerrainChange        // Player converted the tile
}

/// <summary>
/// Events counted during one sim day. Field names are the JSON keys in daily.jsonl.
/// </summary>
[Serializable]
internal class DailyEventCounts
{
    public int birthsClone;
    public int birthsSeedling;
    public int birthsPlayer;

    public int deathsStarvation;
    public int deathsDormancyTimeout;
    public int deathsWithered;
    public int deathsDisplacedBySeedling;
    public int deathsTerrainChange;
    public int deathsOther;

    public int cloneAttempts;      // Clone shoots sent out (an occupied target tile wastes the attempt)
    public int cloneAttemptsCapped; // Plants ready to clone but over the daily attempt limit

    public int floweringStarts;
    public int crossPollinations;
    public int selfPollinations;
    public int selfPollinationFailures; // Flowering ended without seed

    public int seedDispersals;     // Plants releasing a seed batch
    public int seedsReleased;
    public int seedsLost;          // Landed off-map or on a full tile
    public int seedsSprouted;
    public int seedsFailedToSprout; // Lost the germination roll
    public int seedsExhausted;     // Spent all energy while germinating
    public int seedsDecayed;       // Ran out of energy from daily decay
    public int seedsPruned;        // Could no longer afford the remaining germination stages

    public int dormancyEntries;
    public int dormancyExits;
}

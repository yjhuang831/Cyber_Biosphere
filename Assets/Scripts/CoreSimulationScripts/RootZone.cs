using UnityEngine;

/// <summary>
/// Fixed layout of the tiles a plant's roots may occupy: its own tile (bit 0) and every tile within Manhattan distance
/// MAX_DISTANCE of it, ordered by distance (1 + 4 + 8 + 12 = 25 tiles). A plant's roots are a bit mask over this layout,
/// so finding where roots can grow next, or whether the network stays connected, takes a few bitwise operations.
/// Roots only grow orthogonally, so a tile's Manhattan distance is also the shortest root path to it from the plant.
/// Each tile caches the matching 25 tiles around it (EnvironmentData.rootZone).
/// </summary>
internal static class RootZone
{
    internal const int MAX_DISTANCE = 3;
    internal const int SIZE = 25;
    internal const uint BASE_BIT = 1u;

    internal static readonly Vector3Int[] Offsets = new Vector3Int[SIZE];
    internal static readonly int[] Distance = new int[SIZE];
    internal static readonly uint[] Orthogonal = new uint[SIZE];               // Each slot's orthogonal neighbours in the layout
    internal static readonly uint[] WithinDistance = new uint[MAX_DISTANCE + 1]; // Slots at most this far from the plant

    static RootZone()
    {
        int count = 0;
        for (int d = 0; d <= MAX_DISTANCE; d++)
        {
            for (int dx = -d; dx <= d; dx++)
            {
                int dy = d - Mathf.Abs(dx);
                Add(dx, dy, d, ref count);
                if (dy != 0)
                {
                    Add(dx, -dy, d, ref count);
                }
            }
        }

        for (int i = 0; i < SIZE; i++)
        {
            for (int j = 0; j < SIZE; j++)
            {
                if (Mathf.Abs(Offsets[i].x - Offsets[j].x) + Mathf.Abs(Offsets[i].y - Offsets[j].y) == 1)
                {
                    Orthogonal[i] |= 1u << j;
                }
            }
            for (int d = Distance[i]; d <= MAX_DISTANCE; d++)
            {
                WithinDistance[d] |= 1u << i;
            }
        }
    }

    private static void Add(int dx, int dy, int distance, ref int count)
    {
        Offsets[count] = new Vector3Int(dx, dy, 0);
        Distance[count] = distance;
        count++;
    }

    /// <summary>
    /// Layout slot of an offset from the plant, or -1 if it is beyond MAX_DISTANCE.
    /// </summary>
    internal static int IndexOf(int dx, int dy)
    {
        for (int i = 0; i < SIZE; i++)
        {
            if (Offsets[i].x == dx && Offsets[i].y == dy)
                return i;
        }
        return -1;
    }

    internal static int CountBits(uint mask)
    {
        int count = 0;
        while (mask != 0)
        {
            mask &= mask - 1;
            count++;
        }
        return count;
    }

    /// <summary>
    /// Slots orthogonally next to the network but not in it: where a new root could grow.
    /// </summary>
    internal static uint Frontier(uint mask)
    {
        uint frontier = 0u;
        for (int i = 0; i < SIZE; i++)
        {
            if ((mask & (1u << i)) != 0)
            {
                frontier |= Orthogonal[i];
            }
        }
        return frontier & ~mask;
    }

    /// <summary>
    /// True if every slot in the mask joins the plant's own tile (bit 0) through orthogonal steps within the mask.
    /// </summary>
    internal static bool IsConnected(uint mask)
    {
        uint reached = mask & BASE_BIT;
        if (reached == 0)
            return false;

        while (true)
        {
            uint next = reached;
            for (int i = 0; i < SIZE; i++)
            {
                if ((reached & (1u << i)) != 0)
                {
                    next |= Orthogonal[i] & mask;
                }
            }
            if (next == reached)
                return reached == mask;
            reached = next;
        }
    }
}

using System;
using System.Collections.Generic;

/// <summary>
/// Simple reusable list pool to reduce per-frame allocations.
/// </summary>
public sealed class ListPool<T>
{
    private readonly Stack<List<T>> pool;
    private readonly int maxSize;

    public ListPool(int initialSize, int maxSize)
    {
        this.maxSize = Math.Max(0, maxSize);
        pool = new Stack<List<T>>(this.maxSize);
        int count = Math.Max(0, initialSize);
        for (int i = 0; i < count; i++)
        {
            pool.Push(new List<T>());
        }
    }

    public PooledList<T> Get()
    {
        List<T> list = pool.Count > 0 ? pool.Pop() : new List<T>();
        list.Clear();
        return new PooledList<T>(list, this);
    }

    internal void Return(List<T> list)
    {
        if (list == null)
            return;
        if (pool.Count >= maxSize)
            return;
        list.Clear();
        pool.Push(list);
    }
}

public readonly struct PooledList<T> : IDisposable
{
    public List<T> List { get; }
    private readonly ListPool<T> pool;

    internal PooledList(List<T> list, ListPool<T> pool)
    {
        List = list;
        this.pool = pool;
    }

    public void Dispose()
    {
        if (pool != null && List != null)
        {
            pool.Return(List);
        }
    }
}

namespace OneCode.Core.Collections;

/// <summary>
/// 固定容量环形缓冲区，满时自动覆盖最旧元素。
/// 纯 BCL 实现，Core 层内部使用（禁止引用 Infrastructure/CircularBuffer）。
/// </summary>
public sealed class FixedSizeRingBuffer<T>(int capacity)
{
    private readonly T[] _buffer = capacity > 0
        ? new T[capacity]
        : throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive.");

    private int _head;
    private int _count;

    public int Count => _count;
    public int Capacity => capacity;
    public bool IsFull => _count == capacity;

    /// <summary>添加元素，满时覆盖最旧元素。</summary>
    public void Add(T item)
    {
        var index = (_head + _count) % capacity;
        if (IsFull)
            _head = (_head + 1) % capacity;
        else
            _count++;

        _buffer[index] = item;
    }

    /// <summary>按时间顺序返回（最旧在前）。</summary>
    public IEnumerable<T> AsEnumerable()
    {
        for (var i = 0; i < _count; i++)
            yield return _buffer[(_head + i) % capacity];
    }

    /// <summary>获取最近添加的元素，缓冲区为空时返回 default。</summary>
    public T? LastOrDefault()
        => _count == 0 ? default : _buffer[(_head + _count - 1) % capacity];

    /// <summary>清空所有元素。</summary>
    public void Clear()
    {
        Array.Clear(_buffer, 0, _buffer.Length);
        _head = 0;
        _count = 0;
    }
}

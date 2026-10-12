using System;
using System.Collections.Generic;

namespace PKGMerge.Core;

public sealed class TransferRateMeter(TimeSpan window)
{
    private readonly TimeSpan _window = window;
    private readonly List<(TimeSpan Time, long Bytes)> _samples = [];

    private int _first;

    public TransferRateMeter() : this(TimeSpan.FromSeconds(8))
    {
    }

    public double BytesPerSecond { get; private set; }

    public void Reset()
    {
        _samples.Clear();
        _first = 0;
        BytesPerSecond = 0;
    }

    public void AddSample(TimeSpan elapsed, long totalBytes)
    {
        _samples.Add((elapsed, totalBytes));

        while (_samples.Count - _first > 2 && _samples[_first + 1].Time <= elapsed - _window)
            _first++;

        if (_first > _samples.Count / 2)
        {
            _samples.RemoveRange(0, _first);
            _first = 0;
        }

        (TimeSpan firstTime, long firstBytes) = _samples[_first];
        double seconds = (elapsed - firstTime).TotalSeconds;

        BytesPerSecond = seconds > 0 ? Math.Max(0, totalBytes - firstBytes) / seconds : 0;
    }

    public TimeSpan? EstimateRemaining(long remainingBytes)
    {
        if (remainingBytes <= 0)
            return TimeSpan.Zero;

        if (BytesPerSecond <= 0)
            return null;

        double seconds = remainingBytes / BytesPerSecond;
        return seconds < TimeSpan.MaxValue.TotalSeconds ? TimeSpan.FromSeconds(Math.Ceiling(seconds)) : null;
    }
}

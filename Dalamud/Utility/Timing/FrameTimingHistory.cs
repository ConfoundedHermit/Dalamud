using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;

namespace Dalamud.Utility.Timing;

/// <summary>Time-ordered records stored in immutable blocks that successive reads can share.</summary>
internal sealed class FrameTimingHistory : IReadOnlyList<FrameTimingRecord>
{
    // Limit block size when a 50 ms time bucket contains a burst of records.
    private const int MaxBlockRecords = 512;
    private static readonly long BlockTicks = Math.Max(1, Stopwatch.Frequency / 20);
    private static readonly FrameTimingHistory Empty = new([], 0);

    private FrameTimingHistory(ImmutableArray<ImmutableArray<FrameTimingRecord>> blocks, int length)
    {
        this.Blocks = blocks;
        this.Length = length;
    }

    /// <summary>Gets the total number of records.</summary>
    public int Length { get; }

    /// <inheritdoc/>
    public int Count => this.Length;

    /// <summary>Gets the immutable blocks in time order.</summary>
    internal ImmutableArray<ImmutableArray<FrameTimingRecord>> Blocks { get; }

    /// <inheritdoc/>
    public FrameTimingRecord this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            foreach (var block in this.Blocks)
            {
                if (index < block.Length)
                    return block[index];

                index -= block.Length;
            }

            throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    /// <inheritdoc/>
    public IEnumerator<FrameTimingRecord> GetEnumerator()
    {
        foreach (var block in this.Blocks)
        {
            foreach (var record in block)
                yield return record;
        }
    }

    /// <inheritdoc/>
    IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();

    /// <summary>Copies records into immutable blocks and reuses matching blocks from the previous read.</summary>
    /// <param name="records">The records in time order.</param>
    /// <param name="previous">The previous history for the same session, if available.</param>
    /// <returns>The immutable history.</returns>
    internal static FrameTimingHistory Create(ReadOnlySpan<FrameTimingRecord> records, FrameTimingHistory? previous)
    {
        if (records.IsEmpty)
            return Empty;

        var previousBlocks = previous?.Blocks ?? [];
        var blocks = ImmutableArray.CreateBuilder<ImmutableArray<FrameTimingRecord>>(previousBlocks.Length);
        var previousIndex = 0;
        var unchanged = true;
        var length = records.Length;
        while (!records.IsEmpty)
        {
            // Use fixed clock buckets so evicting older records leaves later buckets aligned.
            var bucket = records[0].StartTick / BlockTicks;
            var count = 1;
            while (count < Math.Min(records.Length, MaxBlockRecords) && records[count].StartTick / BlockTicks == bucket)
                count++;

            while (previousIndex < previousBlocks.Length && previousBlocks[previousIndex][0].StartTick / BlockTicks < bucket)
                previousIndex++;

            var block = records[..count];
            if (previousIndex < previousBlocks.Length && previousBlocks[previousIndex][0].StartTick / BlockTicks == bucket)
            {
                var candidate = previousBlocks[previousIndex++];
                if (block.SequenceEqual(candidate.AsSpan()))
                {
                    unchanged &= previousIndex == blocks.Count + 1;
                    blocks.Add(candidate);
                    records = records[count..];
                    continue;
                }
            }

            unchanged = false;
            blocks.Add(ImmutableArray.Create(block));
            records = records[count..];
        }

        return previous is not null && unchanged && blocks.Count == previousBlocks.Length
                   ? previous
                   : new(blocks.DrainToImmutable(), length);
    }
}

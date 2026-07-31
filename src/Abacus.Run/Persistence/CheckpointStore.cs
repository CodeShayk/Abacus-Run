using System.Collections.Concurrent;
using System.Text.Json;
using Abacus.Run.Core;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;

namespace Abacus.Run.Persistence;

public sealed record CheckpointRecord
{
    public required string SessionId { get; init; }
    public required string CheckpointId { get; init; }
    public string? ParentCheckpointId { get; init; }
    public byte[]? Payload { get; init; }
    public string? BlobUri { get; init; }
    public required int SizeBytes { get; init; }
    public required DateTimeOffset CommittedAt { get; init; }
}

/// <summary>
/// Checkpoint store with blob overflow for large payloads.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RetrieveIndexAsync"/> must return checkpoints oldest-first: the framework's
/// <c>CheckpointManager</c> takes the last element as the resume point, so a store that returns them
/// unordered resumes the wrong checkpoint.
/// </para>
/// <para>
/// Ordering is by commit time <em>then checkpoint id</em>. Timestamps can collide within a single
/// superstep at datetime precision; ids are monotonic, so the tiebreak keeps the order total.
/// </para>
/// </remarks>
public sealed class OverflowCheckpointStore : ICheckpointStore<JsonElement>
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, CheckpointRecord>> _checkpoints =
        new(StringComparer.Ordinal);

    private readonly IBlobStore? _blobs;
    private readonly int _inlineThresholdBytes;
    private readonly TimeProvider _clock;

    public OverflowCheckpointStore(
        IBlobStore? blobs = null, int inlineThresholdBytes = 256 * 1024, TimeProvider? clock = null)
    {
        _blobs = blobs;
        _inlineThresholdBytes = inlineThresholdBytes;
        _clock = clock ?? TimeProvider.System;
    }

    public int SessionCount => _checkpoints.Count;

    public async ValueTask<CheckpointInfo> CreateCheckpointAsync(
        string sessionId, JsonElement value, CheckpointInfo? parent = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);

        string checkpointId = IdGenerator.NewId();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value);

        string? blobUri = null;
        byte[]? inline = bytes;

        if (bytes.Length > _inlineThresholdBytes && _blobs is not null)
        {
            blobUri = await _blobs.UploadAsync($"{sessionId}/{checkpointId}.json", bytes, CancellationToken.None)
                .ConfigureAwait(false);
            inline = null;
        }

        var record = new CheckpointRecord
        {
            SessionId = sessionId,
            CheckpointId = checkpointId,
            ParentCheckpointId = parent?.CheckpointId,
            Payload = inline,
            BlobUri = blobUri,
            SizeBytes = bytes.Length,
            CommittedAt = _clock.GetUtcNow()
        };

        _checkpoints.GetOrAdd(sessionId, _ => new ConcurrentDictionary<string, CheckpointRecord>(StringComparer.Ordinal))
            [checkpointId] = record;

        return new CheckpointInfo(sessionId, checkpointId);
    }

    public ValueTask<IEnumerable<CheckpointInfo>> RetrieveIndexAsync(string sessionId, CheckpointInfo? withParent = null)
    {
        if (!_checkpoints.TryGetValue(sessionId, out ConcurrentDictionary<string, CheckpointRecord>? session))
        {
            return ValueTask.FromResult<IEnumerable<CheckpointInfo>>([]);
        }

        IEnumerable<CheckpointRecord> records = session.Values;
        if (withParent is not null)
        {
            records = records.Where(r => r.ParentCheckpointId == withParent.CheckpointId);
        }

        CheckpointInfo[] ordered = records
            .OrderBy(r => r.CommittedAt)
            .ThenBy(r => r.CheckpointId, StringComparer.Ordinal)   // total order under timestamp collision
            .Select(r => new CheckpointInfo(r.SessionId, r.CheckpointId))
            .ToArray();

        return ValueTask.FromResult<IEnumerable<CheckpointInfo>>(ordered);
    }

    public async ValueTask<JsonElement> RetrieveCheckpointAsync(string sessionId, CheckpointInfo key)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (!_checkpoints.TryGetValue(sessionId, out ConcurrentDictionary<string, CheckpointRecord>? session) ||
            !session.TryGetValue(key.CheckpointId, out CheckpointRecord? record))
        {
            throw new KeyNotFoundException($"Checkpoint '{key.CheckpointId}' not found for session '{sessionId}'.");
        }

        byte[] bytes = record.Payload
            ?? await _blobs!.DownloadAsync(record.BlobUri!, CancellationToken.None).ConfigureAwait(false);

        return JsonSerializer.Deserialize<JsonElement>(bytes);
    }

    /// <summary>Checkpoint metadata for the control plane. Not part of the framework contract.</summary>
    public IReadOnlyList<CheckpointRecord> Describe(string sessionId)
        => _checkpoints.TryGetValue(sessionId, out ConcurrentDictionary<string, CheckpointRecord>? session)
            ? session.Values.OrderBy(r => r.CommittedAt).ThenBy(r => r.CheckpointId, StringComparer.Ordinal).ToArray()
            : [];

    /// <summary>Retention pruning. Returns the number of checkpoints removed.</summary>
    public int Prune(DateTimeOffset olderThan)
    {
        int removed = 0;
        foreach ((string sessionId, ConcurrentDictionary<string, CheckpointRecord> session) in _checkpoints)
        {
            foreach (CheckpointRecord record in session.Values.Where(r => r.CommittedAt < olderThan))
            {
                if (session.TryRemove(record.CheckpointId, out _))
                {
                    removed++;
                }
            }

            if (session.IsEmpty)
            {
                _checkpoints.TryRemove(sessionId, out _);
            }
        }
        return removed;
    }
}

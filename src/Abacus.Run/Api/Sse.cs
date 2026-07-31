using System.Threading.Channels;
using Abacus.Run.Abstractions;
using Abacus.Run.Core;
using Microsoft.AspNetCore.Http;

namespace Abacus.Run.Api;

public static class Sse
{
    /// <summary>
    /// Writes one SSE frame. Shared by the live stream and the history endpoint's <c>format=sse</c>
    /// mode, which is what makes "same events, same shape, different transport" structurally true
    /// rather than a promise.
    /// </summary>
    public static async Task WriteEventAsync(HttpResponse response, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(envelope);

        await response.WriteAsync(
            $"event: {envelope.EventType}\nid: {envelope.Sequence}\ndata: {envelope.PayloadJson}\n\n",
            cancellationToken).ConfigureAwait(false);

        await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task StreamAsync(
        HttpContext http,
        string instanceId,
        long fromExclusive,
        bool isTerminal,
        IEventStore store,
        IEventBus? bus,
        CancellationToken cancellationToken,
        TimeSpan? heartbeat = null)
    {
        ArgumentNullException.ThrowIfNull(http);

        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";   // defeat proxy buffering
        await http.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);

        // Subscribe BEFORE reading history. Events landing between the backfill read and the
        // subscription would otherwise be lost; buffering first and de-duplicating by sequence
        // closes that window.
        IAsyncEnumerable<EventEnvelope>? live = bus?.SubscribeAsync(instanceId, cancellationToken);
        Channel<EventEnvelope>? buffer = null;
        Task? pump = null;

        if (live is not null)
        {
            buffer = Channel.CreateUnbounded<EventEnvelope>();
            pump = Task.Run(async () =>
            {
                try
                {
                    await foreach (EventEnvelope envelope in live.WithCancellation(cancellationToken).ConfigureAwait(false))
                    {
                        await buffer.Writer.WriteAsync(envelope, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    // client disconnected
                }
                finally
                {
                    buffer.Writer.TryComplete();
                }
            }, cancellationToken);
        }

        long lastSequence = fromExclusive;

        await foreach (EventEnvelope envelope in store.ReadAsync(instanceId, fromExclusive, cancellationToken).ConfigureAwait(false))
        {
            await WriteEventAsync(http.Response, envelope, cancellationToken).ConfigureAwait(false);
            lastSequence = Math.Max(lastSequence, envelope.Sequence);
        }

        if (isTerminal || buffer is null)
        {
            // Terminal instance: replay history, then close rather than erroring.
            buffer?.Writer.TryComplete();
            return;
        }

        TimeSpan interval = heartbeat ?? TimeSpan.FromSeconds(15);
        using var heartbeatTimer = new PeriodicTimer(interval);

        Task<bool> nextHeartbeat = heartbeatTimer.WaitForNextTickAsync(cancellationToken).AsTask();
        ValueTask<bool> nextEvent = buffer.Reader.WaitToReadAsync(cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            Task<bool> eventTask = nextEvent.AsTask();
            Task completed = await Task.WhenAny(eventTask, nextHeartbeat).ConfigureAwait(false);

            if (completed == eventTask)
            {
                if (!await eventTask.ConfigureAwait(false))
                {
                    break;
                }

                while (buffer.Reader.TryRead(out EventEnvelope? envelope))
                {
                    if (envelope.Sequence <= lastSequence)
                    {
                        continue;   // already delivered during backfill
                    }

                    await WriteEventAsync(http.Response, envelope, cancellationToken).ConfigureAwait(false);
                    lastSequence = envelope.Sequence;

                    if (envelope.EventType == WorkflowEventTypes.WorkflowTerminated)
                    {
                        return;
                    }
                }

                nextEvent = buffer.Reader.WaitToReadAsync(cancellationToken);
            }
            else
            {
                if (!await nextHeartbeat.ConfigureAwait(false))
                {
                    break;
                }

                await http.Response.WriteAsync(": heartbeat\n\n", cancellationToken).ConfigureAwait(false);
                await http.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
                nextHeartbeat = heartbeatTimer.WaitForNextTickAsync(cancellationToken).AsTask();
            }
        }

        if (pump is not null)
        {
            try { await pump.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }
}

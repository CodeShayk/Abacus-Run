using System.Text.Json;
using Abacus.Run.Abstractions;
using Abacus.Run.Core;
using Abacus.Run.Persistence;
using FluentAssertions;
using Xunit;

namespace Abacus.Run.UnitTests.Core;

/// <summary>
/// The audit hook is a framework contract that workflow definitions build on, so its guarantees are
/// tested here rather than only through a workflow that happens to use it.
/// </summary>
public class WorkflowAuditRecorderTests
{
    private static readonly AuditRecordDefinition Definition = new(
        "order",
        "One processed order.",
        [
            new AuditSectionDefinition("submission", "What was submitted.", Multiple: false),
            new AuditSectionDefinition("step", "One processing step.")
        ]);

    private static WorkflowAuditRecorder CreateRecorder(IAuditRecordStore store, string instanceId = "wf-1")
        => new(Definition, store, instanceId, "orders", "1.0.0");

    [Fact]
    public async Task Opening_writes_the_root_with_its_key_and_attributes()
    {
        var store = new InMemoryAuditRecordStore();
        WorkflowAuditRecorder recorder = CreateRecorder(store);

        await recorder.OpenAsync("ORDER-1", new Dictionary<string, object?> { ["channel"] = "web" }, default);

        AuditRecordDocument? document = await store.GetAsync("wf-1", default);

        document.Should().NotBeNull();
        document!.Root.RootKind.Should().Be("order");
        document.Root.RootKey.Should().Be("ORDER-1");
        document.Root.WorkflowName.Should().Be("orders");
        document.Root.Status.Should().Be(AuditRecordStatus.Open);
        document.Root.AttributesJson.Should().Contain("web");
    }

    [Fact]
    public async Task Entries_are_sequenced_in_the_order_they_are_recorded()
    {
        var store = new InMemoryAuditRecordStore();
        WorkflowAuditRecorder recorder = CreateRecorder(store);
        await recorder.OpenAsync("ORDER-1", null, default);

        await recorder.RecordAsync("submission", null, new { total = 10 }, default);
        await recorder.RecordAsync("step", "pick", new { warehouse = "A" }, default);
        await recorder.RecordAsync("step", "pack", new { warehouse = "A" }, default);

        AuditRecordDocument document = (await store.GetAsync("wf-1", default))!;

        document.Entries.Select(e => e.Sequence).Should().BeInAscendingOrder();
        document.Entries.Select(e => e.Key).Should().ContainInOrder([null, "pick", "pack"]);
    }

    [Fact]
    public async Task An_undeclared_section_is_dropped_rather_than_stored()
    {
        var store = new InMemoryAuditRecordStore();
        WorkflowAuditRecorder recorder = CreateRecorder(store);
        await recorder.OpenAsync("ORDER-1", null, default);

        await recorder.RecordAsync("not-declared", null, new { anything = true }, default);

        AuditRecordDocument document = (await store.GetAsync("wf-1", default))!;
        document.Entries.Should().BeEmpty("the definition's declared shape is the contract");
    }

    [Fact]
    public async Task Re_recording_the_same_section_and_key_replaces_the_entry()
    {
        var store = new InMemoryAuditRecordStore();
        WorkflowAuditRecorder recorder = CreateRecorder(store);
        await recorder.OpenAsync("ORDER-1", null, default);

        await recorder.RecordAsync("step", "pick", new { attempt = 1 }, default);
        await recorder.RecordAsync("step", "pick", new { attempt = 2 }, default);

        AuditRecordDocument document = (await store.GetAsync("wf-1", default))!;

        document.Entries.Should().ContainSingle("a retried step corrects its record rather than contradicting it");
        document.Entries[0].PayloadJson.Should().Contain("2");
    }

    [Fact]
    public async Task Closing_settles_the_root_and_keeps_the_entries()
    {
        var store = new InMemoryAuditRecordStore();
        WorkflowAuditRecorder recorder = CreateRecorder(store);
        await recorder.OpenAsync("ORDER-1", null, default);
        await recorder.RecordAsync("step", "pick", new { ok = true }, default);

        await recorder.CloseAsync(AuditRecordStatus.Completed, default);

        AuditRecordDocument document = (await store.GetAsync("wf-1", default))!;
        document.Root.Status.Should().Be(AuditRecordStatus.Completed);
        document.Root.ClosedUtc.Should().NotBeNull();
        document.Entries.Should().ContainSingle();
    }

    [Fact]
    public async Task A_store_failure_never_propagates_to_the_workflow()
    {
        // Audit writes explain work that already happened. Failing the work because its explanation
        // could not be filed would trade a correct result for a missing one.
        WorkflowAuditRecorder recorder = CreateRecorder(new ThrowingAuditRecordStore());

        Func<Task> open = async () => await recorder.OpenAsync("ORDER-1", null, default);
        Func<Task> record = async () => await recorder.RecordAsync("step", "pick", new { ok = true }, default);
        Func<Task> close = async () => await recorder.CloseAsync(AuditRecordStatus.Completed, default);

        await open.Should().NotThrowAsync();
        await record.Should().NotThrowAsync();
        await close.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Payloads_are_stored_as_json()
    {
        var store = new InMemoryAuditRecordStore();
        WorkflowAuditRecorder recorder = CreateRecorder(store);
        await recorder.OpenAsync("ORDER-1", null, default);

        await recorder.RecordAsync("step", "pick", new { warehouse = "A", items = new[] { 1, 2 } }, default);

        AuditRecordDocument document = (await store.GetAsync("wf-1", default))!;
        using JsonDocument payload = JsonDocument.Parse(document.Entries[0].PayloadJson);

        payload.RootElement.GetProperty("warehouse").GetString().Should().Be("A");
        payload.RootElement.GetProperty("items").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task Closing_without_opening_writes_nothing()
    {
        var store = new InMemoryAuditRecordStore();
        WorkflowAuditRecorder recorder = CreateRecorder(store);

        await recorder.CloseAsync(AuditRecordStatus.Failed, default);

        (await store.GetAsync("wf-1", default)).Should().BeNull(
            "closing a record that was never opened would write a root with no identity");
    }

    [Fact]
    public async Task Roots_are_listed_per_workflow_and_filtered_by_status()
    {
        var store = new InMemoryAuditRecordStore();

        WorkflowAuditRecorder first = CreateRecorder(store, "wf-1");
        await first.OpenAsync("ORDER-1", null, default);
        await first.CloseAsync(AuditRecordStatus.Completed, default);

        WorkflowAuditRecorder second = CreateRecorder(store, "wf-2");
        await second.OpenAsync("ORDER-2", null, default);

        (await store.ListAsync("orders", null, null, 10, default)).Should().HaveCount(2);
        (await store.ListAsync("orders", null, AuditRecordStatus.Open, 10, default)).Should().ContainSingle();
        (await store.ListAsync("orders", "ORDER-1", null, 10, default)).Should().ContainSingle();
        (await store.ListAsync("other-workflow", null, null, 10, default)).Should().BeEmpty();
    }

    private sealed class ThrowingAuditRecordStore : IAuditRecordStore
    {
        public ValueTask UpsertRootAsync(AuditRecordRoot root, CancellationToken cancellationToken)
            => throw new InvalidOperationException("storage is down");

        public ValueTask AppendAsync(AuditRecordEntry entry, CancellationToken cancellationToken)
            => throw new InvalidOperationException("storage is down");

        public ValueTask<AuditRecordDocument?> GetAsync(string instanceId, CancellationToken cancellationToken)
            => throw new InvalidOperationException("storage is down");

        public ValueTask<IReadOnlyList<AuditRecordRoot>> ListAsync(
            string workflowName, string? rootKey, string? status, int limit, CancellationToken cancellationToken)
            => throw new InvalidOperationException("storage is down");
    }
}

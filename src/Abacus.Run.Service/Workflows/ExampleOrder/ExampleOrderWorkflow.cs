using Abacus.Run.Abstractions;
using Microsoft.Agents.AI.Workflows;

namespace Abacus.Run.Service.Workflows.ExampleOrder;

/// <summary>What one run is asked to price. <c>FailOnSku</c> exists so the failure path is reachable.</summary>
public sealed record ExampleOrderContext(
    string OrderId = "ORD-1",
    IReadOnlyList<ExampleOrderLine>? Lines = null,
    string? FailOnSku = null)
{
    public IReadOnlyList<ExampleOrderLine> Lines { get; init; } = Lines ?? [];
}

public sealed record ExampleOrderLine(string Sku, int Quantity, decimal UnitPrice);

public sealed record ExampleOrderResult(string OrderId, decimal Total, int LineCount);

/// <summary>
/// A worked example of the framework's audit hook, and the only workflow this host ships.
/// </summary>
/// <remarks>
/// It is deliberately dull work — plan, price each line, total — because the point is the auditing
/// around it, not the domain. Read it as the answer to "what does a definition have to do to get an
/// audit record": implement <see cref="IAuditedWorkflowDefinition"/>, declare a shape, and call the
/// recorder the runtime hands each node. Nothing in the framework knows what an order is.
/// </remarks>
public sealed class ExampleOrderWorkflow
    : IWorkflowDefinition<ExampleOrderContext, ExampleOrderResult>, IAuditedWorkflowDefinition
{
    public string Name => "example-order";
    public string Version => "1.0.0";

    public AuditRecordDefinition AuditRecord => ExampleOrderAuditRecord.Definition;

    public ValueTask<Workflow> BuildAsync(WorkflowBuildContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        ExecutorBinding plan = context.Node(new PlanOrder("plan"));
        ExecutorBinding price = context.Node(new PriceLines("price"));

        return new ValueTask<Workflow>(new WorkflowBuilder(plan)
            .AddEdge(plan, price)
            .WithOutputFrom(price)
            .WithName(Name)
            .Build());
    }

    /// <summary>Opens the record and files what it was asked to do, before doing any of it.</summary>
    private sealed class PlanOrder(string id) : HostExecutor<ExampleOrderContext, ExampleOrderContext>(id)
    {
        protected override async ValueTask<ExampleOrderContext> ExecuteCoreAsync(
            ExampleOrderContext input, IWorkflowContext context, CancellationToken cancellationToken)
        {
            // Null whenever the workflow declares no record, so every call site is guarded. Here it is
            // never null in practice — the guard is the habit an example should teach.
            if (Runtime.Audit is { } audit)
            {
                // Opening is idempotent per instance, so a retried attempt reuses the same root
                // rather than starting a second record for the same run.
                await audit.OpenAsync(
                    input.OrderId,
                    new Dictionary<string, object?>
                    {
                        ["lineCount"] = input.Lines.Count,
                        ["attempt"] = Runtime.Attempt
                    },
                    cancellationToken).ConfigureAwait(false);

                await audit.RecordAsync(
                    ExampleOrderAuditRecord.Submission, null, input, cancellationToken).ConfigureAwait(false);

                await audit.RecordAsync(
                    ExampleOrderAuditRecord.Plan,
                    null,
                    new { steps = new[] { "price-lines", "total" }, skus = input.Lines.Select(l => l.Sku) },
                    cancellationToken).ConfigureAwait(false);
            }

            return input;
        }
    }

    /// <summary>
    /// Prices each line, filing one entry per SKU. Keying entries by SKU is what makes a retry
    /// correct the record instead of doubling it — a re-record of the same (section, key) replaces.
    /// </summary>
    private sealed class PriceLines(string id) : HostExecutor<ExampleOrderContext, ExampleOrderResult>(id)
    {
        protected override async ValueTask<ExampleOrderResult> ExecuteCoreAsync(
            ExampleOrderContext input, IWorkflowContext context, CancellationToken cancellationToken)
        {
            IWorkflowAuditRecorder? audit = Runtime.Audit;
            decimal total = 0m;

            foreach (ExampleOrderLine line in input.Lines)
            {
                if (string.Equals(line.Sku, input.FailOnSku, StringComparison.OrdinalIgnoreCase))
                {
                    var failure = new WorkflowDeadStopException($"Line '{line.Sku}' cannot be priced.");

                    // Recorded *before* the throw. A recorder failure is swallowed by design, so the
                    // record is only useful if it is written while the run can still write it —
                    // filing the explanation after letting the exception go loses the explanation.
                    if (audit is not null)
                    {
                        await audit.RecordAsync(
                            ExampleOrderAuditRecord.Outcome,
                            null,
                            new { status = "Failed", failedSku = line.Sku, reason = failure.Message, total },
                            cancellationToken).ConfigureAwait(false);

                        await audit.CloseAsync(AuditRecordStatus.Failed, cancellationToken).ConfigureAwait(false);
                    }

                    throw failure;
                }

                decimal lineTotal = line.Quantity * line.UnitPrice;
                total += lineTotal;

                if (audit is not null)
                {
                    await audit.RecordAsync(
                        ExampleOrderAuditRecord.Line,
                        line.Sku,
                        new { line.Sku, line.Quantity, line.UnitPrice, lineTotal },
                        cancellationToken).ConfigureAwait(false);
                }
            }

            var result = new ExampleOrderResult(input.OrderId, total, input.Lines.Count);

            if (audit is not null)
            {
                await audit.RecordAsync(
                    ExampleOrderAuditRecord.Outcome,
                    null,
                    new { status = "Completed", result.Total, result.LineCount },
                    cancellationToken).ConfigureAwait(false);

                await audit.CloseAsync(AuditRecordStatus.Completed, cancellationToken).ConfigureAwait(false);
            }

            return result;
        }
    }
}

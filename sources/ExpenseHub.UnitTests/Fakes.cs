using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ExpenseHub.Core.Common;
using ExpenseHub.Core.Domain;
using ExpenseHub.Core.Reports;
using Microsoft.EntityFrameworkCore;

namespace ExpenseHub.UnitTests;

/// <summary>Relógio fixo para resultados determinísticos.</summary>
internal sealed class FixedTimeProvider : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Repositório em memória da aplicação web.</summary>
internal sealed class FakeReportRequestRepository : IReportRequestRepository
{
    public List<ReportRequest> Reports { get; } = [];

    public bool ThrowOnAdd { get; set; }

    public Task AddAsync(ReportRequest report, CancellationToken cancellationToken)
    {
        if (ThrowOnAdd)
        {
            throw new DbUpdateException("banco indisponível");
        }

        Reports.Add(report);
        return Task.CompletedTask;
    }

    public Task<ReportRequest?> FindOwnedAsync(Guid id, string requesterId, CancellationToken cancellationToken) =>
        Task.FromResult(Reports.FirstOrDefault(r => r.Id == id && r.RequesterId == requesterId));

    public Task<IReadOnlyList<ReportRequest>> ListOwnedAsync(string requesterId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ReportRequest>>([.. Reports.Where(r => r.RequesterId == requesterId)]);
}

/// <summary>Publisher que registra mensagens e simula (ou não) a confirmação do broker.</summary>
internal sealed class FakeReportPublisher : IReportPublisher
{
    public List<ReportMessage> Published { get; } = [];

    public bool Confirm { get; set; } = true;

    public Task<bool> TryPublishAsync(ReportMessage message, CancellationToken cancellationToken)
    {
        if (Confirm)
        {
            Published.Add(message);
        }

        return Task.FromResult(Confirm);
    }
}

/// <summary>
/// Store em memória com a mesma semântica condicional do EF (atualiza só se não finalizado).
/// Compartilha a lista de relatórios com o repositório web para testar o fluxo completo.
/// </summary>
internal sealed class FakeReportProcessingStore : IReportProcessingStore
{
    public FakeReportProcessingStore(List<ReportRequest> reports) => Reports = reports;

    public List<ReportRequest> Reports { get; }

    public List<Expense> Expenses { get; } = [];

    public int CompleteWrites { get; private set; }

    public int LoadCalls { get; private set; }

    public bool ThrowOnComplete { get; set; }

    public Task<ReportRequest?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(Copy(Reports.FirstOrDefault(r => r.Id == id)));

    public Task<bool> TryMarkProcessingAsync(Guid id, DateTime startedAt, CancellationToken cancellationToken)
    {
        ReportRequest? report = Reports.FirstOrDefault(r => r.Id == id && r.Status is ReportStatus.Pending or ReportStatus.Processing);
        if (report is null)
        {
            return Task.FromResult(false);
        }

        report.Status = ReportStatus.Processing;
        report.StartedAt ??= startedAt;
        report.Attempts++;
        return Task.FromResult(true);
    }

    public Task<bool> TryCompleteAsync(Guid id, string resultJson, DateTime completedAt, CancellationToken cancellationToken)
    {
        if (ThrowOnComplete)
        {
            throw new DbUpdateException("falha ao salvar");
        }

        ReportRequest? report = Unfinished(id);
        if (report is null)
        {
            return Task.FromResult(false);
        }

        report.Status = ReportStatus.Completed;
        report.ResultJson = resultJson;
        report.CompletedAt = completedAt;
        CompleteWrites++;
        return Task.FromResult(true);
    }

    public Task<bool> TryFailAsync(Guid id, string errorCode, string errorMessage, DateTime completedAt, CancellationToken cancellationToken)
    {
        ReportRequest? report = Unfinished(id);
        if (report is null)
        {
            return Task.FromResult(false);
        }

        report.Status = ReportStatus.Failed;
        report.ErrorCode = errorCode;
        report.ErrorMessage = errorMessage;
        report.CompletedAt = completedAt;
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<ExpenseAmount>> LoadAmountsAsync(ReportScope scope, string requesterId, DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken)
    {
        LoadCalls++;
        IReadOnlyList<ExpenseAmount> amounts =
        [
            .. ReportFilter.Apply(Expenses.AsQueryable(), scope, requesterId, fromDate, toDate)
                .Select(e => new ExpenseAmount(e.Status, e.Amount)),
        ];
        return Task.FromResult(amounts);
    }

    private static ReportRequest? Copy(ReportRequest? r) => r is null ? null : new ReportRequest
    {
        Id = r.Id,
        RequesterId = r.RequesterId,
        Scope = r.Scope,
        FromDate = r.FromDate,
        ToDate = r.ToDate,
        Status = r.Status,
        CreatedAt = r.CreatedAt,
        StartedAt = r.StartedAt,
        CompletedAt = r.CompletedAt,
        ResultJson = r.ResultJson,
        ErrorCode = r.ErrorCode,
        ErrorMessage = r.ErrorMessage,
        Attempts = r.Attempts,
    };

    private ReportRequest? Unfinished(Guid id) =>
        Reports.FirstOrDefault(r => r.Id == id && r.Status is not ReportStatus.Completed and not ReportStatus.Failed);
}

/// <summary>Dados e usuários de exemplo da apostila.</summary>
internal static class TestData
{
    public const string Alice = "alice";
    public const string Bob = "bob";
    public const string Carol = "carol-auditor";

    public static readonly DateOnly From = new(2026, 9, 1);
    public static readonly DateOnly To = new(2026, 9, 30);

    public static UserContext Employee(string id) => new(id, [Roles.Employee]);

    public static UserContext Auditor(string id) => new(id, [Roles.Auditor]);

    public static Expense NewExpense(string owner, DateOnly date, ExpenseStatus status, decimal amount) => new()
    {
        Id = Guid.NewGuid(),
        OwnerId = owner,
        Description = "Despesa de teste",
        ExpenseDate = date,
        Status = status,
        Amount = amount,
        CategoryId = 1,
    };

    /// <summary>Tabela da seção 4 da apostila.</summary>
    public static List<Expense> ApostilaExample() =>
    [
        NewExpense(Alice, new DateOnly(2026, 9, 5), ExpenseStatus.Approved, 100.00m),
        NewExpense(Alice, new DateOnly(2026, 9, 10), ExpenseStatus.Approved, 50.00m),
        NewExpense(Alice, new DateOnly(2026, 9, 15), ExpenseStatus.Paid, 25.00m),
        NewExpense(Bob, new DateOnly(2026, 9, 20), ExpenseStatus.Approved, 200.00m),
        NewExpense(Alice, new DateOnly(2026, 10, 2), ExpenseStatus.Paid, 500.00m),
    ];

    public static StatusSummary Status(ReportResult result, ExpenseStatus status) =>
        result.ByStatus.Single(s => s.Status == status);
}

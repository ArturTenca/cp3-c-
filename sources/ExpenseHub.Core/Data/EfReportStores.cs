using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ExpenseHub.Core.Reports;
using Microsoft.EntityFrameworkCore;

namespace ExpenseHub.Core.Data;

/// <summary>Repositório EF usado pela aplicação web.</summary>
internal sealed class EfReportRequestRepository : IReportRequestRepository
{
    private readonly ExpenseHubDbContext _db;

    public EfReportRequestRepository(ExpenseHubDbContext db) => _db = db;

    public async Task AddAsync(ReportRequest report, CancellationToken cancellationToken)
    {
        _db.ReportRequests.Add(report);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public Task<ReportRequest?> FindOwnedAsync(Guid id, string requesterId, CancellationToken cancellationToken) =>
        _db.ReportRequests.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id && r.RequesterId == requesterId, cancellationToken);

    public async Task<IReadOnlyList<ReportRequest>> ListOwnedAsync(string requesterId, CancellationToken cancellationToken)
    {
        List<ReportRequest> reports = await _db.ReportRequests.AsNoTracking()
            .Where(r => r.RequesterId == requesterId)
            .ToListAsync(cancellationToken);
        return [.. reports.OrderByDescending(r => r.CreatedAt)];
    }
}

/// <summary>Store EF usado pelo worker. Escritas condicionais em um único UPDATE.</summary>
internal sealed class EfReportProcessingStore : IReportProcessingStore
{
    private readonly ExpenseHubDbContext _db;

    public EfReportProcessingStore(ExpenseHubDbContext db) => _db = db;

    public Task<ReportRequest?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        _db.ReportRequests.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    public async Task<bool> TryMarkProcessingAsync(Guid id, DateTime startedAt, CancellationToken cancellationToken)
    {
        int rows = await _db.ReportRequests
            .Where(r => r.Id == id && (r.Status == ReportStatus.Pending || r.Status == ReportStatus.Processing))
            .ExecuteUpdateAsync(
                s => s.SetProperty(r => r.Status, ReportStatus.Processing)
                    .SetProperty(r => r.StartedAt, r => r.StartedAt ?? (DateTime?)startedAt)
                    .SetProperty(r => r.Attempts, r => r.Attempts + 1),
                cancellationToken);
        return rows == 1;
    }

    public async Task<bool> TryCompleteAsync(Guid id, string resultJson, DateTime completedAt, CancellationToken cancellationToken)
    {
        int rows = await _db.ReportRequests
            .Where(r => r.Id == id && r.Status != ReportStatus.Completed && r.Status != ReportStatus.Failed)
            .ExecuteUpdateAsync(
                s => s.SetProperty(r => r.Status, ReportStatus.Completed)
                    .SetProperty(r => r.ResultJson, resultJson)
                    .SetProperty(r => r.CompletedAt, completedAt)
                    .SetProperty(r => r.ErrorCode, (string?)null)
                    .SetProperty(r => r.ErrorMessage, (string?)null),
                cancellationToken);
        return rows == 1;
    }

    public async Task<bool> TryFailAsync(Guid id, string errorCode, string errorMessage, DateTime completedAt, CancellationToken cancellationToken)
    {
        int rows = await _db.ReportRequests
            .Where(r => r.Id == id && r.Status != ReportStatus.Completed && r.Status != ReportStatus.Failed)
            .ExecuteUpdateAsync(
                s => s.SetProperty(r => r.Status, ReportStatus.Failed)
                    .SetProperty(r => r.ErrorCode, errorCode)
                    .SetProperty(r => r.ErrorMessage, errorMessage)
                    .SetProperty(r => r.CompletedAt, completedAt)
                    .SetProperty(r => r.ResultJson, (string?)null),
                cancellationToken);
        return rows == 1;
    }

    public async Task<IReadOnlyList<ExpenseAmount>> LoadAmountsAsync(ReportScope scope, string requesterId, DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken) =>
        await ReportFilter.Apply(_db.Expenses.AsNoTracking(), scope, requesterId, fromDate, toDate)
            .Select(e => new ExpenseAmount(e.Status, e.Amount))
            .ToListAsync(cancellationToken);
}

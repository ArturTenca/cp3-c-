using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ExpenseHub.Core.Reports;

/// <summary>Resultado do processamento de uma mensagem. Somente Transient não é confirmado.</summary>
internal enum ProcessingOutcome
{
    Completed,
    Duplicate,
    Failed,
    NotFound,
    Transient,
}

/// <summary>Processa uma solicitação a partir do registro persistido, de forma idempotente.</summary>
internal sealed class ReportProcessor
{
    private readonly IReportProcessingStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ReportProcessor> _logger;
    private readonly ReportProcessingOptions _options;

    public ReportProcessor(IReportProcessingStore store, TimeProvider timeProvider, ILogger<ReportProcessor> logger, IOptions<ReportProcessingOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _store = store;
        _timeProvider = timeProvider;
        _logger = logger;
        _options = options.Value;
    }

    public async Task<ProcessingOutcome> ProcessAsync(Guid reportId, CancellationToken cancellationToken)
    {
        try
        {
            return await ProcessCoreAsync(reportId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Report {ReportId} transient failure ({ErrorType}); message will not be acknowledged", reportId, ex.GetType().Name);
            return ProcessingOutcome.Transient;
        }
    }

    private async Task<ProcessingOutcome> ProcessCoreAsync(Guid reportId, CancellationToken cancellationToken)
    {
        ReportRequest? report = await _store.FindAsync(reportId, cancellationToken);
        if (report is null)
        {
            _logger.LogWarning("Report {ReportId} not found; message discarded", reportId);
            return ProcessingOutcome.NotFound;
        }

        if (report.Status is ReportStatus.Completed or ReportStatus.Failed)
        {
            _logger.LogInformation("Report {ReportId} duplicate delivery ignored (status {Status})", reportId, report.Status);
            return ProcessingOutcome.Duplicate;
        }

        if (report.Attempts >= _options.MaxAttempts)
        {
            return await FailAsync(reportId, "max_attempts_exceeded", "O processamento excedeu o número máximo de tentativas.", cancellationToken);
        }

        if (report.FromDate > report.ToDate || !Enum.IsDefined(report.Scope) || string.IsNullOrWhiteSpace(report.RequesterId))
        {
            return await FailAsync(reportId, "invalid_request", "A solicitação persistida é inválida.", cancellationToken);
        }

        DateTime startedAt = _timeProvider.GetUtcNow().UtcDateTime;
        if (!await _store.TryMarkProcessingAsync(reportId, startedAt, cancellationToken))
        {
            _logger.LogInformation("Report {ReportId} already finished by another delivery", reportId);
            return ProcessingOutcome.Duplicate;
        }

        _logger.LogInformation("Report {ReportId} processing started (attempt {Attempt})", reportId, report.Attempts + 1);

        IReadOnlyList<ExpenseAmount> amounts = await _store.LoadAmountsAsync(report.Scope, report.RequesterId, report.FromDate, report.ToDate, cancellationToken);
        DateTime generatedAt = _timeProvider.GetUtcNow().UtcDateTime;
        ReportResult result = ReportCalculator.Calculate(amounts, generatedAt);

        if (!await _store.TryCompleteAsync(reportId, ReportJson.SerializeResult(result), generatedAt, cancellationToken))
        {
            _logger.LogInformation("Report {ReportId} duplicate completion ignored", reportId);
            return ProcessingOutcome.Duplicate;
        }

        _logger.LogInformation("Report {ReportId} completed: {TotalCount} expenses", reportId, result.TotalCount);
        return ProcessingOutcome.Completed;
    }

    private async Task<ProcessingOutcome> FailAsync(Guid reportId, string code, string message, CancellationToken cancellationToken)
    {
        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;
        if (!await _store.TryFailAsync(reportId, code, message, now, cancellationToken))
        {
            return ProcessingOutcome.Duplicate;
        }

        _logger.LogWarning("Report {ReportId} failed definitively ({ErrorCode})", reportId, code);
        return ProcessingOutcome.Failed;
    }
}

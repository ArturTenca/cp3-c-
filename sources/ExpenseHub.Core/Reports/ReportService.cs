using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ExpenseHub.Core.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ExpenseHub.Core.Reports;

/// <summary>Regras da aplicação web: solicitar, listar, consultar e exportar relatórios.</summary>
internal sealed class ReportService
{
    private readonly IReportRequestRepository _repository;
    private readonly IReportPublisher _publisher;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ReportService> _logger;

    public ReportService(IReportRequestRepository repository, IReportPublisher publisher, TimeProvider timeProvider, ILogger<ReportService> logger)
    {
        _repository = repository;
        _publisher = publisher;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<ServiceResult<ReportEnvelope>> CreateAsync(CreateReportRequest request, UserContext user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(user);

        if (request.FromDate is not DateOnly fromDate || request.ToDate is not DateOnly toDate || request.Scope is not ReportScope scope)
        {
            return ServiceResult<ReportEnvelope>.Fail(ServiceStatus.BadRequest, "invalid_request", "fromDate, toDate e scope são obrigatórios.");
        }

        if (!Enum.IsDefined(scope))
        {
            return ServiceResult<ReportEnvelope>.Fail(ServiceStatus.BadRequest, "invalid_scope", "scope deve ser Mine ou Global.");
        }

        if (fromDate > toDate)
        {
            return ServiceResult<ReportEnvelope>.Fail(ServiceStatus.BadRequest, "invalid_period", "fromDate deve ser menor ou igual a toDate.");
        }

        if (!ReportAccess.CanRequest(scope, user))
        {
            return ServiceResult<ReportEnvelope>.Fail(ServiceStatus.Forbidden, "scope_forbidden", "O perfil não permite solicitar este escopo.");
        }

        var report = new ReportRequest
        {
            Id = Guid.NewGuid(),
            RequesterId = user.UserId,
            Scope = scope,
            FromDate = fromDate,
            ToDate = toDate,
            Status = ReportStatus.Pending,
            CreatedAt = _timeProvider.GetUtcNow().UtcDateTime,
        };

        try
        {
            await _repository.AddAsync(report, cancellationToken);
        }
        catch (Exception ex) when (ex is DbUpdateException or DbException)
        {
            _logger.LogError("Report request not registered: database unavailable ({ErrorType})", ex.GetType().Name);
            return ServiceResult<ReportEnvelope>.Fail(ServiceStatus.Unavailable, "database_unavailable", "Não foi possível registrar a solicitação.");
        }

        ReportEnvelope envelope = ReportEnvelope.From(report);
        bool confirmed = await _publisher.TryPublishAsync(new ReportMessage(report.Id), cancellationToken);
        if (!confirmed)
        {
            _logger.LogWarning("Report {ReportId} persisted but publication was not confirmed by the broker", report.Id);
            return ServiceResult<ReportEnvelope>.FailWithValue(
                ServiceStatus.Unavailable,
                envelope,
                "publish_unconfirmed",
                "A solicitação foi registrada, mas o envio para processamento não foi confirmado.");
        }

        _logger.LogInformation("Report {ReportId} published (scope {Scope})", report.Id, report.Scope);
        return ServiceResult<ReportEnvelope>.Success(envelope, ServiceStatus.Accepted);
    }

    public async Task<IReadOnlyList<ReportEnvelope>> ListAsync(UserContext user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        IReadOnlyList<ReportRequest> reports = await _repository.ListOwnedAsync(user.UserId, cancellationToken);
        return [.. reports.Select(ReportEnvelope.From)];
    }

    public async Task<ServiceResult<ReportEnvelope>> GetAsync(Guid id, UserContext user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ReportRequest? report = await _repository.FindOwnedAsync(id, user.UserId, cancellationToken);
        return report is null
            ? NotFound<ReportEnvelope>()
            : ServiceResult<ReportEnvelope>.Success(ReportEnvelope.From(report));
    }

    public async Task<ServiceResult<byte[]>> ExportExcelAsync(Guid id, UserContext user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ReportRequest? report = await _repository.FindOwnedAsync(id, user.UserId, cancellationToken);
        if (report is null)
        {
            return NotFound<byte[]>();
        }

        if (report.Status != ReportStatus.Completed || report.ResultJson is null)
        {
            return ServiceResult<byte[]>.Fail(ServiceStatus.Conflict, "report_not_completed", "O relatório ainda não foi concluído com sucesso.");
        }

        ReportResult result = ReportJson.DeserializeResult(report.ResultJson);
        return ServiceResult<byte[]>.Success(ReportExcelBuilder.Build(report, result));
    }

    private static ServiceResult<T> NotFound<T>() =>
        ServiceResult<T>.Fail(ServiceStatus.NotFound, "report_not_found", "Relatório não encontrado.");
}

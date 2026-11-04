using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using ExpenseHub.Core.Domain;

namespace ExpenseHub.Core.Reports;

/// <summary>Escopo de dados do relatório.</summary>
internal enum ReportScope
{
    Mine = 0,
    Global = 1,
}

/// <summary>Estado de uma solicitação de relatório.</summary>
internal enum ReportStatus
{
    Pending = 0,
    Processing = 1,
    Completed = 2,
    Failed = 3,
}

/// <summary>Solicitação de relatório persistida. Solicitante e escopo nunca vêm da mensagem.</summary>
internal sealed class ReportRequest
{
    public Guid Id { get; set; }

    public string RequesterId { get; set; } = string.Empty;

    public ReportScope Scope { get; set; }

    public DateOnly FromDate { get; set; }

    public DateOnly ToDate { get; set; }

    public ReportStatus Status { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public string? ResultJson { get; set; }

    public string? ErrorCode { get; set; }

    public string? ErrorMessage { get; set; }

    public int Attempts { get; set; }
}

/// <summary>Quantidade e valor de um estado.</summary>
internal sealed record StatusSummary(ExpenseStatus Status, int Count, decimal Amount);

/// <summary>Resultado salvo do relatório.</summary>
internal sealed record ReportResult(DateTime GeneratedAt, int TotalCount, decimal TotalAmount, IReadOnlyList<StatusSummary> ByStatus);

/// <summary>Projeção mínima de um reembolso usada no cálculo.</summary>
internal readonly record struct ExpenseAmount(ExpenseStatus Status, decimal Amount);

/// <summary>Contrato mínimo da mensagem publicada no RabbitMQ.</summary>
internal sealed record ReportMessage(Guid ReportId);

/// <summary>Corpo de POST /api/reports.</summary>
internal sealed class CreateReportRequest
{
    [Required]
    public DateOnly? FromDate { get; init; }

    [Required]
    public DateOnly? ToDate { get; init; }

    [Required]
    public ReportScope? Scope { get; init; }
}

/// <summary>Erro sanitizado exposto no envelope.</summary>
internal sealed record ReportErrorDto(string Code, string Message);

/// <summary>Envelope de resposta da solicitação.</summary>
internal sealed record ReportEnvelope(
    string Id,
    ReportScope Scope,
    DateOnly FromDate,
    DateOnly ToDate,
    ReportStatus Status,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    ReportResult? Result,
    ReportErrorDto? Error)
{
    public static ReportEnvelope From(ReportRequest report)
    {
        ArgumentNullException.ThrowIfNull(report);
        ReportResult? result = report.Status == ReportStatus.Completed && report.ResultJson is not null
            ? ReportJson.DeserializeResult(report.ResultJson)
            : null;
        ReportErrorDto? error = report.Status == ReportStatus.Failed
            ? new ReportErrorDto(report.ErrorCode ?? "processing_failed", report.ErrorMessage ?? "Falha ao processar o relatório.")
            : null;

        return new ReportEnvelope(
            report.Id.ToString(),
            report.Scope,
            report.FromDate,
            report.ToDate,
            report.Status,
            report.CreatedAt,
            report.StartedAt,
            report.CompletedAt,
            result,
            error);
    }
}

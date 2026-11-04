using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ExpenseHub.Core.Reports;

/// <summary>Persistência usada pela aplicação web.</summary>
internal interface IReportRequestRepository
{
    Task AddAsync(ReportRequest report, CancellationToken cancellationToken);

    Task<ReportRequest?> FindOwnedAsync(Guid id, string requesterId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ReportRequest>> ListOwnedAsync(string requesterId, CancellationToken cancellationToken);
}

/// <summary>Publicação com confirmação do broker.</summary>
internal interface IReportPublisher
{
    /// <summary>Publica e aguarda confirmação. Retorna false quando não houve confirmação.</summary>
    Task<bool> TryPublishAsync(ReportMessage message, CancellationToken cancellationToken);
}

/// <summary>Persistência usada pelo worker. As escritas são condicionais para garantir idempotência.</summary>
internal interface IReportProcessingStore
{
    Task<ReportRequest?> FindAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Pending/Processing → Processing, incrementa tentativas. False se já finalizado.</summary>
    Task<bool> TryMarkProcessingAsync(Guid id, DateTime startedAt, CancellationToken cancellationToken);

    /// <summary>Grava resultado e Completed numa única operação, somente se ainda não finalizado.</summary>
    Task<bool> TryCompleteAsync(Guid id, string resultJson, DateTime completedAt, CancellationToken cancellationToken);

    /// <summary>Grava Failed com erro sanitizado, somente se ainda não finalizado.</summary>
    Task<bool> TryFailAsync(Guid id, string errorCode, string errorMessage, DateTime completedAt, CancellationToken cancellationToken);

    Task<IReadOnlyList<ExpenseAmount>> LoadAmountsAsync(ReportScope scope, string requesterId, DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken);
}

/// <summary>Configuração do processamento.</summary>
internal sealed class ReportProcessingOptions
{
    public int MaxAttempts { get; set; } = 3;

    public int TransientRetryDelaySeconds { get; set; } = 5;
}

/// <summary>Configuração do RabbitMQ compartilhada por web e worker.</summary>
internal sealed class RabbitMqOptions
{
    public string HostName { get; set; } = "localhost";

    public int Port { get; set; } = 5672;

    public string UserName { get; set; } = "guest";

    public string Password { get; set; } = string.Empty;

    public string QueueName { get; set; } = "expensehub.reports";

    public int PublishTimeoutSeconds { get; set; } = 10;
}

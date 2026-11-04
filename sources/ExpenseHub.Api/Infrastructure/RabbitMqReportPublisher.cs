using System;
using System.Threading;
using System.Threading.Tasks;
using ExpenseHub.Core.Reports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace ExpenseHub.Api.Infrastructure;

/// <summary>
/// Publica na fila durável, com mensagem persistente e publisher confirms.
/// Retorna true somente após o broker confirmar (ack); nack, retorno ou timeout retornam false.
/// </summary>
internal sealed class RabbitMqReportPublisher : IReportPublisher, IAsyncDisposable
{
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqReportPublisher> _logger;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private IConnection? _connection;

    public RabbitMqReportPublisher(IOptions<RabbitMqOptions> options, ILogger<RabbitMqReportPublisher> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _logger = logger;
    }

    public async Task<bool> TryPublishAsync(ReportMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.PublishTimeoutSeconds));

        try
        {
            IConnection connection = await GetConnectionAsync(timeout.Token);
            var channelOptions = new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true);
            await using IChannel channel = await connection.CreateChannelAsync(channelOptions, timeout.Token);
            await channel.QueueDeclareAsync(_options.QueueName, durable: true, exclusive: false, autoDelete: false, arguments: null, cancellationToken: timeout.Token);

            var properties = new BasicProperties
            {
                Persistent = true,
                ContentType = "application/json",
                MessageId = message.ReportId.ToString(),
                CorrelationId = message.ReportId.ToString(),
            };

            await channel.BasicPublishAsync(
                exchange: string.Empty,
                routingKey: _options.QueueName,
                mandatory: true,
                basicProperties: properties,
                body: ReportJson.SerializeMessage(message),
                cancellationToken: timeout.Token);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || timeout.IsCancellationRequested)
        {
            _logger.LogWarning("Report {ReportId} publication not confirmed ({ErrorType})", message.ReportId, ex.GetType().Name);
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        _connectionLock.Dispose();
    }

    private async Task<IConnection> GetConnectionAsync(CancellationToken cancellationToken)
    {
        if (_connection is { IsOpen: true })
        {
            return _connection;
        }

        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            if (_connection is { IsOpen: true })
            {
                return _connection;
            }

            if (_connection is not null)
            {
                await _connection.DisposeAsync();
            }

            var factory = new ConnectionFactory
            {
                HostName = _options.HostName,
                Port = _options.Port,
                UserName = _options.UserName,
                Password = _options.Password,
                ClientProvidedName = "expensehub-web",
            };
            _connection = await factory.CreateConnectionAsync(cancellationToken);
            return _connection;
        }
        finally
        {
            _connectionLock.Release();
        }
    }
}

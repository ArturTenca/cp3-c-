using System;
using System.Threading;
using System.Threading.Tasks;
using ExpenseHub.Core.Reports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace ExpenseHub.Worker;

/// <summary>
/// Consome a fila com ack manual. Ack somente depois que o efeito (Completed/Failed) está persistido
/// ou quando a mensagem é comprovadamente duplicada/inválida. Falha transitória: nack com requeue.
/// </summary>
internal sealed class ReportConsumerService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RabbitMqOptions _rabbit;
    private readonly ReportProcessingOptions _processing;
    private readonly ILogger<ReportConsumerService> _logger;
    private IConnection? _connection;
    private IChannel? _channel;

    public ReportConsumerService(
        IServiceScopeFactory scopeFactory,
        IOptions<RabbitMqOptions> rabbit,
        IOptions<ReportProcessingOptions> processing,
        ILogger<ReportConsumerService> logger)
    {
        ArgumentNullException.ThrowIfNull(rabbit);
        ArgumentNullException.ThrowIfNull(processing);
        _scopeFactory = scopeFactory;
        _rabbit = rabbit.Value;
        _processing = processing.Value;
        _logger = logger;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        if (_channel is not null)
        {
            await _channel.DisposeAsync();
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _connection = await ConnectWithRetryAsync(stoppingToken);
        IChannel channel = await _connection.CreateChannelAsync(cancellationToken: stoppingToken);
        _channel = channel;
        await channel.QueueDeclareAsync(_rabbit.QueueName, durable: true, exclusive: false, autoDelete: false, arguments: null, cancellationToken: stoppingToken);
        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false, cancellationToken: stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, delivery) => HandleAsync(channel, delivery, stoppingToken);
        await channel.BasicConsumeAsync(_rabbit.QueueName, autoAck: false, consumer: consumer, cancellationToken: stoppingToken);
        _logger.LogInformation(
            "Worker version {Version} consuming queue {Queue} (durable, manual ack, prefetch 1)",
            Environment.GetEnvironmentVariable("APP_VERSION") ?? "local",
            _rabbit.QueueName);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Worker stopping");
        }
    }

    private async Task HandleAsync(IChannel channel, BasicDeliverEventArgs delivery, CancellationToken stoppingToken)
    {
        if (!ReportJson.TryParseMessage(delivery.Body.Span, out Guid reportId))
        {
            _logger.LogWarning("Invalid message discarded (delivery tag {DeliveryTag})", delivery.DeliveryTag);
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, CancellationToken.None);
            return;
        }

        _logger.LogInformation("Report {ReportId} received (redelivered: {Redelivered})", reportId, delivery.Redelivered);

        ProcessingOutcome outcome;
        using (IServiceScope scope = _scopeFactory.CreateScope())
        {
            ReportProcessor processor = scope.ServiceProvider.GetRequiredService<ReportProcessor>();
            try
            {
                outcome = await processor.ProcessAsync(reportId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Sem ack: o broker reentrega a mensagem quando o worker voltar.
                return;
            }
        }

        if (outcome == ProcessingOutcome.Transient)
        {
            await Task.Delay(TimeSpan.FromSeconds(_processing.TransientRetryDelaySeconds), CancellationToken.None);
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, CancellationToken.None);
            _logger.LogWarning("Report {ReportId} requeued after transient failure", reportId);
            return;
        }

        await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, CancellationToken.None);
        _logger.LogInformation("Report {ReportId} acknowledged ({Outcome})", reportId, outcome);
    }

    private async Task<IConnection> ConnectWithRetryAsync(CancellationToken stoppingToken)
    {
        var factory = new ConnectionFactory
        {
            HostName = _rabbit.HostName,
            Port = _rabbit.Port,
            UserName = _rabbit.UserName,
            Password = _rabbit.Password,
            ClientProvidedName = "expensehub-worker",
            AutomaticRecoveryEnabled = true,
        };

        while (true)
        {
            try
            {
                return await factory.CreateConnectionAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("RabbitMQ unavailable ({ErrorType}); retrying in 5s", ex.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}

using System.Text;
using System.Text.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace barakoCMS.Features.WorkflowsV2.Queue;

/// <summary>
/// RabbitMQ-based workflow queue service.
/// </summary>
public class WorkflowQueueService : IWorkflowQueueService, IDisposable
{
    private readonly RabbitMQSettings _settings;
    private readonly ILogger<WorkflowQueueService> _logger;
    private IConnection? _connection;
    private IModel? _channel;
    private bool _disposed;

    public WorkflowQueueService(
        IOptions<RabbitMQSettings> settings,
        ILogger<WorkflowQueueService> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task PublishAsync(WorkflowQueueMessage message, CancellationToken ct = default)
    {
        await EnsureConnectionAsync();

        if (_channel == null)
        {
            _logger.LogWarning("RabbitMQ channel not available, workflow will not be queued");
            return;
        }

        var json = JsonSerializer.Serialize(message, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        var body = Encoding.UTF8.GetBytes(json);

        var properties = _channel.CreateBasicProperties();
        properties.Persistent = true;
        properties.MessageId = message.MessageId;
        properties.CorrelationId = message.CorrelationId;
        properties.Priority = (byte)Math.Min(message.Priority, 9);
        properties.Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        if (message.ScheduledFor.HasValue)
        {
            // Calculate delay for scheduled execution
            var delay = (long)(message.ScheduledFor.Value - DateTime.UtcNow).TotalMilliseconds;
            if (delay > 0)
            {
                properties.Headers = new Dictionary<string, object>
                {
                    ["x-delay"] = delay
                };

                _channel.BasicPublish(
                    exchange: _settings.ExchangeName,
                    routingKey: "delayed",
                    basicProperties: properties,
                    body: body);

                _logger.LogInformation("Published delayed workflow message {MessageId} for execution at {ScheduledFor}",
                    message.MessageId, message.ScheduledFor);

                return;
            }
        }

        _channel.BasicPublish(
            exchange: _settings.ExchangeName,
            routingKey: "execute",
            basicProperties: properties,
            body: body);

        _logger.LogInformation("Published workflow message {MessageId} for workflow {WorkflowId}",
            message.MessageId, message.WorkflowId);
    }

    public async Task PublishToDeadLetterAsync(WorkflowDeadLetterMessage message, CancellationToken ct = default)
    {
        await EnsureConnectionAsync();

        if (_channel == null)
            return;

        var json = JsonSerializer.Serialize(message, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        var body = Encoding.UTF8.GetBytes(json);

        var properties = _channel.CreateBasicProperties();
        properties.Persistent = true;
        properties.MessageId = Guid.NewGuid().ToString();

        _channel.BasicPublish(
            exchange: "",
            routingKey: _settings.DeadLetterQueueName,
            basicProperties: properties,
            body: body);

        _logger.LogWarning("Published message to dead letter queue: {MessageId}",
            message.OriginalMessage.MessageId);
    }

    public async Task<WorkflowQueueMessage?> ConsumeAsync(CancellationToken ct = default)
    {
        await EnsureConnectionAsync();

        if (_channel == null)
            return null;

        var result = _channel.BasicGet(_settings.QueueName, autoAck: false);

        if (result == null)
            return null;

        try
        {
            var json = Encoding.UTF8.GetString(result.Body.ToArray());
            var message = JsonSerializer.Deserialize<WorkflowQueueMessage>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (message != null)
            {
                message.Metadata["deliveryTag"] = result.DeliveryTag.ToString();
            }

            return message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deserialize queue message");
            _channel.BasicNack(result.DeliveryTag, multiple: false, requeue: false);
            return null;
        }
    }

    public Task AcknowledgeAsync(WorkflowQueueMessage message, CancellationToken ct = default)
    {
        if (_channel == null)
            return Task.CompletedTask;

        if (message.Metadata.TryGetValue("deliveryTag", out var tagStr) &&
            ulong.TryParse(tagStr, out var deliveryTag))
        {
            _channel.BasicAck(deliveryTag, multiple: false);
        }

        return Task.CompletedTask;
    }

    public Task RejectAsync(WorkflowQueueMessage message, bool requeue = false, CancellationToken ct = default)
    {
        if (_channel == null)
            return Task.CompletedTask;

        if (message.Metadata.TryGetValue("deliveryTag", out var tagStr) &&
            ulong.TryParse(tagStr, out var deliveryTag))
        {
            _channel.BasicNack(deliveryTag, multiple: false, requeue: requeue);
        }

        return Task.CompletedTask;
    }

    public Task<int> GetQueueLengthAsync(CancellationToken ct = default)
    {
        if (_channel == null)
            return Task.FromResult(0);

        var result = _channel.QueueDeclarePassive(_settings.QueueName);
        return Task.FromResult((int)result.MessageCount);
    }

    private async Task EnsureConnectionAsync()
    {
        if (_connection != null && _connection.IsOpen && _channel != null && _channel.IsOpen)
            return;

        try
        {
            var factory = new ConnectionFactory
            {
                HostName = _settings.HostName,
                Port = _settings.Port,
                UserName = _settings.UserName,
                Password = _settings.Password,
                VirtualHost = _settings.VirtualHost,
                AutomaticRecoveryEnabled = true,
                NetworkRecoveryInterval = TimeSpan.FromSeconds(10)
            };

            _connection = factory.CreateConnection();
            _channel = _connection.CreateModel();

            // Declare exchange
            _channel.ExchangeDeclare(
                exchange: _settings.ExchangeName,
                type: ExchangeType.Direct,
                durable: true);

            // Declare main queue
            _channel.QueueDeclare(
                queue: _settings.QueueName,
                durable: true,
                exclusive: false,
                autoDelete: false,
                arguments: new Dictionary<string, object>
                {
                    ["x-dead-letter-exchange"] = "",
                    ["x-dead-letter-routing-key"] = _settings.DeadLetterQueueName,
                    ["x-max-priority"] = 10
                });

            // Declare dead letter queue
            _channel.QueueDeclare(
                queue: _settings.DeadLetterQueueName,
                durable: true,
                exclusive: false,
                autoDelete: false);

            // Bind main queue
            _channel.QueueBind(
                queue: _settings.QueueName,
                exchange: _settings.ExchangeName,
                routingKey: "execute");

            _channel.BasicQos(0, (ushort)_settings.PrefetchCount, false);

            _logger.LogInformation("Connected to RabbitMQ at {Host}:{Port}",
                _settings.HostName, _settings.Port);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to RabbitMQ");
            throw;
        }

        await Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _channel?.Close();
        _channel?.Dispose();
        _connection?.Close();
        _connection?.Dispose();

        _disposed = true;
    }
}

/// <summary>
/// Interface for workflow queue operations.
/// </summary>
public interface IWorkflowQueueService
{
    /// <summary>
    /// Publish a workflow message to the queue.
    /// </summary>
    Task PublishAsync(WorkflowQueueMessage message, CancellationToken ct = default);

    /// <summary>
    /// Publish a message to the dead letter queue.
    /// </summary>
    Task PublishToDeadLetterAsync(WorkflowDeadLetterMessage message, CancellationToken ct = default);

    /// <summary>
    /// Consume a message from the queue.
    /// </summary>
    Task<WorkflowQueueMessage?> ConsumeAsync(CancellationToken ct = default);

    /// <summary>
    /// Acknowledge successful processing.
    /// </summary>
    Task AcknowledgeAsync(WorkflowQueueMessage message, CancellationToken ct = default);

    /// <summary>
    /// Reject a message.
    /// </summary>
    Task RejectAsync(WorkflowQueueMessage message, bool requeue = false, CancellationToken ct = default);

    /// <summary>
    /// Get the current queue length.
    /// </summary>
    Task<int> GetQueueLengthAsync(CancellationToken ct = default);
}

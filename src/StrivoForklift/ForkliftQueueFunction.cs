using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using StrivoForklift.Data;
using StrivoForklift.Models;

namespace StrivoForklift;

/// <summary>
/// Azure Function triggered by messages on the "consumethis" Azure Storage Queue.
/// Each message is a JSON payload with source, Id, and Message fields, e.g.:
///   {"source":"fake_bank_transactions_1000.csv","Id":"tx0001","Message":"..."}
/// A transaction GUID is generated at ingestion time and stored as the primary key.
/// </summary>
public class ForkliftQueueFunction
{
    private readonly ForkliftDbContext _dbContext;
    private readonly ILogger<ForkliftQueueFunction> _logger;

    public ForkliftQueueFunction(ForkliftDbContext dbContext, ILogger<ForkliftQueueFunction> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    [Function(nameof(ForkliftQueueFunction))]
    public async Task Run(
        [QueueTrigger("consumethis", Connection = "StorageQueue")] string rawMessage)
    {
        _logger.LogInformation("Dequeued raw message: {RawMessage}", rawMessage);

        QueueMessage? payload;
        try
        {
            payload = JsonSerializer.Deserialize<QueueMessage>(rawMessage);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize JSON payload: {Json}", rawMessage);
            return;
        }

        if (payload is null)
        {
            _logger.LogWarning("Deserialized JSON payload was null. Message skipped.");
            return;
        }

        var transactionId = Guid.NewGuid();
        _logger.LogInformation(
            "Dequeued message — TransactionId: {TransactionId}, AccountId: {AccountId}, Source: {Source}, Message: {Message}",
            transactionId, payload.Id, payload.Source, payload.Message);

        try
        {
            _dbContext.Transactions.Add(new Transaction
            {
                TransactionId = transactionId,
                AccountId = payload.Id,
                Source = payload.Source,
                Message = payload.Message,
                OriginalJson = rawMessage,
                InsertionTime = DateTime.UtcNow
            });
            await _dbContext.SaveChangesAsync();

            _logger.LogDebug(
                "Successfully wrote transaction to database. TransactionId: {TransactionId}, AccountId: {AccountId}",
                transactionId, payload.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to write transaction to database. TransactionId: {TransactionId}, AccountId: {AccountId}",
                transactionId, payload.Id);
            throw;
        }
    }
}

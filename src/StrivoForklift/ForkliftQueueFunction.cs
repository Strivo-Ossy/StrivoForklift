using System.Globalization;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using StrivoForklift.Data;
using StrivoForklift.Models;

namespace StrivoForklift;

/// <summary>
/// Azure Function triggered by messages on the "consumethis" Azure Storage Queue.
/// Each message is a three-line text payload:
///   Line 1 – transaction GUID (maps to transaction_id)
///   Line 2 – JSON body with source, Id, and Message fields
///   Line 3 – event timestamp (e.g. "3/17/2026, 12:42:55 PM")
/// Transactions are inserted on first receipt; duplicate GUIDs are silently skipped.
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
        var lines = rawMessage.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 3)
        {
            _logger.LogWarning(
                "Received malformed queue message; expected 3 lines but got {Count}. Message skipped.",
                lines.Length);
            return;
        }

        if (!Guid.TryParse(lines[0], out var transactionId))
        {
            _logger.LogWarning("Failed to parse transaction GUID from: {Line}", lines[0]);
            return;
        }

        var jsonLine = lines[1];
        QueueMessage? payload;
        try
        {
            payload = JsonSerializer.Deserialize<QueueMessage>(jsonLine);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize JSON payload: {Json}", jsonLine);
            return;
        }

        if (payload is null)
        {
            _logger.LogWarning("Deserialized JSON payload was null. Message skipped.");
            return;
        }

        DateTime? eventTs = null;
        // Expected timestamp format matches the queue message format: "M/d/yyyy, h:mm:ss tt" (e.g. "3/17/2026, 12:42:55 PM")
        if (DateTime.TryParseExact(lines[2], "M/d/yyyy, h:mm:ss tt",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedTs))
        {
            eventTs = parsedTs;
        }
        else
        {
            _logger.LogWarning("Could not parse event timestamp from: {Line}", lines[2]);
        }

        _logger.LogInformation(
            "Processing transaction Id: {TransactionId}, AccountId: {AccountId}",
            transactionId, payload.Id);

        var existing = await _dbContext.Transactions.FindAsync(transactionId);
        if (existing is not null)
        {
            _logger.LogInformation("Skipped duplicate transaction Id: {TransactionId}", transactionId);
            return;
        }

        _dbContext.Transactions.Add(new Transaction
        {
            TransactionId = transactionId,
            AccountId = payload.Id,
            Source = payload.Source,
            Message = payload.Message,
            EventTs = eventTs,
            OriginalJson = jsonLine,
            InsertionTime = DateTime.UtcNow
        });
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Inserted transaction Id: {TransactionId}", transactionId);
    }
}

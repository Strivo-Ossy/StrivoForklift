using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StrivoForklift.Data;
using Xunit;

namespace StrivoForklift.Tests;

/// <summary>
/// Minimal ILogger implementation that captures log entries so tests can assert on them.
/// </summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public readonly List<(LogLevel Level, string Message)> Entries = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
        => Entries.Add((logLevel, formatter(state, exception)));
}

public class ForkliftQueueFunctionTests
{
    /// <summary>Builds a raw queue message string in the JSON format used by the queue.</summary>
    private static string BuildRawMessage(string source, string accountId, string message)
        => $"{{\"source\":\"{source}\",\"Id\":\"{accountId}\",\"Message\":\"{message}\"}}";

    /// <summary>Creates a fresh in-memory EF Core database context for each test.</summary>
    private static ForkliftDbContext CreateInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<ForkliftDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ForkliftDbContext(options);
    }

    [Fact]
    public async Task Run_ValidMessage_LogsDequeueDetails()
    {
        // Arrange
        var logger = new CapturingLogger<ForkliftQueueFunction>();
        var function = new ForkliftQueueFunction(CreateInMemoryDbContext(), logger);
        var rawMessage = BuildRawMessage("fake_bank_transactions_1000.csv", "tx0001",
            "Direct debit SEK 97.77 (Internet subscription)");

        // Act
        await function.Run(rawMessage);

        // Assert – a dequeue log entry containing the account id must exist
        Assert.Contains(logger.Entries,
            e => e.Level == LogLevel.Information && e.Message.Contains("tx0001"));
        // No warnings should have been emitted for a well-formed message
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task Run_ValidMessage_WritesRecordToDatabase()
    {
        // Arrange
        var db = CreateInMemoryDbContext();
        var logger = new CapturingLogger<ForkliftQueueFunction>();
        var function = new ForkliftQueueFunction(db, logger);
        var rawMessage = BuildRawMessage("fake_bank_transactions_1000.csv", "tx0001",
            "Direct debit SEK 97.77 (Internet subscription)");

        // Act
        await function.Run(rawMessage);

        // Assert – exactly one transaction record inserted with correct fields
        var record = Assert.Single(db.Transactions.ToList());
        Assert.Equal("tx0001", record.AccountId);
        Assert.Equal("fake_bank_transactions_1000.csv", record.Source);
        Assert.Equal("Direct debit SEK 97.77 (Internet subscription)", record.Message);
        Assert.Equal(rawMessage, record.OriginalJson);
        // Debug log for successful write must have been emitted
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Debug);
    }

    [Fact]
    public async Task Run_MultipleDistinctMessages_EachLogged()
    {
        // Arrange
        var logger = new CapturingLogger<ForkliftQueueFunction>();
        var function = new ForkliftQueueFunction(CreateInMemoryDbContext(), logger);

        // Act
        await function.Run(BuildRawMessage("test.csv", "tx0001", "Payment A"));
        await function.Run(BuildRawMessage("test.csv", "tx0002", "Payment B"));

        // Assert – both account ids appear in log output
        Assert.Contains(logger.Entries,
            e => e.Level == LogLevel.Information && e.Message.Contains("tx0001"));
        Assert.Contains(logger.Entries,
            e => e.Level == LogLevel.Information && e.Message.Contains("tx0002"));
    }

    [Fact]
    public async Task Run_SameMessageTwice_BothLogged()
    {
        // Arrange – each invocation gets a unique TransactionId (GUID PK), so two rows are inserted
        var logger = new CapturingLogger<ForkliftQueueFunction>();
        var function = new ForkliftQueueFunction(CreateInMemoryDbContext(), logger);
        var rawMessage = BuildRawMessage("test.csv", "tx0001", "Payment A");

        // Act – send the same message twice
        await function.Run(rawMessage);
        await function.Run(rawMessage);

        // Assert – the dequeue detail log (the structured "Dequeued message —" line) appears once per invocation
        var detailLogs = logger.Entries
            .Where(e => e.Level == LogLevel.Information && e.Message.StartsWith("Dequeued message"))
            .ToList();
        Assert.Equal(2, detailLogs.Count);
    }

    [Fact]
    public async Task Run_MalformedMessage_LogsWarningAndSkips()
    {
        // Arrange
        var logger = new CapturingLogger<ForkliftQueueFunction>();
        var function = new ForkliftQueueFunction(CreateInMemoryDbContext(), logger);

        // Act – plain text, not valid JSON
        await function.Run("not-valid-json");

        // Assert – a warning is logged and no informational dequeue entry is present
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.DoesNotContain(logger.Entries,
            e => e.Level == LogLevel.Information && e.Message.Contains("Dequeued message"));
    }

    [Fact]
    public async Task Run_RealWorldMessage_LogsAccountIdAndSource()
    {
        // Arrange – message format matching actual queue output from the problem statement
        var logger = new CapturingLogger<ForkliftQueueFunction>();
        var function = new ForkliftQueueFunction(CreateInMemoryDbContext(), logger);
        const string rawMessage = "{\"source\":\"fake_bank_transactions_1000.csv\",\"Id\":\"tx0494\",\"Message\":\"Refund SEK 4773.50 from SJ\"}";

        // Act
        await function.Run(rawMessage);

        // Assert
        Assert.Contains(logger.Entries,
            e => e.Level == LogLevel.Information && e.Message.Contains("tx0494"));
        Assert.Contains(logger.Entries,
            e => e.Level == LogLevel.Information && e.Message.Contains("fake_bank_transactions_1000.csv"));
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }
}

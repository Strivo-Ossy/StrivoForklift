# StrivoForklift

Azure Function App that reads bank transaction events from an Azure Storage Queue and persists them in a database. Each message is inserted as a new transaction record; a GUID is generated at ingestion time as the primary key.

## Architecture

```
Azure Storage Queue ("consumethis" – consumeddata.queue.core.windows.net)
        │  (Managed Identity – Storage Queue Data Message Processor)
        ▼
ForkliftQueueFunction  (Azure Functions v4 – .NET 8 isolated worker)
        │  (Managed Identity – Azure SQL db_datareader + db_datawriter)
        ▼
ForkliftDbContext  (Entity Framework Core – Azure SQL Server)
        │
        ▼
transaction_ingester  (ingestdemo.database.windows.net)
```

## Queue Message Format

Messages on the `consumethis` queue are plain UTF-8 text. Azure Storage Queue does not impose a schema — messages can be any string up to 64 KB. When the Azure Functions SDK binds a queue message to a strongly-typed parameter (`QueueMessage`), it automatically deserializes the text as JSON using `System.Text.Json`. Messages must therefore be JSON-encoded with the following shape:

```json
{
  "source": "fake_bank_transactions_1000.csv",
  "Id": "tx0001",
  "Message": "Direct debit SEK 97.77 (Internet subscription)"
}
```

| Field      | Type             | Description                                                  |
|------------|------------------|--------------------------------------------------------------|
| `source`   | string (optional)| Source file or system that originated the transaction        |
| `Id`       | string (optional)| Account identifier for the transaction (e.g. `tx0001`)      |
| `Message`  | string (optional)| Human-readable transaction description                       |

> **Note:** If the queue may contain non-JSON messages, handle the `JsonException` in the function and route invalid messages to the poison queue (Azure Functions does this automatically after 5 failed delivery attempts).

## Database Model

Each dequeued message is inserted into the `dbo.transactions` table with a freshly generated GUID as the primary key.

| Column           | Type           | Description                                              |
|------------------|----------------|----------------------------------------------------------|
| `TransactionId`  | GUID (PK)      | Unique identifier generated at ingestion time            |
| `AccountId`      | string (≤100)  | Account identifier from `$.Id` in the queue message      |
| `Source`         | string (≤255)  | Source file/system from `$.source` in the queue message  |
| `Message`        | string         | Transaction description from `$.Message`                 |
| `EventTs`        | datetime?      | Nullable; not populated by the current source — reserved for a future timestamp field |
| `OriginalJson`   | string         | The raw JSON payload as received from the queue          |
| `InsertionTime`  | datetime       | UTC timestamp of when the record was inserted            |

## Project Structure

```
StrivoForklift.sln
src/
  StrivoForklift/
    ForkliftQueueFunction.cs   # Queue-triggered Azure Function
    Program.cs                 # Host / DI configuration
    host.json
    appsettings.json           # Production config (no secrets – managed identity)
    local.settings.json        # Local dev settings (not published)
    Models/
      QueueMessage.cs          # Deserialized queue payload
      ForkliftEvent.cs         # Database entity
    Data/
      ForkliftDbContext.cs     # EF Core DB context
tests/
  StrivoForklift.Tests/
    ForkliftQueueFunctionTests.cs  # xUnit tests (in-memory DB)
```

## Local Development

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8)
- [Azure Functions Core Tools v4](https://learn.microsoft.com/azure/azure-functions/functions-run-local)
- [Azurite](https://learn.microsoft.com/azure/storage/common/storage-use-azurite) (Storage emulator)
- Access to the Azure subscription (for `Authentication=Active Directory Default` in the SQL connection)

### Run locally

```bash
# Start Azurite (Storage emulator – used for AzureWebJobsStorage host internals)
azurite --silent &

# Start the Function App
cd src/StrivoForklift
func start
```

`local.settings.json` is pre-configured with:
- `AzureWebJobsStorage` pointing at Azurite for the Functions host internals.
- `StorageQueue__serviceUri` pointing at the real `consumeddata` queue service, so the trigger connects via your local developer identity (`Authentication=Active Directory Default`).
- `SqlConnection` pointing at the real Azure SQL server using `Authentication=Active Directory Default`, so EF Core authenticates with your Azure CLI / Visual Studio credential.

### Run tests

```bash
dotnet test
```

---

## Azure Configuration Guide

The Function App uses a **system-assigned managed identity** for all service connections — no secrets, SAS tokens, or passwords are stored in configuration.

### Required Application Settings

Configure the following in the Function App's **Application Settings** (or equivalent environment variables):

| Setting | Value | Notes |
|---------|-------|-------|
| `FUNCTIONS_WORKER_RUNTIME` | `dotnet-isolated` | Set automatically by Azure when deploying a .NET isolated worker app |
| `AzureWebJobsStorage__accountName` | `consumeddata` | Tells the Functions host to use managed identity for its internal storage (leases, state). For production workloads consider a **dedicated storage account** for host internals to keep permissions separate from application queues. |
| `StorageQueue__serviceUri` | `https://consumeddata.queue.core.windows.net` | Queue service endpoint for the `consumethis` trigger. The runtime uses the managed identity automatically when a `__serviceUri` (or `__accountName`) suffix is present instead of a full connection string. |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | *(from Azure Portal → Application Insights resource → Connection String)* | Enables live metrics, distributed traces, and structured logging. Highly recommended for production. |

> **`ConnectionStrings:SqlConnection`** is provided via `appsettings.json` and does **not** need to be set as an Application Setting. It uses `Authentication=Active Directory Managed Identity` so no password is required. Override this setting only if the server name or database name changes.

### Required RBAC Roles

#### Azure Storage Account (`consumeddata`)

Grant these roles to the Function App's managed identity on the storage account:

| Role | Purpose |
|------|---------|
| `Storage Blob Data Contributor` | Functions host uses Blob Storage for distributed lease management |
| `Storage Queue Data Contributor` | Functions host uses Queue Storage internally — it both reads and **writes** to internal queues (including poison queues for failed messages) |
| `Storage Table Data Contributor` | Functions host uses Table Storage for state management |
| `Storage Queue Data Message Processor` | Allows the `consumethis` queue trigger to read, peek, and delete messages |

> All four roles can be granted at the storage account scope. Alternatively, scope `Storage Queue Data Message Processor` to the specific queue (`consumethis`) for least-privilege access.

#### Azure SQL Database (`transaction_ingester` on `ingestdemo.database.windows.net`)

The managed identity uses `Authentication=Active Directory Managed Identity`, so it must be added as a contained database user in the SQL database. Run these T-SQL statements as a SQL admin or Azure AD admin:

```sql
-- Replace <ManagedIdentityName> with the Function App's name (the managed identity display name)
CREATE USER [<ManagedIdentityName>] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader ADD MEMBER [<ManagedIdentityName>];
ALTER ROLE db_datawriter ADD MEMBER [<ManagedIdentityName>];
```

No additional Azure RBAC role assignment is needed for Azure SQL — access is controlled entirely via the contained database user above.

### Summary of All Connections

| Connection | Resource | Auth Method |
|------------|----------|-------------|
| `AzureWebJobsStorage` | `consumeddata` storage account | Managed identity (`__accountName`) |
| `StorageQueue` (queue trigger) | `consumeddata` / `consumethis` queue | Managed identity (`__serviceUri`) |
| `SqlConnection` (EF Core) | `transaction_ingester` on `ingestdemo` | Managed identity (`Active Directory Managed Identity` in connection string) |
| Application Insights | Monitoring resource | Connection string key (non-secret) via `APPLICATIONINSIGHTS_CONNECTION_STRING` |


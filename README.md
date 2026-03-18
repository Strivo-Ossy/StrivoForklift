# StrivoForklift

Azure Function App that reads bank transaction events from an Azure Storage Queue and persists them in a database. Each message is inserted as a new transaction record; a GUID is generated at ingestion time as the primary key.

## Architecture

```
Azure Storage Queue ("consumethis" – consumeddata.queue.core.windows.net)
        │  (Managed Identity – Storage Queue Data Message Processor)
        ▼
ForkliftQueueFunction  (Azure Functions v4 – .NET 8 isolated worker)
        │  (Managed Identity – Key Vault Secrets User on kv-bear)
        ▼
Azure Key Vault  (kv-bear.vault.azure.net – sql-db-username / sql-db-password)
        │
        ▼
ForkliftDbContext  (Entity Framework Core – Azure SQL Server, SQL auth)
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

| Column             | Type           | Description                                              |
|--------------------|----------------|----------------------------------------------------------|
| `transaction_id`   | GUID (PK)      | Unique identifier generated at ingestion time            |
| `account_id`       | string (≤100)  | Account identifier from `$.Id` in the queue message      |
| `Source`           | string (≤255)  | Source file/system from `$.source` in the queue message  |
| `Message`          | string         | Transaction description from `$.Message`                 |
| `event_ts`         | datetime?      | Nullable; not populated by the current source — reserved for a future timestamp field |
| `original_json`    | string         | The raw JSON payload as received from the queue          |
| `insertion_time`   | datetime       | UTC timestamp of when the record was inserted            |

## Project Structure

```
StrivoForklift.sln
src/
  StrivoForklift/
    ForkliftQueueFunction.cs   # Queue-triggered Azure Function
    Program.cs                 # Host / DI configuration
    host.json
    appsettings.json           # Production config (KeyVault URI + secret names, SQL server info)
    local.settings.json        # Local dev settings (not published)
    Models/
      QueueMessage.cs          # Deserialized queue payload
      ForkliftEvent.cs         # Database entity
    Data/
      ForkliftDbContext.cs     # EF Core DB context
tests/
  StrivoForklift.Tests/
    ForkliftQueueFunctionTests.cs  # xUnit tests (in-memory DB)
    HostStartupTests.cs            # xUnit tests for startup validation
```

## Local Development

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8)
- [Azure Functions Core Tools v4](https://learn.microsoft.com/azure/azure-functions/functions-run-local)
- [Azurite](https://learn.microsoft.com/azure/storage/common/storage-use-azurite) (Storage emulator)
- Access to the Azure subscription with permissions to read secrets from `kv-bear` (for Key Vault access)

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
- `StorageQueue__serviceUri` pointing at the real `consumeddata` queue service, so the trigger connects via your local developer identity (`DefaultAzureCredential` → Azure CLI / Visual Studio credential).
- `KeyVault__Uri`, `KeyVault__DbUsernameSecretName`, `KeyVault__DbPasswordSecretName` pointing at `kv-bear` with the expected secret names. `DefaultAzureCredential` uses your local developer credential to authenticate to Key Vault.
- `SqlServer__Server` and `SqlServer__Database` for the target SQL instance. Credentials are fetched at startup from Key Vault.

### Run tests

```bash
dotnet test
```

---

## Azure Configuration Guide

The Function App uses a **system-assigned managed identity** for all Azure service connections — no secrets, SAS tokens, or passwords are stored in configuration files or Application Settings.

Database credentials (username and password) are stored exclusively in **Azure Key Vault** (`kv-bear`) and fetched at startup by the Function App using its managed identity.

### Azure Key Vault (`kv-bear`)

Key Vault URI: `https://kv-bear.vault.azure.net/`

The following secrets must exist in `kv-bear`:

| Secret Name         | Description                                       |
|---------------------|---------------------------------------------------|
| `sql-db-username`   | SQL Server login username for `transaction_ingester` |
| `sql-db-password`   | SQL Server login password for `transaction_ingester` |

> The expected secret names are configured via `KeyVault:DbUsernameSecretName` and `KeyVault:DbPasswordSecretName` in `appsettings.json` (defaults: `sql-db-username` and `sql-db-password`). Override these Application Settings if you use different secret names in your vault.

At startup, `Program.cs` reads these two secrets using `SecretClient` (from `Azure.Security.KeyVault.Secrets`) and constructs the SQL Server connection string dynamically. The connection string is **never stored in configuration files or Application Settings**.

### Required Application Settings

Configure the following in the Function App's **Application Settings** (or equivalent environment variables):

| Setting | Value | Notes |
|---------|-------|-------|
| `FUNCTIONS_WORKER_RUNTIME` | `dotnet-isolated` | Set automatically by Azure when deploying a .NET isolated worker app |
| `AzureWebJobsStorage__accountName` | `consumeddata` | Tells the Functions host to use managed identity for its internal storage (leases, state). For production workloads consider a **dedicated storage account** for host internals to keep permissions separate from application queues. |
| `StorageQueue__serviceUri` | `https://consumeddata.queue.core.windows.net` | Queue service endpoint for the `consumethis` trigger. The runtime uses the managed identity automatically when a `__serviceUri` (or `__accountName`) suffix is present instead of a full connection string. |
| `KeyVault__Uri` | `https://kv-bear.vault.azure.net/` | URI of the Azure Key Vault that holds the database credentials. Configured in `appsettings.json`; override only if the vault changes. |
| `KeyVault__DbUsernameSecretName` | `sql-db-username` | Name of the Key Vault secret holding the database username. Configured in `appsettings.json`; override only if you use a different secret name. |
| `KeyVault__DbPasswordSecretName` | `sql-db-password` | Name of the Key Vault secret holding the database password. Configured in `appsettings.json`; override only if you use a different secret name. |
| `SqlServer__Server` | `tcp:ingestdemo.database.windows.net,1433` | SQL Server host and port. Configured in `appsettings.json`; override only if the server changes. |
| `SqlServer__Database` | `transaction_ingester` | SQL database name. Configured in `appsettings.json`; override only if the database changes. |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | *(from Azure Portal → Application Insights resource → Connection String)* | Enables live metrics, distributed traces, and structured logging. Highly recommended for production. |

> **`ConnectionStrings:SqlConnection`** is **no longer used**. The SQL connection string is assembled at startup from `SqlServer:Server`, `SqlServer:Database`, and the credentials retrieved from Key Vault.

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

#### Azure Key Vault (`kv-bear`)

Grant this role to the Function App's managed identity on the Key Vault:

| Role | Purpose |
|------|---------|
| `Key Vault Secrets User` | Allows the Function App to read (get) secrets — specifically `sql-db-username` and `sql-db-password` |

> Scope the role assignment to the Key Vault resource (`kv-bear`). No broader subscription-level role is needed.

#### Azure SQL Database (`transaction_ingester` on `ingestdemo.database.windows.net`)

Database access uses SQL Server authentication with the username and password retrieved from Key Vault. The SQL login must exist on the SQL Server and the user must be added to the database with appropriate permissions:

```sql
-- Run as SQL admin; replace <username> with the value of the sql-db-username secret in kv-bear
CREATE LOGIN [<username>] WITH PASSWORD = '<password>';
USE transaction_ingester;
CREATE USER [<username>] FOR LOGIN [<username>];
ALTER ROLE db_datareader ADD MEMBER [<username>];
ALTER ROLE db_datawriter ADD MEMBER [<username>];
```

### Summary of All Connections

| Connection | Resource | Auth Method |
|------------|----------|-------------|
| `AzureWebJobsStorage` | `consumeddata` storage account | Managed identity (`__accountName`) |
| `StorageQueue` (queue trigger) | `consumeddata` / `consumethis` queue | Managed identity (`__serviceUri`) |
| Key Vault (`kv-bear`) | `sql-db-username`, `sql-db-password` secrets | Managed identity (`Key Vault Secrets User`) |
| SQL Server (EF Core) | `transaction_ingester` on `ingestdemo` | SQL auth — credentials from Key Vault |
| Application Insights | Monitoring resource | Connection string key (non-secret) via `APPLICATIONINSIGHTS_CONNECTION_STRING` |


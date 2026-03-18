using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StrivoForklift.Data;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) =>
    {
        // Fail fast if the queue trigger connection is not configured.
        // When this setting is absent the QueueTrigger binding silently stops polling,
        // leaving the queue full and the function apparently live but never triggered.
        // NOTE: .NET's EnvironmentVariablesConfigurationProvider translates '__' to ':' in
        // configuration keys, so the environment variable 'StorageQueue__serviceUri' is
        // accessible as 'StorageQueue:serviceUri' via IConfiguration.
        var storageQueueServiceUri = context.Configuration["StorageQueue:serviceUri"]
            ?? throw new InvalidOperationException(
                "Missing required app setting 'StorageQueue__serviceUri' " +
                "(set as the environment variable 'StorageQueue__serviceUri', " +
                "which IConfiguration exposes as 'StorageQueue:serviceUri'). " +
                "Set this to the Azure Queue Storage service URI " +
                "(e.g. https://<account>.queue.core.windows.net). " +
                "The Managed Identity must also hold the " +
                "'Storage Queue Data Message Processor' role on the storage account.");

        // Validate that the URI is the storage-account-level endpoint only.
        // A common misconfiguration is appending the queue name to the URI
        // (e.g. https://<account>.queue.core.windows.net/consumethis) — the queue
        // name must NOT appear here; it is already declared in the QueueTrigger attribute.
        if (Uri.TryCreate(storageQueueServiceUri, UriKind.Absolute, out var parsedUri)
            && parsedUri.AbsolutePath.Trim('/').Length > 0)
        {
            throw new InvalidOperationException(
                $"The app setting 'StorageQueue__serviceUri' has an unexpected path component " +
                $"('{parsedUri.AbsolutePath}'). " +
                $"This setting must be the storage-account-level queue service endpoint with no path " +
                $"(e.g. https://<account>.queue.core.windows.net). " +
                $"Remove the queue name from the URI — it is already declared in the QueueTrigger attribute.");
        }

        var connectionString = context.Configuration.GetConnectionString("SqlConnection")
            ?? throw new InvalidOperationException(
                "A 'SqlConnection' connection string must be provided in configuration.");

        services.AddDbContext<ForkliftDbContext>(options =>
            options.UseSqlServer(connectionString));
    })
    .Build();

host.Run();

using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Azure.Monitor.Query;
using Azure.Identity;
using System;
using Company.Function;
using Company.Function.Services;

Console.WriteLine("====== AZURE DDNS FUNCTION APP STARTING ======");
Console.WriteLine($"Start Time: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} UTC");
Console.WriteLine($"Environment: {Environment.GetEnvironmentVariable("AZURE_FUNCTIONS_ENVIRONMENT") ?? "Development"}");

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication(app =>
    {
        // Add custom middleware to fix header handling in .NET 8 Isolated Worker
        app.UseMiddleware<ForwardedForHeaderMiddleware>();
    })
    .ConfigureServices(services =>
    {
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();
        
        // Add custom services for DDNS functionality
        services.AddSingleton<TableStorageService>();
        services.AddScoped<ApiKeyService>();
        services.AddScoped<TelemetryHelper>();
        
        // Add Azure Monitor Query client for monitoring validation
        services.AddSingleton(provider => new LogsQueryClient(new DefaultAzureCredential()));
    })
    .ConfigureLogging((context, logging) =>
    {
        // Ensure console logging is enabled
        logging.AddConsole();
        
        // Set minimum log level
        logging.SetMinimumLevel(LogLevel.Information);
        
        // Add filter for our namespace to ensure all logs are captured
        logging.AddFilter("Company.Function", LogLevel.Debug);
        
        Console.WriteLine("Logging configuration completed");
    })
    .Build();

Console.WriteLine("Host built successfully, starting runtime...");
Console.WriteLine("====== AZURE DDNS FUNCTION APP READY ======");

host.Run();

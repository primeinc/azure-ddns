using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;

Console.WriteLine("====== AZURE DDNS FUNCTION APP STARTING ======");
Console.WriteLine($"Start Time: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} UTC");
Console.WriteLine($"Environment: {Environment.GetEnvironmentVariable("AZURE_FUNCTIONS_ENVIRONMENT") ?? "Development"}");

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
    .ConfigureServices(services =>
    {
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();
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

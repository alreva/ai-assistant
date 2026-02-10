using DevicePairingFunction.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

// Ensure environment variables are loaded
builder.Configuration.AddEnvironmentVariables();

// Register services
builder.Services.AddSingleton<ITableStorageService, TableStorageService>();
builder.Services.AddSingleton<IOAuthService, OAuthService>();
builder.Services.AddSingleton<IEncryptionService, EncryptionService>();
builder.Services.AddHttpClient();

builder.Build().Run();

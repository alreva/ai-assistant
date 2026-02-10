using System.Text.RegularExpressions;
using DevicePairingFunction.Models;
using DevicePairingFunction.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace DevicePairingFunction.Functions;

public partial class RegisterDevice
{
    private readonly ITableStorageService _tableStorage;
    private readonly IEncryptionService _encryptionService;
    private readonly ILogger<RegisterDevice> _logger;

    public RegisterDevice(
        ITableStorageService tableStorage,
        IEncryptionService encryptionService,
        ILogger<RegisterDevice> logger)
    {
        _tableStorage = tableStorage;
        _encryptionService = encryptionService;
        _logger = logger;
    }

    [Function("RegisterDevice")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "device/register")]
        HttpRequest req)
    {
        DeviceRegisterRequest? request;
        try
        {
            request = await req.ReadFromJsonAsync<DeviceRegisterRequest>();
        }
        catch
        {
            return new BadRequestObjectResult(new { error = "Invalid request body" });
        }

        if (request == null || string.IsNullOrEmpty(request.DeviceId) || string.IsNullOrEmpty(request.DeviceSecret))
        {
            return new BadRequestObjectResult(new { error = "Missing deviceId or deviceSecret" });
        }

        // Validate device ID format
        if (!IsValidDeviceId(request.DeviceId))
        {
            return new BadRequestObjectResult(new { error = "Invalid device ID format. Use alphanumeric characters and hyphens, 3-50 characters." });
        }

        // Validate device secret strength
        if (request.DeviceSecret.Length < 32)
        {
            return new BadRequestObjectResult(new { error = "Device secret must be at least 32 characters" });
        }

        // Check if device already exists
        var existingDevice = await _tableStorage.GetDeviceAsync(request.DeviceId);
        if (existingDevice != null)
        {
            _logger.LogWarning("Device already registered: {DeviceId}", request.DeviceId);
            return new ConflictObjectResult(new { error = "Device already registered" });
        }

        // Create device
        var device = new DeviceEntity
        {
            RowKey = request.DeviceId,
            DeviceSecretHash = _encryptionService.HashDeviceSecret(request.DeviceSecret),
            CreatedAt = DateTime.UtcNow
        };

        await _tableStorage.CreateDeviceAsync(device);

        _logger.LogInformation("Device registered: {DeviceId}", request.DeviceId);

        return new OkObjectResult(new { success = true, message = "Device registered successfully" });
    }

    private static bool IsValidDeviceId(string deviceId)
    {
        return !string.IsNullOrWhiteSpace(deviceId)
            && deviceId.Length >= 3
            && deviceId.Length <= 50
            && DeviceIdPattern().IsMatch(deviceId);
    }

    [GeneratedRegex("^[a-zA-Z0-9-]+$")]
    private static partial Regex DeviceIdPattern();
}

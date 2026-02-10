using System.Text.RegularExpressions;
using DevicePairingFunction.Models;
using DevicePairingFunction.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace DevicePairingFunction.Functions;

public partial class StartPairing
{
    private readonly ITableStorageService _tableStorage;
    private readonly IOAuthService _oAuthService;
    private readonly ILogger<StartPairing> _logger;

    public StartPairing(
        ITableStorageService tableStorage,
        IOAuthService oAuthService,
        ILogger<StartPairing> logger)
    {
        _tableStorage = tableStorage;
        _oAuthService = oAuthService;
        _logger = logger;
    }

    [Function("StartDevicePairing")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "device/{deviceId}/pair")]
        HttpRequest req,
        string deviceId)
    {
        _logger.LogInformation("Starting pairing for device: {DeviceId}", deviceId);

        // Validate device ID format (alphanumeric with hyphens, 3-50 chars)
        if (!IsValidDeviceId(deviceId))
        {
            _logger.LogWarning("Invalid device ID format: {DeviceId}", deviceId);
            return new BadRequestObjectResult(new { error = "Invalid device ID format" });
        }

        // Check if device is registered
        var device = await _tableStorage.GetDeviceAsync(deviceId);
        if (device == null)
        {
            _logger.LogWarning("Device not registered: {DeviceId}", deviceId);
            return new NotFoundObjectResult(new { error = "Device not registered. Run setup-device.sh first." });
        }

        // Generate PKCE challenge
        var codeVerifier = _oAuthService.GenerateCodeVerifier();
        var codeChallenge = _oAuthService.GenerateCodeChallenge(codeVerifier);

        // Create short-lived pairing session (5 min)
        var sessionId = Guid.NewGuid().ToString("N")[..12];
        var session = new PairingSessionEntity
        {
            RowKey = sessionId,
            DeviceId = deviceId,
            CodeVerifier = codeVerifier,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5)
        };

        await _tableStorage.CreatePairingSessionAsync(session);

        // Build authorization URL and redirect
        var authUrl = _oAuthService.BuildAuthorizationUrl(sessionId, codeChallenge);

        _logger.LogInformation("Redirecting to OAuth for session {SessionId}", sessionId);
        return new RedirectResult(authUrl);
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

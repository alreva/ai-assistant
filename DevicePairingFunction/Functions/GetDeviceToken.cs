using DevicePairingFunction.Models;
using DevicePairingFunction.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace DevicePairingFunction.Functions;

public class GetDeviceToken
{
    private readonly ITableStorageService _tableStorage;
    private readonly IOAuthService _oAuthService;
    private readonly IEncryptionService _encryptionService;
    private readonly ILogger<GetDeviceToken> _logger;

    public GetDeviceToken(
        ITableStorageService tableStorage,
        IOAuthService oAuthService,
        IEncryptionService encryptionService,
        ILogger<GetDeviceToken> logger)
    {
        _tableStorage = tableStorage;
        _oAuthService = oAuthService;
        _encryptionService = encryptionService;
        _logger = logger;
    }

    [Function("GetDeviceToken")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "device/token")]
        HttpRequest req)
    {
        DeviceTokenRequest? request;
        try
        {
            request = await req.ReadFromJsonAsync<DeviceTokenRequest>();
        }
        catch
        {
            return new BadRequestObjectResult(new { error = "Invalid request body" });
        }

        if (request == null || string.IsNullOrEmpty(request.DeviceId) || string.IsNullOrEmpty(request.DeviceSecret))
        {
            return new BadRequestObjectResult(new { error = "Missing deviceId or deviceSecret" });
        }

        // Get device
        var device = await _tableStorage.GetDeviceAsync(request.DeviceId);
        if (device == null)
        {
            _logger.LogWarning("Device not found: {DeviceId}", request.DeviceId);
            return new UnauthorizedObjectResult(new { error = "Invalid device credentials" });
        }

        // Validate device secret
        if (!_encryptionService.VerifyDeviceSecret(request.DeviceSecret, device.DeviceSecretHash))
        {
            _logger.LogWarning("Invalid device secret for device: {DeviceId}", request.DeviceId);
            return new UnauthorizedObjectResult(new { error = "Invalid device credentials" });
        }

        // Check if device is linked to a user
        if (string.IsNullOrEmpty(device.EncryptedRefreshToken))
        {
            _logger.LogWarning("Device not linked to user: {DeviceId}", request.DeviceId);
            return new StatusCodeResult(403); // Forbidden - device exists but not linked
        }

        // Decrypt refresh token
        string refreshToken;
        try
        {
            refreshToken = _encryptionService.Decrypt(device.EncryptedRefreshToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to decrypt refresh token for device: {DeviceId}", request.DeviceId);
            return new StatusCodeResult(500);
        }

        // Get fresh access token
        try
        {
            var tokens = await _oAuthService.RefreshTokenAsync(refreshToken);

            // If refresh token was rotated, update stored token
            if (!string.IsNullOrEmpty(tokens.RefreshToken) && tokens.RefreshToken != refreshToken)
            {
                device.EncryptedRefreshToken = _encryptionService.Encrypt(tokens.RefreshToken);
                await _tableStorage.UpdateDeviceAsync(device);
                _logger.LogInformation("Rotated refresh token for device: {DeviceId}", request.DeviceId);
            }

            _logger.LogInformation("Issued access token for device: {DeviceId}", request.DeviceId);

            return new OkObjectResult(new DeviceTokenResponse
            {
                AccessToken = tokens.AccessToken,
                ExpiresIn = tokens.ExpiresIn
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Token refresh failed for device: {DeviceId}", request.DeviceId);

            // If refresh fails, the user needs to re-pair
            return new StatusCodeResult(401); // Unauthorized - need to re-pair
        }
    }
}

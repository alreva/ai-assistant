using System.Text.Json;
using DevicePairingFunction.Models;
using DevicePairingFunction.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace DevicePairingFunction.Functions;

public class ApprovePairing
{
    private readonly ITableStorageService _tableStorage;
    private readonly IEncryptionService _encryptionService;
    private readonly ILogger<ApprovePairing> _logger;

    public ApprovePairing(
        ITableStorageService tableStorage,
        IEncryptionService encryptionService,
        ILogger<ApprovePairing> logger)
    {
        _tableStorage = tableStorage;
        _encryptionService = encryptionService;
        _logger = logger;
    }

    [Function("ApprovePairing")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "approve")]
        HttpRequest req)
    {
        var form = await req.ReadFormAsync();
        var sessionId = form["sessionId"].ToString();

        if (string.IsNullOrEmpty(sessionId))
        {
            return new BadRequestObjectResult(new { error = "Missing sessionId" });
        }

        // Get pairing session
        var session = await _tableStorage.GetPairingSessionAsync(sessionId);
        if (session == null)
        {
            _logger.LogWarning("Pairing session not found: {SessionId}", sessionId);
            return new NotFoundObjectResult(new { error = "Pairing session not found" });
        }

        if (session.IsExpired)
        {
            _logger.LogWarning("Pairing session expired: {SessionId}", sessionId);
            return new ContentResult
            {
                ContentType = "text/html",
                StatusCode = 400,
                Content = HtmlGenerator.GenerateErrorPage("Session Expired", "The pairing session has expired. Please scan the QR code again.")
            };
        }

        if (session.Status != "authenticated")
        {
            _logger.LogWarning("Session not authenticated: {SessionId}, status: {Status}", sessionId, session.Status);
            return new BadRequestObjectResult(new { error = "Session not authenticated" });
        }

        if (string.IsNullOrEmpty(session.EncryptedTokens) || string.IsNullOrEmpty(session.UserId))
        {
            _logger.LogError("Session missing tokens or userId: {SessionId}", sessionId);
            return new ContentResult
            {
                ContentType = "text/html",
                StatusCode = 500,
                Content = HtmlGenerator.GenerateErrorPage("Error", "Session data is incomplete. Please try again.")
            };
        }

        // Decrypt tokens to get refresh token
        var tokensJson = _encryptionService.Decrypt(session.EncryptedTokens);
        var tokens = JsonSerializer.Deserialize<TokenResponse>(tokensJson);

        if (tokens?.RefreshToken == null)
        {
            _logger.LogError("No refresh token in session: {SessionId}", sessionId);
            return new ContentResult
            {
                ContentType = "text/html",
                StatusCode = 500,
                Content = HtmlGenerator.GenerateErrorPage("Error", "No refresh token received. Ensure 'offline_access' scope is configured.")
            };
        }

        // Get or create device entity
        var device = await _tableStorage.GetDeviceAsync(session.DeviceId);
        if (device == null)
        {
            _logger.LogError("Device not found: {DeviceId}", session.DeviceId);
            return new NotFoundObjectResult(new { error = "Device not registered" });
        }

        // Update device with user link
        device.UserId = session.UserId;
        device.EncryptedRefreshToken = _encryptionService.Encrypt(tokens.RefreshToken);
        device.LinkedAt = DateTime.UtcNow;
        await _tableStorage.UpdateDeviceAsync(device);

        // Mark pairing as approved
        session.Status = "approved";
        session.EncryptedTokens = null; // Clear tokens from session
        await _tableStorage.UpdatePairingSessionAsync(session);

        _logger.LogInformation("Device {DeviceId} linked to user {UserId}", session.DeviceId, session.UserId);

        return new ContentResult
        {
            ContentType = "text/html",
            Content = HtmlGenerator.GenerateSuccessPage(session.DeviceId)
        };
    }
}

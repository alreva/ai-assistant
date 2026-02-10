using System.Text;
using System.Text.Json;
using DevicePairingFunction.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DevicePairingFunction.Functions;

public class OAuthCallback
{
    private readonly ITableStorageService _tableStorage;
    private readonly IOAuthService _oAuthService;
    private readonly IEncryptionService _encryptionService;
    private readonly string _baseUrl;
    private readonly ILogger<OAuthCallback> _logger;

    public OAuthCallback(
        ITableStorageService tableStorage,
        IOAuthService oAuthService,
        IEncryptionService encryptionService,
        IConfiguration configuration,
        ILogger<OAuthCallback> logger)
    {
        _tableStorage = tableStorage;
        _oAuthService = oAuthService;
        _encryptionService = encryptionService;
        _baseUrl = configuration["BaseUrl"] ?? throw new InvalidOperationException("BaseUrl not configured");
        _logger = logger;
    }

    [Function("OAuthCallback")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "callback")]
        HttpRequest req)
    {
        var code = req.Query["code"].ToString();
        var state = req.Query["state"].ToString();
        var error = req.Query["error"].ToString();
        var errorDescription = req.Query["error_description"].ToString();

        // Handle OAuth errors
        if (!string.IsNullOrEmpty(error))
        {
            _logger.LogError("OAuth error: {Error} - {Description}", error, errorDescription);
            return new ContentResult
            {
                ContentType = "text/html",
                StatusCode = 400,
                Content = HtmlGenerator.GenerateErrorPage(error, errorDescription)
            };
        }

        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
        {
            _logger.LogError("Missing code or state in callback");
            return new BadRequestObjectResult(new { error = "Missing code or state" });
        }

        // Decode state to get session ID
        string sessionId;
        try
        {
            var stateJson = Encoding.UTF8.GetString(Base64UrlDecode(state));
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var stateData = JsonSerializer.Deserialize<StateData>(stateJson, options);
            sessionId = stateData?.SessionId ?? throw new InvalidOperationException("Missing sessionId in state");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to decode state");
            return new BadRequestObjectResult(new { error = "Invalid state" });
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

        // Exchange code for tokens
        try
        {
            var tokens = await _oAuthService.ExchangeCodeForTokensAsync(code, session.CodeVerifier);
            var userId = _oAuthService.GetUserIdFromToken(tokens.AccessToken);

            if (string.IsNullOrEmpty(userId))
            {
                _logger.LogError("Could not extract user ID from token");
                return new ContentResult
                {
                    ContentType = "text/html",
                    StatusCode = 500,
                    Content = HtmlGenerator.GenerateErrorPage("Authentication Error", "Could not identify user from token.")
                };
            }

            // Store encrypted tokens in session
            var tokensJson = JsonSerializer.Serialize(tokens);
            session.EncryptedTokens = _encryptionService.Encrypt(tokensJson);
            session.UserId = userId;
            session.Status = "authenticated";
            await _tableStorage.UpdatePairingSessionAsync(session);

            _logger.LogInformation("User {UserId} authenticated for session {SessionId}", userId, sessionId);

            // Show approval page
            return new ContentResult
            {
                ContentType = "text/html",
                Content = HtmlGenerator.GenerateApprovalPage(session.DeviceId, sessionId, _baseUrl)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Token exchange failed");
            return new ContentResult
            {
                ContentType = "text/html",
                StatusCode = 500,
                Content = HtmlGenerator.GenerateErrorPage("Authentication Failed", "Could not complete authentication. Please try again.")
            };
        }
    }

    private static byte[] Base64UrlDecode(string input)
    {
        var output = input.Replace('-', '+').Replace('_', '/');
        switch (output.Length % 4)
        {
            case 2: output += "=="; break;
            case 3: output += "="; break;
        }
        return Convert.FromBase64String(output);
    }

    private record StateData(string SessionId);
}

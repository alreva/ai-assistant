using DevicePairingFunction.Models;
using DevicePairingFunction.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace DevicePairingFunction.Functions;

public class GetPairingStatus
{
    private readonly ITableStorageService _tableStorage;
    private readonly ILogger<GetPairingStatus> _logger;

    public GetPairingStatus(ITableStorageService tableStorage, ILogger<GetPairingStatus> logger)
    {
        _tableStorage = tableStorage;
        _logger = logger;
    }

    [Function("GetPairingStatus")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "device/pairing/{sessionId}/status")]
        HttpRequest req,
        string sessionId)
    {
        var session = await _tableStorage.GetPairingSessionAsync(sessionId);

        if (session == null)
        {
            return new NotFoundObjectResult(new PairingStatusResponse { Status = "not_found" });
        }

        var status = session.IsExpired ? "expired" : session.Status;

        _logger.LogInformation("Pairing status for session {SessionId}: {Status}", sessionId, status);

        return new OkObjectResult(new PairingStatusResponse { Status = status });
    }
}

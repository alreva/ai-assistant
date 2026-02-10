using DevicePairingFunction.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace DevicePairingFunction.Functions;

public class GetDeviceStatus
{
    private readonly ITableStorageService _tableStorage;
    private readonly ILogger<GetDeviceStatus> _logger;

    public GetDeviceStatus(ITableStorageService tableStorage, ILogger<GetDeviceStatus> logger)
    {
        _tableStorage = tableStorage;
        _logger = logger;
    }

    [Function("GetDeviceStatus")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "device/{deviceId}/status")]
        HttpRequest req,
        string deviceId)
    {
        var device = await _tableStorage.GetDeviceAsync(deviceId);

        if (device == null)
        {
            return new NotFoundObjectResult(new { status = "not_registered" });
        }

        var isLinked = !string.IsNullOrEmpty(device.EncryptedRefreshToken);

        return new OkObjectResult(new
        {
            status = isLinked ? "linked" : "registered",
            deviceId = device.RowKey,
            linkedAt = device.LinkedAt,
            createdAt = device.CreatedAt
        });
    }
}

using Azure;
using Azure.Data.Tables;
using DevicePairingFunction.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DevicePairingFunction.Services;

public class TableStorageService : ITableStorageService
{
    private readonly TableClient _sessionsTable;
    private readonly TableClient _devicesTable;
    private readonly ILogger<TableStorageService> _logger;

    public TableStorageService(IConfiguration configuration, ILogger<TableStorageService> logger)
    {
        _logger = logger;
        var connectionString = configuration["TableStorageConnection"]
            ?? throw new InvalidOperationException("TableStorageConnection not configured");

        var serviceClient = new TableServiceClient(connectionString);

        _sessionsTable = serviceClient.GetTableClient("PairingSessions");
        _sessionsTable.CreateIfNotExists();

        _devicesTable = serviceClient.GetTableClient("Devices");
        _devicesTable.CreateIfNotExists();

        _logger.LogInformation("Table storage initialized");
    }

    public async Task<PairingSessionEntity?> GetPairingSessionAsync(string sessionId)
    {
        try
        {
            var response = await _sessionsTable.GetEntityAsync<PairingSessionEntity>("session", sessionId);
            return response.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task CreatePairingSessionAsync(PairingSessionEntity session)
    {
        await _sessionsTable.AddEntityAsync(session);
        _logger.LogInformation("Created pairing session {SessionId} for device {DeviceId}",
            session.RowKey, session.DeviceId);
    }

    public async Task UpdatePairingSessionAsync(PairingSessionEntity session)
    {
        await _sessionsTable.UpdateEntityAsync(session, session.ETag, TableUpdateMode.Replace);
        _logger.LogInformation("Updated pairing session {SessionId}", session.RowKey);
    }

    public async Task DeletePairingSessionAsync(string sessionId)
    {
        await _sessionsTable.DeleteEntityAsync("session", sessionId);
        _logger.LogInformation("Deleted pairing session {SessionId}", sessionId);
    }

    public async Task<DeviceEntity?> GetDeviceAsync(string deviceId)
    {
        try
        {
            var response = await _devicesTable.GetEntityAsync<DeviceEntity>("device", deviceId);
            return response.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task CreateDeviceAsync(DeviceEntity device)
    {
        await _devicesTable.AddEntityAsync(device);
        _logger.LogInformation("Created device {DeviceId}", device.RowKey);
    }

    public async Task UpdateDeviceAsync(DeviceEntity device)
    {
        await _devicesTable.UpdateEntityAsync(device, device.ETag, TableUpdateMode.Replace);
        _logger.LogInformation("Updated device {DeviceId}", device.RowKey);
    }

    public async Task<bool> DeviceExistsAsync(string deviceId)
    {
        var device = await GetDeviceAsync(deviceId);
        return device != null;
    }
}

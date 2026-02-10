namespace DevicePairingFunction.Services;

public static class HtmlGenerator
{
    private const string CommonStyles = @"
        body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
               display: flex; justify-content: center; align-items: center; min-height: 100vh;
               margin: 0; background: #f5f5f5; }
        .container { background: white; padding: 2rem; border-radius: 12px;
                      box-shadow: 0 4px 6px rgba(0,0,0,0.1); max-width: 400px; text-align: center; }
        .device-name { font-family: monospace; background: #f0f0f0; padding: 0.5rem 1rem;
                        border-radius: 6px; margin: 1rem 0; font-size: 1.2rem; }
        .btn { background: #0078d4; color: white; border: none; padding: 1rem 2rem;
                border-radius: 8px; font-size: 1rem; cursor: pointer; width: 100%;
                transition: background 0.2s; }
        .btn:hover { background: #106ebe; }
        .btn-cancel { background: #6c757d; margin-top: 0.5rem; }
        .btn-cancel:hover { background: #5a6268; }
        .icon { font-size: 3rem; margin-bottom: 1rem; }
        .success-icon { font-size: 4rem; margin-bottom: 1rem; }
    ";

    public static string GenerateApprovalPage(string deviceId, string sessionId, string baseUrl)
    {
        var encodedDeviceId = System.Net.WebUtility.HtmlEncode(deviceId);
        return $@"<!DOCTYPE html>
<html>
<head>
    <meta charset=""utf-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1"">
    <title>Link Device</title>
    <style>
        {CommonStyles}
        h1 {{ margin-top: 0; color: #333; }}
    </style>
</head>
<body>
    <div class=""container"">
        <h1>Link Device?</h1>
        <p>Do you want to link this device to your account?</p>
        <div class=""device-name"">{encodedDeviceId}</div>
        <form method=""post"" action=""{baseUrl}/api/approve"">
            <input type=""hidden"" name=""sessionId"" value=""{sessionId}"">
            <button type=""submit"" class=""btn"">Approve</button>
        </form>
        <button onclick=""window.close()"" class=""btn btn-cancel"">Cancel</button>
    </div>
</body>
</html>";
    }

    public static string GenerateSuccessPage(string deviceId)
    {
        var encodedDeviceId = System.Net.WebUtility.HtmlEncode(deviceId);
        return $@"<!DOCTYPE html>
<html>
<head>
    <meta charset=""utf-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1"">
    <title>Device Linked</title>
    <style>
        {CommonStyles}
        h1 {{ margin-top: 0; color: #107c10; }}
    </style>
</head>
<body>
    <div class=""container"">
        <div class=""success-icon"">&#10004;</div>
        <h1>Device Linked!</h1>
        <p>Your device has been successfully linked to your account.</p>
        <div class=""device-name"">{encodedDeviceId}</div>
        <p>You can close this window.</p>
    </div>
</body>
</html>";
    }

    public static string GenerateErrorPage(string error, string description)
    {
        var encodedError = System.Net.WebUtility.HtmlEncode(error);
        var encodedDescription = System.Net.WebUtility.HtmlEncode(description);
        return $@"<!DOCTYPE html>
<html>
<head>
    <meta charset=""utf-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1"">
    <title>Error</title>
    <style>
        {CommonStyles}
        h1 {{ margin-top: 0; color: #d93025; }}
    </style>
</head>
<body>
    <div class=""container"">
        <div class=""icon"">&#10060;</div>
        <h1>{encodedError}</h1>
        <p>{encodedDescription}</p>
    </div>
</body>
</html>";
    }
}

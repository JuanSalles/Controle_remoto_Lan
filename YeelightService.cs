using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

internal sealed class DeviceService
{
    private const string MulticastIp = "239.255.255.250";
    private const int MulticastPort = 1982;
    private const int DefaultDevicePort = 55443;
    private const int EsphomePort = 6053;

    private readonly HttpClient _httpClient = new();
    private readonly AppConfig _config;

    public DeviceService(AppConfig config)
    {
        _config = config;
    }

    public async Task<List<DeviceInfo>> DiscoverDevicesAsync()
    {
        List<DeviceInfo> devices = await DiscoverYeelightByMulticastAsync(TimeSpan.FromSeconds(2));

        List<string> baseIps = GetLocalBaseIps();
        if (baseIps.Count == 0)
        {
            return devices;
        }

        foreach (string baseIp in baseIps)
        {
            List<DeviceInfo> found = _config.ScanOtherDevices
                ? await ScanByIpAsync(baseIp, timeoutMs: 300, maxConcurrency: 48)
                : await ScanYeelightOnlyByIpAsync(baseIp, timeoutMs: 300, maxConcurrency: 48);
            foreach (DeviceInfo foundDevice in found)
            {
                if (devices.All(existing => existing.UniqueKey != foundDevice.UniqueKey))
                {
                    devices.Add(foundDevice);
                }
            }
        }

        return devices;
    }

    public async Task SetPowerAsync(DeviceInfo device, bool isOn)
    {
        switch (device.Kind)
        {
            case DeviceKind.Yeelight:
                await SendYeelightPowerAsync(device, isOn);
                return;
            case DeviceKind.Tasmota:
                await SendTasmotaPowerAsync(device, isOn);
                return;
            default:
                throw new InvalidOperationException("Dispositivo nao suporta controle ON/OFF.");
        }
    }

    public async Task<bool?> GetPowerStateAsync(DeviceInfo device)
    {
        switch (device.Kind)
        {
            case DeviceKind.Yeelight:
                return await GetYeelightPowerAsync(device);
            case DeviceKind.Tasmota:
                return await GetTasmotaPowerAsync(device);
            default:
                return null;
        }
    }

    private async Task<bool?> GetYeelightPowerAsync(DeviceInfo device)
    {
        var payload = new
        {
            id = 2,
            method = "get_prop",
            @params = new object[] { "power" }
        };

        string json = JsonSerializer.Serialize(payload) + "\r\n";

        using var client = new TcpClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        await client.ConnectAsync(device.Ip, device.Port, cts.Token);
        using NetworkStream stream = client.GetStream();

        byte[] data = Encoding.UTF8.GetBytes(json);
        await stream.WriteAsync(data, cts.Token);

        byte[] buffer = new byte[512];
        int read = await stream.ReadAsync(buffer, cts.Token);
        if (read <= 0)
        {
            return null;
        }

        string response = Encoding.UTF8.GetString(buffer, 0, read);
        return TryParseYeelightPower(response);
    }

    private async Task<bool?> GetTasmotaPowerAsync(DeviceInfo device)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        string url = $"http://{device.Ip}/cm?cmnd=Power";

        HttpResponseMessage response = await _httpClient.GetAsync(url, cts.Token);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        string payload = await response.Content.ReadAsStringAsync(cts.Token);
        return TryParseTasmotaPower(payload);
    }

    private async Task SendYeelightPowerAsync(DeviceInfo device, bool isOn)
    {
        var payload = new
        {
            id = 1,
            method = "set_power",
            @params = new object[] { isOn ? "on" : "off", "smooth", 500 }
        };

        string json = JsonSerializer.Serialize(payload);
        string message = json + "\r\n";

        using var client = new TcpClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        await client.ConnectAsync(device.Ip, device.Port, cts.Token);
        using NetworkStream stream = client.GetStream();

        byte[] data = Encoding.UTF8.GetBytes(message);
        await stream.WriteAsync(data, cts.Token);

        byte[] buffer = new byte[1024];
        try
        {
            int read = await stream.ReadAsync(buffer, cts.Token);
            if (read > 0)
            {
                string response = Encoding.UTF8.GetString(buffer, 0, read).Trim();
                if (!string.IsNullOrWhiteSpace(response))
                {
                    if (TryGetCommandResponse(response, expectedId: 1, out bool isError, out string? errorMessage))
                    {
                        if (isError)
                        {
                            throw new InvalidOperationException(errorMessage ?? "Erro retornado pelo dispositivo.");
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SendTasmotaPowerAsync(DeviceInfo device, bool isOn)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        string command = isOn ? "On" : "Off";
        string url = $"http://{device.Ip}/cm?cmnd=Power%20{command}";

        HttpResponseMessage response = await _httpClient.GetAsync(url, cts.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("Falha ao enviar comando para Tasmota.");
        }

        string payload = await response.Content.ReadAsStringAsync(cts.Token);
        if (payload.IndexOf("Not Authorized", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            throw new InvalidOperationException("Tasmota requer autenticacao.");
        }
    }

    private static async Task<List<DeviceInfo>> DiscoverYeelightByMulticastAsync(TimeSpan timeout)
    {
        var devices = new List<DeviceInfo>();
        var message = "M-SEARCH * HTTP/1.1\r\n" +
                      $"HOST:{MulticastIp}:{MulticastPort}\r\n" +
                      "MAN:\"ssdp:discover\"\r\n" +
                      "ST:wifi_bulb\r\n" +
                      "\r\n";

        using var client = new UdpClient(0);
        client.EnableBroadcast = true;
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        byte[] payload = Encoding.ASCII.GetBytes(message);
        await client.SendAsync(payload, payload.Length, new IPEndPoint(IPAddress.Parse(MulticastIp), MulticastPort));

        DateTime deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            TimeSpan remaining = deadline - DateTime.UtcNow;
            Task<UdpReceiveResult> receiveTask = client.ReceiveAsync();
            Task delayTask = Task.Delay(remaining);

            Task completed = await Task.WhenAny(receiveTask, delayTask);
            if (completed != receiveTask)
            {
                break;
            }

            UdpReceiveResult result = receiveTask.Result;
            string response = Encoding.ASCII.GetString(result.Buffer);
            DeviceInfo? device = ParseYeelightResponse(response);

            if (device == null)
            {
                continue;
            }

            if (devices.Any(existing => existing.UniqueKey == device.UniqueKey))
            {
                continue;
            }

            devices.Add(device);
        }

        return devices;
    }

    private static string DecodeName(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return string.Empty;
        }

        try
        {
            byte[] data = Convert.FromBase64String(rawName);
            string decoded = Encoding.UTF8.GetString(data);
            return string.IsNullOrWhiteSpace(decoded) ? rawName : decoded;
        }
        catch
        {
            return rawName;
        }
    }

    private static DeviceInfo? ParseYeelightResponse(string response)
    {
        string[] lines = response.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            return null;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines)
        {
            int idx = line.IndexOf(':');
            if (idx <= 0)
            {
                continue;
            }

            string key = line.Substring(0, idx).Trim();
            string value = line.Substring(idx + 1).Trim();
            if (!string.IsNullOrWhiteSpace(key))
            {
                headers[key] = value;
            }
        }

        if (!headers.TryGetValue("Location", out string? location) || string.IsNullOrWhiteSpace(location))
        {
            return null;
        }

        if (!location.StartsWith("yeelight://", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string uriString = "tcp://" + location.Substring("yeelight://".Length);
        if (!Uri.TryCreate(uriString, UriKind.Absolute, out Uri? uri))
        {
            return null;
        }

        if (!IPAddress.TryParse(uri.Host, out IPAddress? ip))
        {
            return null;
        }

        int port = uri.Port > 0 ? uri.Port : DefaultDevicePort;

        headers.TryGetValue("id", out string? id);
        headers.TryGetValue("model", out string? model);
        headers.TryGetValue("name", out string? rawName);

        string name = DecodeName(rawName);
        string safeId = string.IsNullOrWhiteSpace(id) ? $"{ip}:{port}" : id;

        return new DeviceInfo(safeId, DeviceKind.Yeelight, model ?? string.Empty, name, ip, port, true);
    }

    private static List<string> GetLocalBaseIps()
    {
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            IPInterfaceProperties props = ni.GetIPProperties();
            foreach (UnicastIPAddressInformation addr in props.UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork)
                {
                    continue;
                }

                if (IPAddress.IsLoopback(addr.Address))
                {
                    continue;
                }

                if (IsApipa(addr.Address))
                {
                    continue;
                }

                string? baseIp = ExtractBaseIp(addr.Address);
                if (!string.IsNullOrWhiteSpace(baseIp))
                {
                    results.Add(baseIp);
                }
            }
        }

        return results.OrderBy(value => value).ToList();
    }

    private static string? ExtractBaseIp(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }

        byte[] bytes = address.GetAddressBytes();
        if (bytes.Length != 4)
        {
            return null;
        }

        return $"{bytes[0]}.{bytes[1]}.{bytes[2]}.";
    }

    private static bool IsApipa(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        byte[] bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
    }

    private async Task<List<DeviceInfo>> ScanByIpAsync(string baseIp, int timeoutMs, int maxConcurrency)
    {
        var devices = new List<DeviceInfo>();
        using var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        var tasks = new List<Task>();
        var lockObject = new object();

        for (int i = 1; i <= 254; i++)
        {
            string ipString = baseIp + i;
            tasks.Add(Task.Run(async () =>
            {
                await semaphore.WaitAsync();
                try
                {
                    List<DeviceInfo> found = await DetectDevicesOnIpAsync(ipString, timeoutMs);
                    if (found.Count == 0)
                    {
                        return;
                    }

                    lock (lockObject)
                    {
                        devices.AddRange(found);
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            }));
        }

        await Task.WhenAll(tasks);
        return devices.OrderBy(d => d.Ip.ToString()).ThenBy(d => d.Kind.ToString()).ToList();
    }

    private async Task<List<DeviceInfo>> ScanYeelightOnlyByIpAsync(string baseIp, int timeoutMs, int maxConcurrency)
    {
        var devices = new List<DeviceInfo>();
        using var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        var tasks = new List<Task>();
        var lockObject = new object();

        for (int i = 1; i <= 254; i++)
        {
            string ipString = baseIp + i;
            tasks.Add(Task.Run(async () =>
            {
                await semaphore.WaitAsync();
                try
                {
                    if (await TryIdentifyYeelightAsync(ipString, DefaultDevicePort, timeoutMs))
                    {
                        if (IPAddress.TryParse(ipString, out IPAddress? ip))
                        {
                            var device = new DeviceInfo($"{ip}:{DefaultDevicePort}", DeviceKind.Yeelight, "", "Yeelight confirmada", ip, DefaultDevicePort, true);
                            lock (lockObject)
                            {
                                devices.Add(device);
                            }
                        }
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            }));
        }

        await Task.WhenAll(tasks);
        return devices.OrderBy(d => d.Ip.ToString()).ThenBy(d => d.Kind.ToString()).ToList();
    }

    private async Task<List<DeviceInfo>> DetectDevicesOnIpAsync(string ipString, int timeoutMs)
    {
        var devices = new List<DeviceInfo>();

        if (await TryIdentifyYeelightAsync(ipString, DefaultDevicePort, timeoutMs))
        {
            if (IPAddress.TryParse(ipString, out IPAddress? ip))
            {
                devices.Add(new DeviceInfo($"{ip}:{DefaultDevicePort}", DeviceKind.Yeelight, "", "Yeelight confirmada", ip, DefaultDevicePort, true));
            }
        }

        (bool isTasmota, string? tasmotaName) = await TryIdentifyTasmotaAsync(ipString, timeoutMs);
        if (isTasmota)
        {
            if (IPAddress.TryParse(ipString, out IPAddress? ip))
            {
                string name = string.IsNullOrWhiteSpace(tasmotaName) ? "Tasmota" : tasmotaName;
                devices.Add(new DeviceInfo($"{ip}:80", DeviceKind.Tasmota, "Tasmota", name, ip, 80, true));
            }
        }

        if (await TryIdentifyEsphomeAsync(ipString, timeoutMs))
        {
            if (IPAddress.TryParse(ipString, out IPAddress? ip))
            {
                devices.Add(new DeviceInfo($"{ip}:{EsphomePort}", DeviceKind.Esphome, "ESPHome", "ESPHome (porta 6053)", ip, EsphomePort, false));
            }
        }

        return devices;
    }

    private static async Task<bool> TryIdentifyYeelightAsync(string ip, int port, int timeoutMs)
    {
        using var client = new TcpClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));

        try
        {
            await client.ConnectAsync(ip, port, cts.Token);
            if (!client.Connected)
            {
                return false;
            }

            using NetworkStream stream = client.GetStream();

            var payload = new
            {
                id = 99,
                method = "get_prop",
                @params = new object[] { "power" }
            };

            string json = JsonSerializer.Serialize(payload) + "\r\n";
            byte[] data = Encoding.UTF8.GetBytes(json);
            await stream.WriteAsync(data, cts.Token);

            byte[] buffer = new byte[512];
            int read = await stream.ReadAsync(buffer, cts.Token);
            if (read <= 0)
            {
                return false;
            }

            string response = Encoding.UTF8.GetString(buffer, 0, read);
            return IsValidYeelightResponse(response);
        }
        catch
        {
            return false;
        }
    }

    private async Task<(bool IsTasmota, string? FriendlyName)> TryIdentifyTasmotaAsync(string ip, int timeoutMs)
    {
        string? friendlyName = null;

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));
        string url = $"http://{ip}/cm?cmnd=Status%200";

        try
        {
            HttpResponseMessage response = await _httpClient.GetAsync(url, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return (false, friendlyName);
            }

            string json = await response.Content.ReadAsStringAsync(cts.Token);
            if (TryParseTasmotaFriendlyName(json, out string? parsedName))
            {
                friendlyName = parsedName;
                return (true, friendlyName);
            }
        }
        catch
        {
        }

        return (false, friendlyName);
    }

    private static bool TryParseTasmotaFriendlyName(string json, out string? name)
    {
        name = null;
        int jsonStart = json.IndexOf('{');
        if (jsonStart < 0)
        {
            return false;
        }

        string payload = json[jsonStart..].Trim();
        try
        {
            using JsonDocument doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("Status", out JsonElement status) &&
                status.TryGetProperty("FriendlyName", out JsonElement friendly))
            {
                if (friendly.ValueKind == JsonValueKind.Array && friendly.GetArrayLength() > 0)
                {
                    name = friendly[0].GetString();
                }
                else if (friendly.ValueKind == JsonValueKind.String)
                {
                    name = friendly.GetString();
                }
            }

            if (string.IsNullOrWhiteSpace(name) &&
                doc.RootElement.TryGetProperty("StatusNET", out _))
            {
                name = "Tasmota";
            }

            return !string.IsNullOrWhiteSpace(name);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> TryIdentifyEsphomeAsync(string ip, int timeoutMs)
    {
        using var client = new TcpClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));

        try
        {
            await client.ConnectAsync(ip, EsphomePort, cts.Token);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsValidYeelightResponse(string response)
    {
        int jsonStart = response.IndexOf('{');
        if (jsonStart < 0)
        {
            return false;
        }

        string json = response[jsonStart..].Trim();
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("id", out _) &&
                (doc.RootElement.TryGetProperty("result", out _) || doc.RootElement.TryGetProperty("error", out _)))
            {
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool? TryParseYeelightPower(string response)
    {
        foreach (string line in response.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            int jsonStart = line.IndexOf('{');
            if (jsonStart < 0)
            {
                continue;
            }

            string json = line[jsonStart..].Trim();
            try
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("result", out JsonElement result) &&
                    result.ValueKind == JsonValueKind.Array &&
                    result.GetArrayLength() > 0)
                {
                    string? value = result[0].GetString();
                    if (string.Equals(value, "on", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    if (string.Equals(value, "off", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }
            }
            catch
            {
            }
        }

        return null;
    }

    private static bool? TryParseTasmotaPower(string json)
    {
        int jsonStart = json.IndexOf('{');
        if (jsonStart < 0)
        {
            return null;
        }

        string payload = json[jsonStart..].Trim();
        try
        {
            using JsonDocument doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("POWER", out JsonElement power))
            {
                string? value = power.GetString();
                if (string.Equals(value, "ON", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (string.Equals(value, "OFF", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static bool TryGetCommandResponse(string response, int expectedId, out bool isError, out string? errorMessage)
    {
        isError = false;
        errorMessage = null;

        foreach (string line in response.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            int jsonStart = line.IndexOf('{');
            if (jsonStart < 0)
            {
                continue;
            }

            string json = line[jsonStart..].Trim();
            try
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("id", out JsonElement idElement) &&
                    idElement.ValueKind == JsonValueKind.Number &&
                    idElement.GetInt32() == expectedId)
                {
                    if (doc.RootElement.TryGetProperty("error", out JsonElement errorElement))
                    {
                        isError = true;
                        errorMessage = errorElement.ToString();
                        return true;
                    }

                    if (doc.RootElement.TryGetProperty("result", out _))
                    {
                        return true;
                    }
                }
            }
            catch
            {
            }
        }

        return false;
    }
}

internal enum DeviceKind
{
    Yeelight,
    Tasmota,
    Esphome
}

internal sealed record DeviceInfo(string Id, DeviceKind Kind, string Model, string Name, IPAddress Ip, int Port, bool SupportsPowerControl)
{
    public string UniqueKey => string.IsNullOrWhiteSpace(Id) ? $"{Kind}:{Ip}:{Port}" : Id;

    public string ToDisplayString()
    {
        string label = string.IsNullOrWhiteSpace(Name) ? Model : Name;
        if (string.IsNullOrWhiteSpace(label))
        {
            label = "(sem nome)";
        }

        return $"{label} - {Kind} [{Ip}:{Port}]";
    }
}

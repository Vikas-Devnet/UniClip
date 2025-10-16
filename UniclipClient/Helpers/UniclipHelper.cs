using System.Net;

namespace UniclipClient.Helpers
{
    internal static class UniclipHelper
    {
        internal static async Task<string> DiscoverServer(CancellationTokenSource cts, string? serverIp = null)
        {
            if (string.IsNullOrEmpty(serverIp))
            {
                try
                {
                    var machineName = Dns.GetHostName();
                    var uri = new Uri($"http://{machineName}:5000/serverinfo");
                    using var http = new HttpClient();
                    var response = await http.GetAsync(uri, cts.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync();
                        var ip = System.Text.Json.JsonDocument.Parse(json)
                            .RootElement.GetProperty("ip").GetString();
                        return $"ws://{ip}:5000/ws";
                    }
                }
                catch (Exception) { }
            }
            else
            {
                return $"ws://{serverIp}:5000/ws";
            }
            throw new Exception("Server not found automatically");
        }

        internal static T ConvertBytesTo<T>(byte[] bytes) where T : class
        {
            using var ms = new MemoryStream(bytes);

            if (typeof(T) == typeof(Image))
                return (T)(object)Image.FromStream(ms);

            if (typeof(T) == typeof(Icon))
                return (T)(object)new Icon(ms);

            throw new NotSupportedException($"Type '{typeof(T).Name}' is not supported.");
        }
    }
}

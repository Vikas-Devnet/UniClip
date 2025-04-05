using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// Add CORS services
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

// Use CORS middleware
app.UseCors("AllowAll");
app.UseWebSockets();

// Server state
var clients = new ConcurrentDictionary<string, (WebSocket socket, string? partnerId)>();
var pairingCodes = new ConcurrentDictionary<string, string>(); // code -> clientId
var clientCodes = new ConcurrentDictionary<string, string>();  // clientId -> code

// Server info endpoint
app.MapGet("/serverinfo", () =>
{
    var ip = Dns.GetHostEntry(Dns.GetHostName())
        .AddressList.FirstOrDefault(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?
        .ToString();
    return Results.Json(new { ip, machineName = Environment.MachineName });
});

app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        return;
    }

    var socket = await context.WebSockets.AcceptWebSocketAsync();
    var clientId = Guid.NewGuid().ToString();
    clients[clientId] = (socket, null);

    try
    {
        var buffer = new byte[1024 * 4];
        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed by client", CancellationToken.None);
                break;
            }

            var message = Encoding.UTF8.GetString(buffer, 0, result.Count);

            if (message.StartsWith("OPEN:"))
            {
                string code = GenerateCode();
                pairingCodes[code] = clientId;
                clientCodes[clientId] = code;
                await Send(socket, $"CODE:{code}");
            }
            else if (message.StartsWith("JOIN:"))
            {
                string code = message.Substring(5).Trim();
                if (pairingCodes.TryRemove(code, out var hostId) &&
                    clients.TryGetValue(hostId, out var hostClient) &&
                    hostClient.partnerId == null)
                {
                    clients[clientId] = (socket, hostId);
                    clients[hostId] = (hostClient.socket, clientId);

                    await Send(socket, "CONNECTED");
                    await Send(hostClient.socket, "CONNECTED");
                }
                else
                {
                    await Send(socket, "ERROR:Invalid or expired code");
                }
            }
            else if (message == "PING")
            {
                await Send(socket, "PONG");
            }
            else if (!string.IsNullOrEmpty(message)) // Clipboard data
            {
                if (clients.TryGetValue(clientId, out var current) && current.partnerId != null)
                {
                    if (clients.TryGetValue(current.partnerId, out var partner))
                    {
                        await Send(partner.socket, message);
                    }
                }
            }
        }
    }
    finally
    {
        CleanupClient(clientId);
        socket.Dispose();
    }
});

void CleanupClient(string clientId)
{
    if (clients.TryRemove(clientId, out var client))
    {
        if (client.partnerId != null && clients.TryGetValue(client.partnerId, out var partner))
        {
            _ = Send(partner.socket, "DISCONNECTED");
            clients[client.partnerId] = (partner.socket, null);
        }

        if (clientCodes.TryRemove(clientId, out var code))
        {
            pairingCodes.TryRemove(code, out _);
        }
    }
}

string GenerateCode()
{
    const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    var rand = new Random();
    return new string([.. Enumerable.Repeat(chars, 5).Select(s => s[rand.Next(s.Length)])]);
}

async Task Send(WebSocket socket, string message)
{
    if (socket?.State == WebSocketState.Open)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        await socket.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            true,
            CancellationToken.None);
    }
}

app.Run("http://0.0.0.0:5000");
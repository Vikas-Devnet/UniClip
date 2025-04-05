using System.Net;
using System.Net.WebSockets;
using System.Text;
using Timer = System.Windows.Forms.Timer;

namespace UniclipClient
{
    public partial class Client : Form
    {
        private NotifyIcon trayIcon;
        private ContextMenuStrip trayMenu;
        private ClientWebSocket ws;
        private bool isReceiving = false;
        private bool isConnected = false;
        private Timer clipboardTimer;
        private string lastClipboardText = "";
        private readonly CancellationTokenSource cts = new();

        public Client()
        {
            InitializeComponent();
            InitializeTray();
        }

        private void InitializeTray()
        {
            trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("Open Connection", null, OnOpenConnection);
            trayMenu.Items.Add("Connect to Code", null, OnConnect);
            trayMenu.Items.Add("Close Connection", null, OnCloseConnection);
            trayMenu.Items.Add("Exit", null, OnExit);

            trayIcon = new NotifyIcon
            {
                Text = "Uniclip",
                Icon = SystemIcons.Application,
                ContextMenuStrip = trayMenu,
                Visible = true
            };

            WindowState = FormWindowState.Minimized;
            ShowInTaskbar = false;
            Load += (s, e) => Hide();
        }

        private async void OnOpenConnection(object sender, EventArgs e)
        {
            if (isConnected)
            {
                MessageBox.Show("Connection Already Established", "Uniclip", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                string serverUrl = await DiscoverServer();
                ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri(serverUrl), cts.Token);
                await Send("OPEN:");
                isConnected = true;
                StartClipboardWatcher();
                _ = ReceiveLoop();
            }
            catch (Exception ex)
            {
                ShowError($"Could not connect automatically. Please enter server address manually.\n\nError: {ex.Message}");
                ManualServerEntry();
            }
        }

        private async Task<string> DiscoverServer(string? serverIp = null)
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

        private void ManualServerEntry()
        {
            string serverUrl = Microsoft.VisualBasic.Interaction.InputBox(
                "Enter server IpAddress",
                "Manual Server Entry",
                "");
            serverUrl = DiscoverServer(serverUrl).Result;
            if (!string.IsNullOrWhiteSpace(serverUrl))
            {
                try
                {
                    ws = new ClientWebSocket();
                    ws.ConnectAsync(new Uri(serverUrl), cts.Token).Wait();
                    Send("OPEN:").Wait();
                    isConnected = true;
                    StartClipboardWatcher();
                    _ = ReceiveLoop();
                }
                catch (Exception ex)
                {
                    ShowError($"Connection failed: {ex.Message}");
                }
            }
        }

        private async void OnConnect(object sender, EventArgs e)
        {
            if (isConnected)
            {
                MessageBox.Show("Device Already connected", "Uniclip", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using var form = new Form();
            form.Text = "Connection Details";
            form.Size = new Size(300, 150);
            form.FormBorderStyle = FormBorderStyle.FixedDialog;
            form.StartPosition = FormStartPosition.CenterScreen;

            var lblIp = new Label() { Text = "Server IP:", Left = 10, Top = 10, Width = 80 };
            var txtIp = new TextBox() { Left = 100, Top = 10, Width = 150 };

            var lblCode = new Label() { Text = "Code:", Left = 10, Top = 40, Width = 80 };
            var txtCode = new TextBox() { Left = 100, Top = 40, Width = 150 };

            var btnOk = new Button() { Text = "Connect", Left = 100, Top = 70, Width = 70 };
            var btnCancel = new Button() { Text = "Cancel", Left = 180, Top = 70, Width = 70 };

            btnOk.DialogResult = DialogResult.OK;
            btnCancel.DialogResult = DialogResult.Cancel;

            form.Controls.AddRange([lblIp, txtIp, lblCode, txtCode, btnOk, btnCancel]);
            form.AcceptButton = btnOk;
            form.CancelButton = btnCancel;

            if (form.ShowDialog() != DialogResult.OK) return;

            try
            {
                string serverIp = txtIp.Text.Trim();
                string code = txtCode.Text.Trim();

                if (string.IsNullOrWhiteSpace(serverIp) || string.IsNullOrWhiteSpace(code))
                {
                    ShowError("Both fields are required");
                    return;
                }

                if (ws == null || ws.State != WebSocketState.Open)
                {
                    string serverUrl = await DiscoverServer(serverIp);
                    ws = new ClientWebSocket();
                    await ws.ConnectAsync(new Uri(serverUrl), cts.Token);
                }

                await Send($"JOIN:{code}");
                isConnected = true;
                StartClipboardWatcher();
                _ = ReceiveLoop();
            }
            catch (Exception ex)
            {
                ShowError($"Connection failed: {ex.Message}");
            }
        }

        private async void OnCloseConnection(object sender, EventArgs e)
        {
            isConnected = false;
            clipboardTimer?.Stop();

            try
            {
                if (ws?.State == WebSocketState.Open)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed", cts.Token);
                }
            }
            catch { }
            finally
            {
                ws?.Dispose();
                ws = null;
            }
        }

        private void OnExit(object sender, EventArgs e)
        {
            cts.Cancel();
            OnCloseConnection(sender, e);
            trayIcon.Visible = false;
            Application.Exit();
        }

        private void StartClipboardWatcher()
        {
            clipboardTimer = new Timer { Interval = 500 };
            clipboardTimer.Tick += async (s, e) =>
            {
                if (isReceiving || !isConnected) return;

                try
                {
                    if (Clipboard.ContainsText())
                    {
                        string text = Clipboard.GetText();
                        if (text != lastClipboardText)
                        {
                            await Send(text);
                            lastClipboardText = text;
                        }
                    }
                }
                catch { }
            };
            clipboardTimer.Start();
        }

        private async Task Send(string message)
        {
            if (ws?.State == WebSocketState.Open)
            {
                try
                {
                    var bytes = Encoding.UTF8.GetBytes(message);
                    await ws.SendAsync(
                        new ArraySegment<byte>(bytes),
                        WebSocketMessageType.Text,
                        true,
                        cts.Token);
                }
                catch
                {
                    isConnected = false;
                    clipboardTimer?.Stop();
                }
            }
        }

        private async Task ReceiveLoop()
        {
            var buffer = new byte[1024 * 4];
            while (isConnected && ws?.State == WebSocketState.Open)
            {
                try
                {
                    var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        isConnected = false;
                        break;
                    }

                    string message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    Invoke(new Action(() => HandleMessage(message)));
                }
                catch
                {
                    isConnected = false;
                    break;
                }
            }

            if (!isConnected)
            {
                Invoke(new Action(() =>
                    trayIcon.ShowBalloonTip(3000, "Uniclip", "Disconnected", ToolTipIcon.Info)));
            }
        }

        private void HandleMessage(string message)
        {
            if (message.StartsWith("CODE:"))
            {
                string code = message.Substring(5);
                MessageBox.Show($"Your Connection Code: {code}", "Uniclip");
            }
            else if (message.StartsWith("CONNECTED"))
            {
                trayIcon.ShowBalloonTip(3000, "Uniclip", "Devices Connected", ToolTipIcon.Info);
            }
            else if (message.StartsWith("ERROR:"))
            {
                ShowError(message.Substring(6));
                isConnected = false;
                clipboardTimer?.Stop();
            }
            else if (message == "DISCONNECTED")
            {
                trayIcon.ShowBalloonTip(3000, "Uniclip", "Partner Disconnected", ToolTipIcon.Info);
                isConnected = false;
                clipboardTimer?.Stop();
            }
            else if (!string.IsNullOrEmpty(message))
            {
                isReceiving = true;
                try
                {
                    Clipboard.SetText(message);
                    lastClipboardText = message;
                }
                catch { }
                isReceiving = false;
            }
        }

        private static void ShowError(string message)
        {
            MessageBox.Show(message, "Uniclip Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
using System.Net.WebSockets;
using System.Text;
using UniclipClient.Helpers;
using Timer = System.Windows.Forms.Timer;

namespace UniclipClient
{
    public partial class Client : Form
    {
        private NotifyIcon? trayIcon;
        private ContextMenuStrip? trayMenu;
        private ClientWebSocket? ws;
        private bool isReceiving = false;
        private bool isConnected = false;
        private Timer? clipboardTimer;
        private string lastClipboardText = "";
        private readonly CancellationTokenSource cts = new();
        private static string _roomSecretCode = string.Empty;

        public Client()
        {
            InitializeComponent();
            InitializeTray();
        }

        #region Tray Menu Handlers
        private async void HandleCreateRoomRequest(object? sender, EventArgs e)
        {
            if (isConnected)
            {
                MessageBox.Show($"Room Already Created with Code {_roomSecretCode}", "Uniclip", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                string serverUrl = await UniclipHelper.DiscoverServer(cts);
                await ConnectToServer(serverUrl);
            }
            catch (Exception ex)
            {
                ShowError($"Could not connect automatically. Please enter server address manually.\n\nError: {ex.Message}");
                await ManualServerEntry();
            }
        }
        private async void HandleJoinRoomRequest(object? sender, EventArgs e)
        {
            if (isConnected)
            {
                MessageBox.Show($"Device already in Room with code {_roomSecretCode}", "Uniclip", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
                    string serverUrl = await UniclipHelper.DiscoverServer(cts, serverIp);
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
        private async void HanleCloseRoomRequest(object? sender, EventArgs e)
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
        private void HandleExitRequest(object? sender, EventArgs e)
        {
            cts.Cancel();
            HanleCloseRoomRequest(sender, e);
            if (trayIcon != null)
                trayIcon.Visible = false;
            Application.Exit();
        }
        #endregion


        #region Message Handlers
        private void HandleMessage(string message)
        {
            if (message.StartsWith("CODE:"))
            {
                _roomSecretCode = message[5..];
                MessageBox.Show($"Your Room Code : {_roomSecretCode}", "Uniclip");
            }
            else if (message.StartsWith("CONNECTED"))
            {
                trayIcon?.ShowBalloonTip(3000, "Uniclip", "Joined Room Successfully", ToolTipIcon.Info);
            }
            else if (message.StartsWith("ERROR:"))
            {
                ShowError(message[6..]);
                isConnected = false;
                clipboardTimer?.Stop();
            }
            else if (message == "DISCONNECTED")
            {
                trayIcon?.ShowBalloonTip(3000, "Uniclip", "Lost Room Connectivity", ToolTipIcon.Info);
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
            else
            {
                MessageBox.Show("Something went wrong", "Uniclip", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private static void ShowError(string message)
        {
            MessageBox.Show(message, "Uniclip Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        #endregion


        #region Private Support Helper
        private void InitializeTray()
        {
            trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("CREATE ROOM", UniclipHelper.ConvertBytesTo<Image>(Properties.Resource.CreateRoom), HandleCreateRoomRequest);
            trayMenu.Items.Add("JOIN ROOM", UniclipHelper.ConvertBytesTo<Image>(Properties.Resource.JoinRoom), HandleJoinRoomRequest);
            trayMenu.Items.Add("CLOSE ROOM", UniclipHelper.ConvertBytesTo<Image>(Properties.Resource.CloseRoom), HanleCloseRoomRequest);
            trayMenu.Items.Add("EXIT", UniclipHelper.ConvertBytesTo<Image>(Properties.Resource.Exit), HandleExitRequest);
            trayMenu.ShowImageMargin = true;

            trayIcon = new NotifyIcon
            {
                Text = "Uniclip",
                Icon = UniclipHelper.ConvertBytesTo<Icon>(Properties.Resource.uniclip),
                ContextMenuStrip = trayMenu,
                Visible = true
            };
            trayIcon.DoubleClick += (s, e) => MessageBox.Show(isConnected ? "ALREADY IN ROOM" : "AVAILABLE TO JOIN ROOM", "Uniclip", MessageBoxButtons.OK, isConnected ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            WindowState = FormWindowState.Minimized;
            ShowInTaskbar = false;
            Load += (s, e) => Hide();
        }
        private async Task ConnectToServer(string serverUrl)
        {
            ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri(serverUrl), cts.Token);
            await Send("OPEN:");
            isConnected = true;
            StartClipboardWatcher();
            _ = ReceiveLoop();
        }
        private async Task ManualServerEntry()
        {
            string serverUrl = Microsoft.VisualBasic.Interaction.InputBox("Enter server IpAddress", "Manual Server Entry", "");
            serverUrl = await UniclipHelper.DiscoverServer(cts, serverUrl);
            if (!string.IsNullOrWhiteSpace(serverUrl))
            {
                try
                {
                    await ConnectToServer(serverUrl);
                }
                catch (Exception ex)
                {
                    ShowError($"Connection failed: {ex.Message}");
                }
            }
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

        #endregion


        #region SOCKET HELPER
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
                    trayIcon?.ShowBalloonTip(3000, "Uniclip", "Lost Room Connectivity", ToolTipIcon.Info)));
            }
        }

        #endregion
    }
}
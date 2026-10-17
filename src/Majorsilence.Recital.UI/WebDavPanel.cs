using Majorsilence.Forms;

namespace Majorsilence.Recital.UI
{
    /// <summary>The address, user name and password of a WebDAV folder (Nextcloud, ownCloud, a NAS), as an in-window panel.</summary>
    public class WebDavPanel : Panel
    {
        private readonly TextBox address = new () { Width = 360, PlaceholderText = "https://cloud.example.com/remote.php/dav/files/me/" };
        private readonly TextBox user = new () { Width = 360 };
        private readonly TextBox password = new () { Width = 360, UseSystemPasswordChar = true };
        private readonly Label message = new () { AutoSize = true, MaximumSize = new System.Drawing.Size (360, 0) };
        private readonly Button connect = new () { Text = "Connect", AutoSize = true };

        public WebDavPanel ()
        {
            Visible = false;
            BorderStyle = BorderStyle.FixedSingle;
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding (16), AutoScroll = true };
            flow.Controls.Add (new Label { Text = "Sync with a WebDAV folder", AutoSize = true, Font = new Majorsilence.Forms.Drawing.Font (Font.FontFamily, Font.Size * 1.2f) });
            flow.Controls.Add (new Label { Text = "Folder address", AutoSize = true, Padding = new Padding (0, 8, 0, 0) });
            flow.Controls.Add (address);
            flow.Controls.Add (new Label { Text = "User name", AutoSize = true, Padding = new Padding (0, 8, 0, 0) });
            flow.Controls.Add (user);
            flow.Controls.Add (new Label { Text = "Password (an app password, where the server offers them)", AutoSize = true, Padding = new Padding (0, 8, 0, 0) });
            flow.Controls.Add (password);
            flow.Controls.Add (new Label { Text = "The password is kept in this device's secure store, never in the synced folder.", AutoSize = true, MaximumSize = new System.Drawing.Size (360, 0), Padding = new Padding (0, 6, 0, 0) });
            flow.Controls.Add (message);

            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = new Padding (0, 10, 0, 0) };
            var cancel = new Button { Text = "Cancel", AutoSize = true };
            connect.Click += (_, _) => {
                if (!Uri.TryCreate (address.Text?.Trim (), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) {
                    ShowMessage ("The address starts with https://");
                    return;
                }
                ConnectRequested?.Invoke (this, new WebDavCredentials (uri, user.Text?.Trim () ?? "", password.Text ?? ""));
            };
            cancel.Click += (_, _) => Cancelled?.Invoke (this, EventArgs.Empty);
            buttons.Controls.Add (connect);
            buttons.Controls.Add (cancel);
            flow.Controls.Add (buttons);
            Controls.Add (flow);
        }

        public TextBox Address => address;
        public TextBox UserName => user;
        public TextBox Password => password;
        public string Message => message.Text ?? string.Empty;

        public event EventHandler<WebDavCredentials>? ConnectRequested;
        public event EventHandler? Cancelled;

        public void Open ()
        {
            message.Text = "";
            password.Text = "";
            connect.Enabled = true;
            Visible = true;
            BringToFront ();
            address.Focus ();
        }

        /// <summary>While connecting: the button is disabled and the message says so.</summary>
        public void ShowBusy (bool busy)
        {
            connect.Enabled = !busy;
            if (busy)
                message.Text = "Connecting…";
        }

        public void ShowMessage (string text)
        {
            message.Text = text;
            connect.Enabled = true;
        }
    }

    public sealed record WebDavCredentials (Uri Address, string UserName, string Password)
    {
        // Never shown in logs or exception messages.
        public override string ToString () => $"WebDavCredentials {{ Address = {Address}, UserName = {UserName} }}";
    }
}

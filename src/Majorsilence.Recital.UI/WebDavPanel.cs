using Majorsilence.Forms;
using Majorsilence.Recital.UI.Localization;

namespace Majorsilence.Recital.UI
{
    /// <summary>The address, user name and password of a WebDAV folder (Nextcloud, ownCloud, a NAS), as an in-window panel.</summary>
    public class WebDavPanel : Panel
    {
        private readonly TextBox address = new () { Width = 360 };
        private readonly TextBox user = new () { Width = 360 };
        private readonly TextBox password = new () { Width = 360, UseSystemPasswordChar = true };
        private readonly Label message = new () { AutoSize = true, MaximumSize = new System.Drawing.Size (360, 0) };
        private readonly Button connect = new () { AutoSize = true };
        private readonly Button cancel = new () { AutoSize = true };
        private readonly Label heading = new () { AutoSize = true };
        private readonly Label address_caption = new () { AutoSize = true, Padding = new Padding (0, 8, 0, 0) };
        private readonly Label user_caption = new () { AutoSize = true, Padding = new Padding (0, 8, 0, 0) };
        private readonly Label password_caption = new () { AutoSize = true, Padding = new Padding (0, 8, 0, 0) };
        private readonly Label password_note = new () { AutoSize = true, MaximumSize = new System.Drawing.Size (360, 0), Padding = new Padding (0, 6, 0, 0) };

        public WebDavPanel ()
        {
            Visible = false;
            BorderStyle = BorderStyle.FixedSingle;
            heading.Font = new Majorsilence.Forms.Drawing.Font (Font.FontFamily, Font.Size * 1.2f);
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding (16), AutoScroll = true };
            flow.Controls.Add (heading);
            flow.Controls.Add (address_caption);
            flow.Controls.Add (address);
            flow.Controls.Add (user_caption);
            flow.Controls.Add (user);
            flow.Controls.Add (password_caption);
            flow.Controls.Add (password);
            flow.Controls.Add (password_note);
            flow.Controls.Add (message);

            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = new Padding (0, 10, 0, 0) };
            connect.Click += (_, _) => {
                if (!Uri.TryCreate (address.Text?.Trim (), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) {
                    ShowMessage (Strings.Get ("WebDavAddressMustBeHttps"));
                    return;
                }
                // Android sends nothing unencrypted (a WebDAV password goes with every request), but for the
                // emulator's own name for the development machine (the app's network security config).
                if (OperatingSystem.IsAndroid () && uri.Scheme == "http" && uri.Host != "10.0.2.2") {
                    ShowMessage (Strings.Get ("WebDavAndroidMustBeHttps"));
                    return;
                }
                ConnectRequested?.Invoke (this, new WebDavCredentials (uri, user.Text?.Trim () ?? "", password.Text ?? ""));
            };
            cancel.Click += (_, _) => Cancelled?.Invoke (this, EventArgs.Empty);
            buttons.Controls.Add (connect);
            buttons.Controls.Add (cancel);
            flow.Controls.Add (buttons);
            Controls.Add (flow);
            ApplyLanguage ();
        }

        /// <summary>Re-reads this panel's own chrome text; <see cref="LibraryView.ApplyLanguage"/> calls it.</summary>
        public void ApplyLanguage ()
        {
            heading.Text = Strings.Get ("WebDavHeading");
            address_caption.Text = Strings.Get ("WebDavFolderAddress");
            address.PlaceholderText = Strings.Get ("WebDavAddressPlaceholder");
            user_caption.Text = Strings.Get ("WebDavUserName");
            password_caption.Text = Strings.Get ("WebDavPassword");
            password_note.Text = Strings.Get ("WebDavPasswordNote");
            connect.Text = Strings.Get ("Connect");
            cancel.Text = Strings.Get ("Cancel");
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
                message.Text = Strings.Get ("Connecting");
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

using System.Windows;
using System.Windows.Input;
using Dingus.Models;
using Dingus.Services;

namespace Dingus.Views;

public partial class PlaylistDialog : Window
{
    readonly PlaylistConfig _existing;
    public PlaylistConfig Result { get; private set; }

    public PlaylistDialog(PlaylistConfig existing)
    {
        _existing = existing;
        InitializeComponent();
        SourceInitialized += (_, _) => NativeWindow.ApplyDarkTitleBar(this);

        if (existing != null)
        {
            HeaderText.Text = "Edit playlist";
            NameBox.Text = existing.Name;
            ServerBox.Text = existing.Server;
            UserBox.Text = existing.Username;
            PassBox.Password = existing.Password;
            M3uBox.Text = existing.M3uUrl;
            EpgBox.Text = existing.EpgUrl;
            (existing.Type == PlaylistType.M3U ? TypeM3u : TypeXtream).IsChecked = true;
        }
        else
        {
            TypeXtream.IsChecked = true;
        }
        Loaded += (_, _) => NameBox.Focus();
    }

    bool IsXtream => TypeXtream.IsChecked == true;

    void Type_Checked(object sender, RoutedEventArgs e)
    {
        if (XtreamPanel == null || M3uPanel == null) return; // during InitializeComponent
        XtreamPanel.Visibility = IsXtream ? Visibility.Visible : Visibility.Collapsed;
        M3uPanel.Visibility = IsXtream ? Visibility.Collapsed : Visibility.Visible;
    }

    void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    async void Save_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        var name = NameBox.Text.Trim();
        var server = ServerBox.Text.Trim();
        var user = UserBox.Text.Trim();
        var pass = PassBox.Password.Trim();
        var m3u = M3uBox.Text.Trim();
        var epg = EpgBox.Text.Trim();

        if (name.Length == 0) { ShowError("Give the playlist a name."); return; }

        if (IsXtream)
        {
            if (server.Length == 0 || user.Length == 0 || pass.Length == 0)
            {
                ShowError("Enter the server address, username and password.");
                return;
            }
            SaveButton.IsEnabled = false;
            SaveButton.Content = "Checking…";
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                await new XtreamClient(server, user, pass).LoginAsync(cts.Token);
            }
            catch (Exception ex)
            {
                ShowError(ex is InvalidOperationException ? ex.Message : "Couldn't sign in: " + Http.Friendly(ex) + ".");
                SaveButton.IsEnabled = true;
                SaveButton.Content = "Save";
                return;
            }
        }
        else
        {
            bool isFile = File.Exists(m3u);
            if (!isFile && !(Uri.TryCreate(m3u, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https")))
            {
                ShowError("Enter a valid http(s) M3U address, or the path of an .m3u file on this PC.");
                return;
            }
        }

        var p = _existing ?? new PlaylistConfig();
        p.Name = name;
        p.Type = IsXtream ? PlaylistType.Xtream : PlaylistType.M3U;
        p.Server = IsXtream ? XtreamClient.NormalizeServer(server) : "";
        p.Username = IsXtream ? user : "";
        p.Password = IsXtream ? pass : "";
        p.M3uUrl = IsXtream ? "" : m3u;
        p.EpgUrl = epg;
        Result = p;
        DialogResult = true;
    }

    void ShowError(string msg)
    {
        ErrorText.Text = msg;
        ErrorText.Visibility = Visibility.Visible;
    }
}

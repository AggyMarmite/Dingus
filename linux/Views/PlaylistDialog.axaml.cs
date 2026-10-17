using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Dingus.Models;
using Dingus.Services;

namespace Dingus.Views;

public partial class PlaylistDialog : Window
{
    readonly PlaylistConfig _existing;
    public PlaylistConfig Result { get; private set; }

    public PlaylistDialog() : this(null) { }

    public PlaylistDialog(PlaylistConfig existing)
    {
        _existing = existing;
        InitializeComponent();
        if (existing != null)
        {
            HeaderText.Text = "Edit playlist";
            NameBox.Text = existing.Name;
            ServerBox.Text = existing.Server;
            UserBox.Text = existing.Username;
            PassBox.Text = existing.Password;
            M3uBox.Text = existing.M3uUrl;
            EpgBox.Text = existing.EpgUrl;
            (existing.Type == PlaylistType.M3U ? TypeM3u : TypeXtream).IsChecked = true;
        }
        else TypeXtream.IsChecked = true;
        Opened += (_, _) => NameBox.Focus();
    }

    bool IsXtream => TypeXtream.IsChecked == true;

    void Type_Checked(object sender, RoutedEventArgs e)
    {
        if (XtreamPanel == null || M3uPanel == null) return;
        XtreamPanel.IsVisible = IsXtream;
        M3uPanel.IsVisible = !IsXtream;
    }

    void Header_PointerPressed(object sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose an M3U playlist",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Playlists") { Patterns = new[] { "*.m3u", "*.m3u8", "*.txt" } }, FilePickerFileTypes.All },
        });
        var f = files.FirstOrDefault()?.TryGetLocalPath();
        if (f != null) M3uBox.Text = f;
    }

    async void Save_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.IsVisible = false;
        var name = (NameBox.Text ?? "").Trim();
        var server = (ServerBox.Text ?? "").Trim();
        var user = (UserBox.Text ?? "").Trim();
        var pass = (PassBox.Text ?? "").Trim();
        var m3u = (M3uBox.Text ?? "").Trim();
        var epg = (EpgBox.Text ?? "").Trim();

        if (name.Length == 0) { ShowError("Give the playlist a name."); return; }
        if (IsXtream)
        {
            if (server.Length == 0 || user.Length == 0 || pass.Length == 0) { ShowError("Enter the server address, username and password."); return; }
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
                ShowError("Enter a valid http(s) M3U address, or choose an .m3u file on this computer.");
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
        Close(true);
    }

    void ShowError(string msg)
    {
        ErrorText.Text = msg;
        ErrorText.IsVisible = true;
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Dingus.Services;

namespace Dingus.Views;

/// <summary>Settings: your Stremio account and add-ons, and Real-Debrid.</summary>
public partial class StremioSettings : UserControl
{
    CancellationTokenSource _rdWait;
    RdDevice _device;

    public StremioSettings()
    {
        InitializeComponent();
        Stremio.Changed += () => Dispatcher.InvokeAsync(Refresh);
        RealDebrid.Changed += () => Dispatcher.InvokeAsync(Refresh);
        Loaded += async (_, _) =>
        {
            Refresh();
            if (Stremio.Addons.Any(a => a.Resources.Count == 0)) await Stremio.EnsureManifestsAsync();
            await RefreshRd();
        };
    }

    void Refresh()
    {
        bool signedIn = Stremio.SignedIn;
        SignedOutPart.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        SignedInPart.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        SignedInText.Text = signedIn ? $"Signed in as {Stremio.Email}" : "";
        AddonList.ItemsSource = Stremio.SettingsItems();
        bool rd = RealDebrid.Connected;
        RdOffPart.Visibility = rd ? Visibility.Collapsed : Visibility.Visible;
        RdOnPart.Visibility = rd ? Visibility.Visible : Visibility.Collapsed;
    }

    static void Say(TextBlock t, string text)
    {
        t.Text = text ?? "";
        t.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    // ------------------------------------------------------------------ Stremio account

    void Password_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { SignIn_Click(null, null); e.Handled = true; } }

    async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        var email = (EmailBox.Text ?? "").Trim();
        if (email.Length == 0 || PasswordBox.Password.Length == 0) { Say(AccountStatus, "Type your Stremio email and password."); return; }
        SignInButton.IsEnabled = false;
        Say(AccountStatus, "Signing in…");
        try
        {
            int n = await Stremio.SignInAsync(email, PasswordBox.Password);
            PasswordBox.Clear();
            Say(AccountStatus, n == 1 ? "Signed in – 1 add-on brought in." : $"Signed in – {n} add-ons brought in.");
        }
        catch (Exception ex) { Log.Error(ex); Say(AccountStatus, "Couldn't sign in: " + Http.Friendly(ex)); }
        finally { SignInButton.IsEnabled = true; Refresh(); }
    }

    async void RefreshAddons_Click(object sender, RoutedEventArgs e)
    {
        RefreshAddonsButton.IsEnabled = false;
        try
        {
            int n = await Stremio.PullAccountAddonsAsync();
            Say(AccountStatus, n == 1 ? "1 add-on from your account." : $"{n} add-ons from your account.");
        }
        catch (Exception ex) { Log.Error(ex); Say(AccountStatus, "Couldn't refresh: " + Http.Friendly(ex)); }
        finally { RefreshAddonsButton.IsEnabled = true; }
    }

    void SignOut_Click(object sender, RoutedEventArgs e)
    {
        Stremio.SignOut();
        Say(AccountStatus, "Signed out. Add-ons you added by address are still here.");
    }

    // ------------------------------------------------------------------ add-ons

    void AddonUrl_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { AddAddon_Click(null, null); e.Handled = true; } }

    async void AddAddon_Click(object sender, RoutedEventArgs e)
    {
        var url = (AddonUrlBox.Text ?? "").Trim();
        if (url.Length == 0) { Say(AddonStatus, "Paste the add-on's address (it usually ends in manifest.json)."); return; }
        AddAddonButton.IsEnabled = false;
        Say(AddonStatus, "Adding…");
        try
        {
            var a = await Stremio.InstallAsync(url);
            AddonUrlBox.Text = "";
            Say(AddonStatus, $"Added {a.Name}.");
        }
        catch (Exception ex) { Log.Error(ex); Say(AddonStatus, "Couldn't add it: " + (ex is System.Text.Json.JsonException ? "that address isn't a Stremio add-on" : Http.Friendly(ex)) + "."); }
        finally { AddAddonButton.IsEnabled = true; }
    }

    void AddonToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: AddonItem it } cb) Stremio.SetEnabled(it.Addon, cb.IsChecked == true);
    }

    void AddonRemove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AddonItem it) return;
        Stremio.Remove(it.Addon);
        Say(AddonStatus, it.Addon.FromAccount ? $"Removed {it.Name} (it comes back if you refresh your Stremio add-ons)." : $"Removed {it.Name}.");
    }

    // ------------------------------------------------------------------ Real-Debrid

    async Task RefreshRd()
    {
        if (!RealDebrid.Connected) return;
        try
        {
            var u = await RealDebrid.UserAsync();
            RdUserText.Text = $"Connected as {u.Username}";
            RdPremiumText.Text = u.Premium
                ? $"Premium until {u.Expiration?.ToLocalTime():d MMM yyyy} ({u.DaysLeft} day{(u.DaysLeft == 1 ? "" : "s")} left)"
                : "Not premium – torrent sources need a premium account";
        }
        catch (Exception ex) { RdUserText.Text = "Connected"; RdPremiumText.Text = ex is InvalidOperationException ? ex.Message : "Couldn't check your account: " + Http.Friendly(ex); }
    }

    async void RdConnect_Click(object sender, RoutedEventArgs e)
    {
        _rdWait?.Cancel();
        var cts = _rdWait = new CancellationTokenSource();
        RdConnectButton.IsEnabled = false;
        Say(RdStatus, "");
        try
        {
            _device = await RealDebrid.StartDeviceAsync(cts.Token);
            RdCode.Text = _device.UserCode;
            RdCodeHint.Text = $"On {_device.VerificationUrl.Replace("https://", "")}, enter:";
            RdCodePart.Visibility = Visibility.Visible;
            if (await RealDebrid.WaitForDeviceAsync(_device, cts.Token))
            {
                RdCodePart.Visibility = Visibility.Collapsed;
                Refresh();
                await RefreshRd();
                MainWindow.Instance?.ShowToast("Real-Debrid connected");
            }
            else { RdCodePart.Visibility = Visibility.Collapsed; Say(RdStatus, "The code ran out – press Connect to get a new one."); }
        }
        catch (OperationCanceledException) { RdCodePart.Visibility = Visibility.Collapsed; }
        catch (Exception ex) { Log.Error(ex); RdCodePart.Visibility = Visibility.Collapsed; Say(RdStatus, "Couldn't connect: " + Http.Friendly(ex)); }
        finally { RdConnectButton.IsEnabled = true; }
    }

    void RdOpen_Click(object sender, RoutedEventArgs e)
    {
        var url = _device?.DirectUrl ?? _device?.VerificationUrl ?? "https://real-debrid.com/device";
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    void RdCancel_Click(object sender, RoutedEventArgs e) { _rdWait?.Cancel(); RdCodePart.Visibility = Visibility.Collapsed; }

    void RdTokenShow_Click(object sender, RoutedEventArgs e) =>
        RdTokenPart.Visibility = RdTokenPart.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    async void RdToken_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RealDebrid.UseApiTokenAsync(RdTokenBox.Password);
            RdTokenBox.Clear();
            Refresh();
            await RefreshRd();
            MainWindow.Instance?.ShowToast("Real-Debrid connected");
        }
        catch (Exception ex) { Say(RdStatus, "That token didn't work: " + (ex is InvalidOperationException ? ex.Message : Http.Friendly(ex))); }
    }

    void RdDisconnect_Click(object sender, RoutedEventArgs e)
    {
        RealDebrid.Disconnect();
        Say(RdStatus, "Disconnected.");
    }
}

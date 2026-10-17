using Avalonia.Controls;

namespace Dingus.Services;

/// <summary>The PIN prompt (Linux).</summary>
public static partial class Parental
{
    /// <summary>Asks for the PIN and unlocks for this session. Returns true if unlocked.</summary>
    public static async Task<bool> TryUnlockAsync(Window owner)
    {
        if (!Active) return true;
        var pin = await AskPinAsync(owner, "Enter your PIN", "Unlocks the locked categories until you close Dingus.");
        if (pin == null) return false;
        if (!Verify(pin))
        {
            MainWindow.Instance?.ShowToast("That PIN isn't right.");
            return false;
        }
        Unlocked = true;
        Notify();
        return true;
    }

    /// <summary>A PIN prompt (shown as dots). Returns null if cancelled.</summary>
    public static Task<string> AskPinAsync(Window owner, string title, string prompt) =>
        Dialogs.Ask(owner, title, prompt, "", password: true);
}

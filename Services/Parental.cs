using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Dingus.Services;

/// <summary>
/// Parental PIN. Locked categories are hidden everywhere (guide, search, films, shows, Home) until the PIN
/// is entered; they then stay unlocked until the app closes or you lock them again.
/// The PIN itself is never stored – only a salted hash of it.
/// </summary>
public static partial class Parental
{
    static readonly Regex AdultRx = new(@"\b(adults?|xxx|18\s*\+|porn\w*|erotic\w*|for\s+adults)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool Unlocked { get; private set; }
    public static bool HasPin => !string.IsNullOrEmpty(SettingsStore.Current.ParentalPinHash);
    /// <summary>Locks are in force right now.</summary>
    public static bool Active => HasPin && !Unlocked;

    public static event Action Changed;

    /// <summary>Locked category ids of one kind (live | vod | series), whether or not they're unlocked right now.</summary>
    public static HashSet<string> LockedSet(string kind)
    {
        var set = new HashSet<string>();
        var p = DataService.Instance.Active;
        if (!HasPin || p == null) return set;
        var prefix = kind + ":";
        foreach (var l in p.LockedCategories)
            if (l.StartsWith(prefix, StringComparison.Ordinal)) set.Add(l[prefix.Length..]);
        if (SettingsStore.Current.AutoLockAdult)
        {
            var data = DataService.Instance.Catalog;
            var cats = kind switch { "vod" => data.VodCategories, "series" => data.SeriesCategories, _ => data.LiveCategories };
            foreach (var c in cats) if (IsAdultName(c.Name)) set.Add(c.Id);
        }
        return set;
    }

    public static bool IsAdultName(string name) => name != null && AdultRx.IsMatch(name);

    public static void SetPin(string pin)
    {
        var s = SettingsStore.Current;
        if (string.IsNullOrEmpty(pin))
        {
            s.ParentalPinHash = null;
            s.ParentalPinSalt = null;
        }
        else
        {
            var salt = RandomNumberGenerator.GetBytes(16);
            s.ParentalPinSalt = Convert.ToBase64String(salt);
            s.ParentalPinHash = Convert.ToBase64String(Hash(pin, salt));
        }
        Unlocked = false;
        SettingsStore.Save();
        Notify();
    }

    public static bool Verify(string pin)
    {
        var s = SettingsStore.Current;
        if (!HasPin || pin == null) return false;
        try
        {
            var expected = Convert.FromBase64String(s.ParentalPinHash);
            var got = Hash(pin, Convert.FromBase64String(s.ParentalPinSalt ?? ""));
            return CryptographicOperations.FixedTimeEquals(expected, got);
        }
        catch { return false; }
    }

    static byte[] Hash(string pin, byte[] salt) => Rfc2898DeriveBytes.Pbkdf2(pin, salt, 100_000, HashAlgorithmName.SHA256, 32);

    public static void Lock()
    {
        if (!HasPin) return;
        Unlocked = false;
        Notify();
    }

    public static void Notify()
    {
        Changed?.Invoke();
        DataService.Instance.NotifyCatalogChanged(); // everything re-reads the hidden categories
    }
}

using System.Globalization;
using System.Text.Json.Nodes;
using MudBlazor;

namespace EnterprisePulse.Web.Services;

/// <summary>UI strings that are not field labels (those come from the API metadata).</summary>
public static class Texts
{
    private static readonly Dictionary<string, (string Tr, string En)> All = new()
    {
        ["app"] = ("EnterprisePulse", "EnterprisePulse"),
        ["login"] = ("Giriş yap", "Sign in"),
        ["logout"] = ("Çıkış yap", "Sign out"),
        ["email"] = ("E-posta", "E-mail"),
        ["password"] = ("Şifre", "Password"),
        ["code2fa"] = ("Doğrulama kodu", "Verification code"),
        ["forgot"] = ("Şifremi unuttum", "Forgot password"),
        ["sendLink"] = ("Bağlantı gönder", "Send link"),
        ["resetPassword"] = ("Şifreyi sıfırla", "Reset password"),
        ["newPassword"] = ("Yeni şifre", "New password"),
        ["currentPassword"] = ("Mevcut şifre", "Current password"),
        ["backToLogin"] = ("Girişe dön", "Back to sign in"),
        ["demoAccounts"] = ("Demo hesaplar", "Demo accounts"),
        ["search"] = ("Ara", "Search"),
        ["new"] = ("Yeni", "New"),
        ["save"] = ("Kaydet", "Save"),
        ["cancel"] = ("Vazgeç", "Cancel"),
        ["delete"] = ("Sil", "Delete"),
        ["edit"] = ("Düzenle", "Edit"),
        ["close"] = ("Kapat", "Close"),
        ["back"] = ("Geri", "Back"),
        ["refresh"] = ("Yenile", "Refresh"),
        ["export"] = ("Dışa aktar", "Export"),
        ["filters"] = ("Filtreler", "Filters"),
        ["clear"] = ("Temizle", "Clear"),
        ["all"] = ("Tümü", "All"),
        ["yes"] = ("Evet", "Yes"),
        ["no"] = ("Hayır", "No"),
        ["saved"] = ("Kaydedildi", "Saved"),
        ["deleted"] = ("Silindi", "Deleted"),
        ["confirmDelete"] = ("Bu kaydı silmek istediğinize emin misiniz?", "Are you sure you want to delete this record?"),
        ["noRecords"] = ("Kayıt bulunamadı", "No records found"),
        ["loading"] = ("Yükleniyor...", "Loading..."),
        ["details"] = ("Detaylar", "Details"),
        ["history"] = ("Geçmiş", "History"),
        ["files"] = ("Dosyalar", "Files"),
        ["upload"] = ("Dosya yükle", "Upload file"),
        ["notifications"] = ("Bildirimler", "Notifications"),
        ["markAllRead"] = ("Tümünü okundu yap", "Mark all as read"),
        ["profile"] = ("Profil", "Profile"),
        ["settings"] = ("Ayarlar", "Settings"),
        ["company"] = ("Şirket", "Company"),
        ["branch"] = ("Şube", "Branch"),
        ["allBranches"] = ("Tüm şubeler", "All branches"),
        ["dashboard"] = ("Gösterge Paneli", "Dashboard"),
        ["approve"] = ("Onayla", "Approve"),
        ["reject"] = ("Reddet", "Reject"),
        ["comment"] = ("Açıklama", "Comment"),
        ["rejectReason"] = ("Ret gerekçesi", "Rejection reason"),
        ["withdraw"] = ("Geri çek", "Withdraw"),
        ["inbox"] = ("Onay Kutum", "My approvals"),
        ["myRequests"] = ("Taleplerim", "My requests"),
        ["approvalFlow"] = ("Onay süreci", "Approval flow"),
        ["step"] = ("Adım", "Step"),
        ["startedBy"] = ("Başlatan", "Started by"),
        ["startedAt"] = ("Başlangıç", "Started"),
        ["status"] = ("Durum", "Status"),
        ["actions"] = ("İşlemler", "Actions"),
        ["permissions"] = ("İzinler", "Permissions"),
        ["roles"] = ("Roller", "Roles"),
        ["users"] = ("Kullanıcılar", "Users"),
        ["twoFactor"] = ("İki adımlı doğrulama", "Two-factor authentication"),
        ["enable"] = ("Etkinleştir", "Enable"),
        ["disable"] = ("Devre dışı bırak", "Disable"),
        ["language"] = ("Dil", "Language"),
        ["theme"] = ("Tema", "Theme"),
        ["notFound"] = ("Sayfa bulunamadı", "Page not found"),
        ["forbidden"] = ("Bu sayfa için yetkiniz yok", "You are not allowed to view this page"),
        ["required"] = ("Zorunlu alan", "Required"),
        ["system"] = ("Sistem", "System"),
        ["total"] = ("Toplam", "Total"),
        ["send"] = ("Gönder", "Send"),
        ["submit"] = ("Onaya gönder", "Submit for approval"),
        ["add"] = ("Ekle", "Add"),
        ["lines"] = ("Satırlar", "Lines"),
        ["print"] = ("Yazdır", "Print"),
    };

    public static string Get(string key, string locale) =>
        All.TryGetValue(key, out var t) ? (locale == "en" ? t.En : t.Tr) : key;
}

public static class Fmt
{
    public static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");
    public static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    public static CultureInfo Culture(string locale) => locale == "en" ? En : Tr;

    public static string Money(decimal? value, string? currency = null, string locale = "tr") =>
        value == null ? "" : value.Value.ToString("N2", Culture(locale)) + (currency != null ? " " + currency : "");

    public static string Date(DateTime? value) => value?.ToLocalTime().ToString("dd.MM.yyyy") ?? "";
    public static string DateTime(DateTime? value) => value?.ToLocalTime().ToString("dd.MM.yyyy HH:mm") ?? "";

    public static string Value(FieldMeta f, JsonNode? node, MetadataDocument meta, string locale, Dictionary<string, Dictionary<string, string>>? labels = null)
    {
        if (node == null) return "";
        var raw = node is JsonValue v && v.TryGetValue<string>(out var s) ? s : node.ToJsonString();
        if (f.Ref != null && labels != null && labels.TryGetValue(f.Name, out var map) && map.TryGetValue(raw, out var label)) return label;
        switch (f.Kind)
        {
            case "enum": return meta.EnumLabel(f.Enum, raw, locale);
            case "bool": return node.GetValue<bool>() ? Texts.Get("yes", locale) : Texts.Get("no", locale);
            case "date": return System.DateTime.TryParse(raw, out var d) ? d.ToString("dd.MM.yyyy") : raw;
            case "datetime": return System.DateTime.TryParse(raw, null, DateTimeStyles.RoundtripKind, out var dt) ? dt.ToLocalTime().ToString("dd.MM.yyyy HH:mm") : raw;
            case "decimal":
                return decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var m)
                    ? m.ToString(f.Scale is > 2 ? "N" + f.Scale : "N2", Culture(locale)) : raw;
            case "json": return node.ToJsonString();
            default: return raw;
        }
    }

    public static Color StatusColor(string? value) => value switch
    {
        "APPROVED" or "ACTIVE" or "COMPLETED" or "PAID" or "POSTED" or "DONE" or "DELIVERED" or "RECEIVED" or "CLOSED" or "ACCEPTED" or "HIRED" or "IN_USE" or "SENT" => Color.Success,
        "PENDING" or "SUBMITTED" or "IN_PROGRESS" or "OPEN" or "PARTIAL" or "PARTIALLY_PAID" or "PARTIALLY_RECEIVED" or "PARTIALLY_DELIVERED" or "SCHEDULED" or "IN_REVIEW" => Color.Warning,
        "REJECTED" or "CANCELLED" or "OVERDUE" or "LOST" or "FAILED" or "RETIRED" or "DISPOSED" or "TERMINATED" or "EXPIRED" or "VOID" => Color.Error,
        "DRAFT" => Color.Default,
        _ => Color.Info,
    };
}

/// <summary>Which navigation areas are live; every phase switches on its modules.</summary>
public static class Features
{
    public static readonly HashSet<string> Groups =
    [
        "Genel", "Onay Süreçleri", "Organizasyon", "Sistem",
    ];

    public static readonly HashSet<string> Pages =
    [
        "", "workflow/inbox", "roles", "settings",
    ];

    public static bool Enabled(NavGroupMeta g) => Groups.Contains(g.Label.Tr);
    public static bool Enabled(NavItemMeta i) => i.Resource != null || Pages.Contains(i.Page ?? "");
}

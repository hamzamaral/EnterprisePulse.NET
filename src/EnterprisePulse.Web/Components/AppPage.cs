using Microsoft.AspNetCore.Components;
using MudBlazor;
using EnterprisePulse.Web.Services;

namespace EnterprisePulse.Web.Components;

/// <summary>Base for every page/component: API access, current session, metadata and error-to-snackbar handling.</summary>
public abstract class AppPage : ComponentBase
{
    [Inject] protected ApiClient Api { get; set; } = null!;
    [Inject] protected Session Session { get; set; } = null!;
    [Inject] protected ISnackbar Snackbar { get; set; } = null!;
    [Inject] protected IDialogService Dialogs { get; set; } = null!;
    [Inject] protected NavigationManager Nav { get; set; } = null!;
    [Inject] private MetaCache MetaCache { get; set; } = null!;
    [CascadingParameter] private MetadataDocument? CascadedMeta { get; set; }

    /// <summary>Dialogs render outside the layout's cascading value, so they fall back to the cached document.</summary>
    protected MetadataDocument Meta => CascadedMeta ?? MetaCache.Document!;

    protected string Locale => Session.Locale;
    protected string L(string key) => Texts.Get(key, Session.Locale);
    protected string T(LocalizedText text) => text.For(Session.Locale);
    protected string T(string tr, string en) => Session.Locale == "en" ? en : tr;

    /// <summary>Runs an API call, shows its error as a snackbar and returns whether it succeeded.</summary>
    protected async Task<bool> Run(Func<Task> action, string? success = null)
    {
        try
        {
            await action();
            if (success != null) Snackbar.Add(success, Severity.Success);
            return true;
        }
        catch (ApiException ex)
        {
            if (ex.Status != 401) Snackbar.Add(ex.Message, Severity.Error);
            return false;
        }
    }

    protected async Task<bool> Confirm(string message, string? yes = null)
    {
        var result = await Dialogs.ShowMessageBox(T("Onay", "Confirm"), message, yesText: yes ?? L("yes"), cancelText: L("cancel"));
        return result == true;
    }
}

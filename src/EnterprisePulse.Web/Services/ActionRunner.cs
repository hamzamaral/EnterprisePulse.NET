using EnterprisePulse.Web.Components.Shared;
using MudBlazor;

namespace EnterprisePulse.Web.Services;

public static class ActionRunner
{
    public static async Task RunAsync(ResourceAction action, ActionContext ctx)
    {
        var title = action.Text(ctx.Locale);
        string? comment = null;
        if (action.Prompt != null || action.Confirm != null)
        {
            comment = await PromptDialog.AskAsync(ctx.Dialogs, title, action.Prompt, action.PromptRequired, action.Confirm, title, action.Color);
            if (comment == null) return;
        }
        try
        {
            if (action.Run != null)
            {
                await action.Run(ctx);
                return;
            }
            var url = action.Endpoint!.Replace("{id}", ctx.Id);
            if (action.Download)
            {
                await ctx.Api.DownloadAsync(url);
                return;
            }
            await ctx.Api.PostAsync(url, new { comment = string.IsNullOrEmpty(comment) ? null : comment });
            ctx.Snackbar.Add(Texts.Get("saved", ctx.Locale), Severity.Success);
            await ctx.Reload();
        }
        catch (ApiException ex)
        {
            ctx.Snackbar.Add(ex.Message, Severity.Error);
        }
    }
}

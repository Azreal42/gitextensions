using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;
using GitUIPluginInterfaces.BuildServerIntegration;

namespace GitExtensions.Linux.Desktop;

internal sealed partial class BuildServerIntegrationSettingsPage
{
    private Button? _jenkinsCredentials;
    private TextBlock? _jenkinsCredentialsNotice;
    private BuildServerCredentialsDialog? _credentialsDialog;
    private Task _credentialsCompletion = Task.CompletedTask;
    private bool _credentialsBusy, _credentialsClosing;
    private int _credentialsGeneration;
    internal Task CredentialsCompletion => _credentialsCompletion;

    private void InitializeJenkinsCredentials()
    {
        // Keep the original three-field Jenkins table intact. Linux authentication uses the common dialog explicitly.
        _jenkinsCredentials = new() { Name = "JenkinsCredentials", Content = Translations.Translate("Credentials..."),
            HorizontalAlignment = HorizontalAlignment.Left, Height = 25, MinHeight = 0 };
        _jenkinsCredentialsNotice = new() { Name = "JenkinsCredentialsNotice", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        Add(_jenkinsCredentials); Add(_jenkinsCredentialsNotice);
        _jenkinsCredentials.Click += (_, _) => _ = StartJenkinsCredentialsAsync();
    }

    private void InvalidateJenkinsCredentials()
    {
        ++_credentialsGeneration; _credentialsDialog?.Close(); UpdateJenkinsCredentials();
    }

    private void UpdateJenkinsCredentials()
    {
        if (_jenkinsCredentials is null || _jenkinsCredentialsNotice is null) { return; }
        bool active = Session.Read(CurrentScope, JenkinsSettings.ProviderKey) == JenkinsSettings.ProviderName;
        bool endpoint = false;
        try { _ = Session.Jenkins(CurrentScope).ServerUri; endpoint = true; } catch (ArgumentException) { }
        _jenkinsCredentials.IsVisible = _jenkinsCredentialsNotice.IsVisible = active;
        _jenkinsCredentials.IsEnabled = active && endpoint && Session.Loaded && CurrentScope != GitDistributedSettingsScope.Effective
            && !_credentialsBusy && !_credentialsClosing;
    }

    private Task StartJenkinsCredentialsAsync()
    {
        if (_jenkinsCredentials?.IsEnabled != true || _credentialsBusy || _credentialsClosing) { return Task.CompletedTask; }
        _credentialsBusy = true; _jenkinsCredentialsNotice!.Text = ""; UpdateJenkinsCredentials(); BusyChanged?.Invoke();
        return _credentialsCompletion = RunAsync();
        async Task RunAsync()
        {
            try
            {
                if (this.GetVisualRoot() is not Window owner || !owner.IsVisible) { return; }
                Uri endpoint = Session.Jenkins(CurrentScope).ServerUri;
                GitDistributedSettingsScope scope = CurrentScope; int generation = _credentialsGeneration;
                BuildServerCredentials initial = Session.CredentialDraft(JenkinsSettings.ProviderName, endpoint);
                BuildServerCredentialsDialog dialog = new(endpoint.AbsoluteUri, initial, Translations, allowBearerToken: false);
                _credentialsDialog = dialog;
                try { await dialog.ShowDialog<BuildServerCredentials?>(owner); }
                finally { if (ReferenceEquals(_credentialsDialog, dialog)) { _credentialsDialog = null; } }
                if (_credentialsClosing || generation != _credentialsGeneration || scope != CurrentScope || dialog.Result is null
                    || Session.Read(scope, JenkinsSettings.ProviderKey) != JenkinsSettings.ProviderName
                    || Session.Jenkins(scope).ServerUri != endpoint) { return; }
                Session.StageCredentials(JenkinsSettings.ProviderName, endpoint, dialog.Result);
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                if (!_credentialsClosing && IsVisible)
                { _jenkinsCredentialsNotice!.Text = Translations.Translate("Could not read Jenkins credentials. Check the server URL and profile access."); }
            }
            finally { _credentialsBusy = false; UpdateJenkinsCredentials(); BusyChanged?.Invoke(); }
        }
    }

    private Task BeginCredentialsClose()
    {
        if (!_credentialsClosing) { _credentialsClosing = true; InvalidateJenkinsCredentials(); }
        return _credentialsCompletion;
    }
}

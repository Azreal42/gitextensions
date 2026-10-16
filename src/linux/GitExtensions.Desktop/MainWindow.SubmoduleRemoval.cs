using Avalonia.Controls;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private Button? _removeSubmoduleButton;

    private Task ManageSubmodulesAsync(GitSubmodule? selected = null)
    {
        if (_repository is not { IsBare: false } repository || _busy) { return Task.CompletedTask; }
        if (selected is null && _navigation.SelectedItem is TreeViewItem item && _navigationKeys.GetValueOrDefault(item) is string key)
        { selected = _navigationSubmodules.FirstOrDefault(module => key == "submodule:" + module.Path); }
        return ShowRepositoryDialogAsync(new SubmoduleRemovalDialog(repository, selected,
            afterSubmoduleUpdateAsync: (owner, module, token) => AfterSubmoduleUpdateAsync(module, owner, token),
            resolveConflictsAsync: ResolveWorkflowConflictsAsync));
    }

    private Button BuildSubmoduleRemovalButton()
    {
        Button button = Button("Remove", RemoveSubmoduleAsync);
        _removeSubmoduleButton = button;
        button.Name = "RemoveSubmoduleRegistration";
        button.Tag = true;
        _submodules.SelectionChanged += (_, _) => RefreshSubmoduleRemovalButton();
        RefreshSubmoduleRemovalButton();
        return button;
    }

    private void RefreshSubmoduleRemovalButton()
    {
        if (_removeSubmoduleButton is not null)
        { _removeSubmoduleButton.IsEnabled = !_busy && _repository is { IsBare: false } && _submodules.SelectedItem is GitSubmodule; }
    }

    private Task RemoveSubmoduleAsync()
        => _repository is null || _busy || _repository.IsBare || _submodules.SelectedItem is not GitSubmodule selected
            ? Task.CompletedTask
            : ManageSubmodulesAsync(selected);
}

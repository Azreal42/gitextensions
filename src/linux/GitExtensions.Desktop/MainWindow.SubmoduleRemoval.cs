using Avalonia.Controls;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private Button? _removeSubmoduleButton;

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
            : ShowRepositoryDialogAsync(new SubmoduleRemovalDialog(_repository, selected));
}

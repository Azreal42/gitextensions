using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace GitExtensions.Linux.Desktop;

internal static class RebasePlanDialog
{
    public static Task<GitRebasePlan?> ShowAsync(Window owner, GitRebasePlan plan)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(plan);
        ObservableCollection<PlanRow> rows = new(plan.Steps.Select(step => new PlanRow(step)));
        Window dialog = new()
        {
            Title = "Interactive rebase — Git Extensions",
            Width = 800,
            Height = 540,
            MinWidth = 660,
            MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        ListBox commits = new() { ItemsSource = rows, SelectionMode = SelectionMode.Single };
        commits.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(Layoutable.HeightProperty, 24d),
                new Setter(Layoutable.MinHeightProperty, 24d),
                new Setter(Avalonia.Controls.Primitives.TemplatedControl.PaddingProperty, new Thickness(0)),
                new Setter(Layoutable.MarginProperty, new Thickness(0)),
                new Setter(ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch),
            },
        });
        Button up = CompactButton("↑ Move up");
        Button down = CompactButton("↓ Move down");
        Button reset = CompactButton("Reset plan");
        Button cancel = CompactButton("Cancel");
        cancel.IsCancel = true;
        Button accept = CompactButton("Rebase");
        accept.IsDefault = true;
        TextBox message = new()
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 82,
            MinHeight = 24,
            Padding = new Thickness(4, 2),
            IsEnabled = false,
        };
        TextBlock messageLabel = new() { Text = "Commit message", VerticalAlignment = VerticalAlignment.Center };
        TextBlock messageHint = new() { Text = "Select Reword or Squash to edit its message.", FontSize = 11, Opacity = 0.8 };
        TextBlock validation = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Firebrick, MinHeight = 18 };
        TextBlock count = new() { VerticalAlignment = VerticalAlignment.Center };
        bool loadingMessage = false;

        commits.ItemTemplate = new FuncDataTemplate<PlanRow>((row, _) =>
        {
            if (row is null)
            {
                return new TextBlock();
            }

            Grid cells = new() { ColumnDefinitions = new ColumnDefinitions("32,110,90,*"), Height = 24 };
            row.NumberLabel = new TextBlock { Text = (rows.IndexOf(row) + 1).ToString(), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            ComboBox action = new()
            {
                ItemsSource = Enum.GetValues<GitRebaseAction>(),
                SelectedItem = row.Step.Action,
                Height = 22,
                MinHeight = 22,
                Padding = new Thickness(3, 0),
                Margin = new Thickness(0, 1, 4, 1),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            TextBlock hash = new()
            {
                Text = row.Step.Hash[..Math.Min(9, row.Step.Hash.Length)],
                FontFamily = new FontFamily("DejaVu Sans Mono, monospace"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0),
            };
            row.SubjectLabel = new TextBlock
            {
                Text = Subject(row.Step),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0),
                Opacity = row.Step.Action == GitRebaseAction.Drop ? 0.55 : 1,
            };
            ToolTip.SetTip(row.SubjectLabel, row.Step.Subject);
            action.SelectionChanged += (_, _) =>
            {
                if (action.SelectedItem is GitRebaseAction selected && selected != row.Step.Action)
                {
                    row.Step = row.Step with { Action = selected };
                    if (selected == GitRebaseAction.Reword && string.IsNullOrWhiteSpace(row.Step.Message))
                    {
                        row.Step = row.Step with { Message = row.Step.Subject };
                    }

                    UpdateSubject(row);
                    commits.SelectedItem = row;
                    if (ReferenceEquals(commits.SelectedItem, row))
                    {
                        LoadMessage();
                    }

                    Validate();
                }
            };
            Grid.SetColumn(action, 1);
            Grid.SetColumn(hash, 2);
            Grid.SetColumn(row.SubjectLabel, 3);
            cells.Children.Add(row.NumberLabel);
            cells.Children.Add(action);
            cells.Children.Add(hash);
            cells.Children.Add(row.SubjectLabel);
            return cells;
        });
        commits.SelectionChanged += (_, _) => LoadMessage();
        message.PropertyChanged += (_, change) =>
        {
            if (change.Property == TextBox.TextProperty && !loadingMessage && commits.SelectedItem is PlanRow selected && selected.Step.Action is GitRebaseAction.Reword or GitRebaseAction.Squash)
            {
                selected.Step = selected.Step with { Message = message.Text };
                UpdateSubject(selected);
                Validate();
            }
        };
        up.Click += (_, _) => Move(-1);
        down.Click += (_, _) => Move(1);
        reset.Click += (_, _) =>
        {
            rows.Clear();
            foreach (GitRebaseStep step in plan.Steps)
            {
                rows.Add(new PlanRow(step));
            }

            commits.SelectedIndex = rows.Count > 0 ? 0 : -1;
            Validate();
        };
        cancel.Click += (_, _) => dialog.Close(null);
        accept.Click += (_, _) =>
        {
            if (Validate())
            {
                dialog.Close(plan with { Steps = rows.Select(row => row.Step).ToArray() });
            }
        };
        dialog.KeyDown += (_, args) =>
        {
            if (args.KeyModifiers == KeyModifiers.Alt && args.Key is Key.Up or Key.Down)
            {
                Move(args.Key == Key.Up ? -1 : 1);
                args.Handled = true;
            }
        };

        Grid header = new() { ColumnDefinitions = new ColumnDefinitions("32,110,90,*"), Height = 24, Background = new SolidColorBrush(Color.Parse("#E8E8E8")) };
        string[] headings = ["#", "Action", "Commit", "Message"];
        for (int column = 0; column < headings.Length; column++)
        {
            TextBlock label = new() { Text = headings[column], FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0) };
            Grid.SetColumn(label, column);
            header.Children.Add(label);
        }

        DockPanel table = new();
        DockPanel.SetDock(header, Dock.Top);
        table.Children.Add(header);
        table.Children.Add(commits);
        Grid content = new() { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto,Auto,Auto"), Margin = new Thickness(10), RowSpacing = 6 };
        TextBlock range = new()
        {
            Text = $"Replay {plan.Steps.Count} commits onto {ShortHash(plan.Upstream)}. Oldest commit first.",
            TextWrapping = TextWrapping.Wrap,
        };
        TextBlock warning = new()
        {
            Text = "This rewrites commit history. Rewritten commits get new IDs; Drop removes a commit from this branch.",
            TextWrapping = TextWrapping.Wrap,
        };
        Border tableBorder = new() { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Child = table };
        StackPanel ordering = new() { Orientation = Orientation.Horizontal, Spacing = 5, Children = { up, down, reset, count } };
        StackPanel editing = new() { Spacing = 3, Children = { messageLabel, message, messageHint } };
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 6, Children = { cancel, accept } };
        Control[] controls = [range, warning, tableBorder, ordering, editing, validation, buttons];
        for (int index = 0; index < controls.Length; index++)
        {
            Grid.SetRow(controls[index], index);
            content.Children.Add(controls[index]);
        }

        dialog.Content = content;
        dialog.Opened += (_, _) =>
        {
            commits.SelectedIndex = rows.Count > 0 ? 0 : -1;
            commits.Focus();
            Validate();
        };
        Validate();
        return dialog.ShowDialog<GitRebasePlan?>(owner);

        void Move(int delta)
        {
            int index = commits.SelectedIndex;
            int next = index + delta;
            if (index < 0 || next < 0 || next >= rows.Count)
            {
                return;
            }

            PlanRow selected = rows[index];
            rows.Move(index, next);
            commits.SelectedItem = selected;
            foreach (PlanRow row in rows)
            {
                if (row.NumberLabel is not null)
                {
                    row.NumberLabel.Text = (rows.IndexOf(row) + 1).ToString();
                }
            }

            commits.ScrollIntoView(selected);
            LoadMessage();
            Validate();
        }

        void LoadMessage()
        {
            loadingMessage = true;
            PlanRow? selected = commits.SelectedItem as PlanRow;
            bool editable = selected?.Step.Action is GitRebaseAction.Reword or GitRebaseAction.Squash;
            message.IsEnabled = editable;
            message.Text = editable ? selected?.Step.Message ?? "" : "";
            message.Watermark = selected?.Step.Action == GitRebaseAction.Squash ? "Leave empty to keep Git's combined message" : "New commit message";
            messageLabel.Text = selected?.Step.Action == GitRebaseAction.Squash ? "Combined commit message (optional)" : "Commit message";
            messageHint.Text = selected?.Step.Action switch
            {
                GitRebaseAction.Reword => "A message is required for Reword.",
                GitRebaseAction.Squash => "Squash combines this commit with the preceding retained commit.",
                GitRebaseAction.Edit => "Git will pause here so you can amend the commit, then continue the rebase.",
                GitRebaseAction.Fixup => "Fixup combines changes into the preceding retained commit and keeps its message.",
                GitRebaseAction.Drop => "This commit will be removed from the rewritten branch.",
                _ => "Select Reword or Squash to edit its message. Alt+Up / Alt+Down changes commit order.",
            };
            loadingMessage = false;
            up.IsEnabled = commits.SelectedIndex > 0;
            down.IsEnabled = commits.SelectedIndex >= 0 && commits.SelectedIndex < rows.Count - 1;
        }

        bool Validate()
        {
            GitRebaseStep? first = rows.Select(row => row.Step).FirstOrDefault(step => step.Action != GitRebaseAction.Drop);
            string? error = first?.Action is GitRebaseAction.Squash or GitRebaseAction.Fixup
                ? "The first retained commit must be Pick, Reword or Edit."
                : rows.Any(row => row.Step.Action == GitRebaseAction.Reword && string.IsNullOrWhiteSpace(row.Step.Message))
                    ? "Enter a message for each commit marked Reword."
                    : rows.Any(row => row.Step.Message?.Contains('\0') == true)
                        ? "Commit messages cannot contain NUL characters." : null;
            validation.Text = error ?? "";
            accept.IsEnabled = rows.Count > 0 && error is null;
            count.Text = $"  {rows.Count(row => row.Step.Action != GitRebaseAction.Drop)} retained, {rows.Count(row => row.Step.Action == GitRebaseAction.Drop)} dropped";
            return accept.IsEnabled;
        }
    }

    private static Button CompactButton(string label) => new() { Content = label, Height = 24, MinHeight = 24, Padding = new Thickness(7, 1) };
    private static string ShortHash(string hash) => hash[..Math.Min(9, hash.Length)];
    private static string Subject(GitRebaseStep step) => step.Action == GitRebaseAction.Reword && !string.IsNullOrWhiteSpace(step.Message)
        ? step.Message.Split('\n')[0].TrimEnd('\r') : step.Subject;
    private static void UpdateSubject(PlanRow row)
    {
        if (row.SubjectLabel is not null)
        {
            row.SubjectLabel.Text = Subject(row.Step);
            row.SubjectLabel.Opacity = row.Step.Action == GitRebaseAction.Drop ? 0.55 : 1;
        }
    }

    private sealed class PlanRow(GitRebaseStep step)
    {
        public GitRebaseStep Step { get; set; } = step;
        public TextBlock? NumberLabel { get; set; }
        public TextBlock? SubjectLabel { get; set; }
    }
}

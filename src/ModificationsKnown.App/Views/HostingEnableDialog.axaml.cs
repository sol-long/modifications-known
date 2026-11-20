using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ModificationsKnown.App.Services;
using ModificationsKnown.Core.Saves;
using ModificationsKnown.Infrastructure.Saves;

namespace ModificationsKnown.App.Views;

/// <summary>
/// Re-enable dialog: the receiving instance is fixed to the active one; the
/// user chooses between importing the live folder as a new group and
/// overwriting an existing group. Closing any other way returns null.
/// </summary>
public partial class HostingEnableDialog : DialogWindow
{
    private IReadOnlyList<SaveGroup> _groups = [];
    private bool _importMode = true;

    public HostingEnableDialog()
    {
        InitializeComponent();
        KeyDown += OnWindowKeyDown;
    }

    public HostingEnableDialog(
        string installationLabel,
        string liveDirectory,
        int fileCount,
        IReadOnlyList<SaveGroup> groups,
        Guid? selectedGroupId,
        string suggestedName)
        : this()
    {
        _groups = groups;
        SummaryText.Text =
            $"游戏目录里有 {fileCount} 个文件。MK 会先把这些文件复制到一个存档组并逐文件校验，" +
            "成功后才切换链接。取消不会修改任何文件。";
        DirectoryText.Text = liveDirectory;
        InstanceText.Text =
            $"接收实例：{installationLabel}（总是当前选中的版本；覆盖只列出该实例的存档组）";

        NameBox.Text = suggestedName;
        GroupCombo.ItemsSource = groups;
        GroupCombo.SelectedItem = groups.FirstOrDefault(group => group.Id == selectedGroupId)
                                  ?? groups.FirstOrDefault();
        BackupCheck.IsChecked = true;

        Opened += (_, _) =>
        {
            _importMode = ImportRadio.IsChecked != false;
            UpdateMode();
            if (_importMode) NameBox.Focus();
        };
    }

    private void OnChoiceChanged(object? sender, RoutedEventArgs e)
    {
        // Read the radio that raised the event, not the other radio. During a
        // group switch Avalonia can raise Checked before the previously checked
        // radio reports false; reading ImportRadio here would keep the dialog in
        // the old mode and leave the target controls disabled.
        if (sender is RadioButton { IsChecked: true } radio)
        {
            _importMode = ReferenceEquals(radio, ImportRadio);
            UpdateMode();
        }
    }

    private void OnNameChanged(object? sender, TextChangedEventArgs e) => UpdateMode();

    private void OnGroupChanged(object? sender, SelectionChangedEventArgs e) => UpdateMode();

    private void UpdateMode()
    {
        if (NameBox is null || GroupCombo is null || BackupCheck is null || ConfirmButton is null) return;

        var hasGroups = _groups.Count > 0;
        if (!hasGroups && !_importMode)
            _importMode = true;

        var import = _importMode;
        OverwriteRadio.IsEnabled = hasGroups;
        NameBox.IsEnabled = import;
        GroupCombo.IsEnabled = !import && hasGroups;
        BackupCheck.IsEnabled = !import && hasGroups;
        ConfirmButton.IsEnabled = import
            ? !string.IsNullOrWhiteSpace(NameBox.Text)
            : hasGroups && GroupCombo.SelectedItem is SaveGroup;

        HintText.Text = import
            ? "导入会把当前游戏目录复制成新组，并把该组设为选定组；原目录保留为备份。"
            : !hasGroups
                ? "该实例还没有存档组；请改用导入。"
                : "覆盖会用游戏目录里的内容替换目标组；目标组的名称、ID 与创建时间保持不变。";
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        if (_importMode)
        {
            var name = NameBox.Text?.Trim();
            if (string.IsNullOrEmpty(name)) return;
            Close(new HostingEnableDecision(
                HostingEnableChoice.ImportIntoNewGroup, name, null, false));
            return;
        }

        if (_groups.Count == 0 || GroupCombo.SelectedItem is not SaveGroup target) return;
        Close(new HostingEnableDecision(
            HostingEnableChoice.OverwriteExistingGroup,
            null,
            target.Id,
            BackupCheck.IsChecked == true));
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close(null);
        }
    }
}

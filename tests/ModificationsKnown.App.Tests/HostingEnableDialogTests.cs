using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using ModificationsKnown.App.Services;
using ModificationsKnown.App.Views;
using ModificationsKnown.Core.Saves;
using ModificationsKnown.Infrastructure.Saves;

namespace ModificationsKnown.App.Tests;

/// <summary>
/// Real production dialog driven through headless pointer input. These tests
/// prove the mode controls are wired to the same state as the submitted
/// decision; they do not claim pixel, dropdown-popup or keyboard-focus
/// coverage.
/// </summary>
public sealed class HostingEnableDialogTests
{
    [AvaloniaFact]
    public async Task InitialImportMode_EnablesNameAndDisablesTargetControls()
    {
        var groups = Groups();
        var result = await RunDialogAsync(groups, context =>
        {
            Assert.True(context.Import.IsChecked);
            Assert.True(context.NameBox.IsEnabled);
            Assert.False(context.GroupCombo.IsEnabled);
            Assert.False(context.BackupCheck.IsEnabled);
            Assert.True(context.Confirm.IsEnabled);
            Assert.Equal(groups[0].Id, ((SaveGroup)context.GroupCombo.SelectedItem!).Id);
        });

        Assert.Null(result);
    }

    [AvaloniaFact]
    public async Task SelectingOverwriteWithGroups_EnablesTargetAndBackup()
    {
        var groups = Groups();
        await RunDialogAsync(groups, context =>
        {
            Click(context.Dialog, context.Overwrite);

            Assert.True(context.Overwrite.IsChecked);
            Assert.False(context.Import.IsChecked);
            Assert.True(context.GroupCombo.IsEnabled);
            Assert.True(context.BackupCheck.IsEnabled);
            Assert.True(context.Confirm.IsEnabled);
            Assert.Equal(groups[0].Id, ((SaveGroup)context.GroupCombo.SelectedItem!).Id);
        });
    }


    [AvaloniaFact]
    public async Task KeyboardSelectingOverwrite_AppliesTheSameModeState()
    {
        await RunDialogAsync(Groups(), context =>
        {
            context.Overwrite.Focus();
            Dispatcher.UIThread.RunJobs();
            context.Dialog.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            context.Dialog.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();

            Assert.True(context.Overwrite.IsChecked);
            Assert.True(context.GroupCombo.IsEnabled);
            Assert.True(context.BackupCheck.IsEnabled);
            Assert.True(context.Confirm.IsEnabled);
        });
    }

    [AvaloniaFact]
    public async Task OverwriteWithoutSelectedTarget_DisablesConfirm()
    {
        await RunDialogAsync(Groups(), context =>
        {
            Click(context.Dialog, context.Overwrite);
            context.GroupCombo.SelectedItem = null;
            Dispatcher.UIThread.RunJobs();
            Assert.False(context.Confirm.IsEnabled);

            context.GroupCombo.SelectedItem = ((IReadOnlyList<SaveGroup>)context.GroupCombo.ItemsSource!)[0];
            Dispatcher.UIThread.RunJobs();
            Assert.True(context.Confirm.IsEnabled);
        });
    }
    [AvaloniaFact]
    public async Task SwitchingBackToImport_DisablesTargetControlsAndKeepsName()
    {
        var groups = Groups();
        await RunDialogAsync(groups, context =>
        {
            context.NameBox.Text = "保留的名字";
            Click(context.Dialog, context.Overwrite);
            Click(context.Dialog, context.Import);

            Assert.True(context.Import.IsChecked);
            Assert.Equal("保留的名字", context.NameBox.Text);
            Assert.True(context.NameBox.IsEnabled);
            Assert.False(context.GroupCombo.IsEnabled);
            Assert.False(context.BackupCheck.IsEnabled);
            Assert.True(context.Confirm.IsEnabled);
        });
    }

    [AvaloniaFact]
    public async Task NoGroups_DisablesOverwriteAndKeepsImportUsable()
    {
        await RunDialogAsync([], context =>
        {
            Assert.False(context.Overwrite.IsEnabled);
            Assert.False(context.GroupCombo.IsEnabled);
            Assert.False(context.BackupCheck.IsEnabled);
            Assert.True(context.Import.IsChecked);
            Assert.True(context.Confirm.IsEnabled);

            Click(context.Dialog, context.Overwrite);

            Assert.True(context.Import.IsChecked);
            Assert.False(context.Overwrite.IsChecked);
            Assert.True(context.Confirm.IsEnabled);
        });
    }

    [AvaloniaFact]
    public async Task TargetAndBackupSelectionsSurviveModeRoundTrip()
    {
        var groups = Groups();
        await RunDialogAsync(groups, context =>
        {
            Click(context.Dialog, context.Overwrite);
            context.GroupCombo.SelectedItem = groups[1];
            Click(context.Dialog, context.BackupCheck);
            Assert.False(context.BackupCheck.IsChecked);
            Click(context.Dialog, context.BackupCheck);
            Assert.True(context.BackupCheck.IsChecked);
            Click(context.Dialog, context.BackupCheck);
            Assert.False(context.BackupCheck.IsChecked);

            Click(context.Dialog, context.Import);
            Click(context.Dialog, context.Overwrite);

            Assert.Equal(groups[1].Id, ((SaveGroup)context.GroupCombo.SelectedItem!).Id);
            Assert.False(context.BackupCheck.IsChecked);
            Assert.True(context.Confirm.IsEnabled);
        });
    }

    [AvaloniaFact]
    public async Task EmptyImportName_DisablesConfirm()
    {
        await RunDialogAsync(
            Groups(),
            context =>
            {
                Assert.True(context.Import.IsChecked);
                Assert.Equal(string.Empty, context.NameBox.Text);
                Assert.False(context.Confirm.IsEnabled);
            },
            suggestedName: string.Empty);
    }

    [AvaloniaFact]
    public async Task OverwriteSubmit_ReturnsSelectedTargetAndBackupValue()
    {
        var groups = Groups();
        var result = await RunDialogAsync(groups, context =>
        {
            Click(context.Dialog, context.Overwrite);
            context.GroupCombo.SelectedItem = groups[1];
            Click(context.Dialog, context.BackupCheck);
            Click(context.Dialog, context.Confirm);
        });

        Assert.NotNull(result);
        Assert.Equal(HostingEnableChoice.OverwriteExistingGroup, result.Choice);
        Assert.Equal(groups[1].Id, result.TargetGroupId);
        Assert.False(result.BackupTargetGroup);
        Assert.Null(result.NewGroupName);
    }

    [AvaloniaFact]
    public async Task ImportSubmit_ReturnsActualName()
    {
        var result = await RunDialogAsync(Groups(), context =>
        {
            context.NameBox.Text = "实际导入名";
            Click(context.Dialog, context.Confirm);
        });

        Assert.NotNull(result);
        Assert.Equal(HostingEnableChoice.ImportIntoNewGroup, result.Choice);
        Assert.Equal("实际导入名", result.NewGroupName);
        Assert.Null(result.TargetGroupId);
        Assert.False(result.BackupTargetGroup);
    }

    [AvaloniaFact]
    public async Task Cancel_ReturnsNull()
    {
        var result = await RunDialogAsync(Groups(), context =>
        {
            Click(context.Dialog, context.Cancel);
        });

        Assert.Null(result);
    }

    private static IReadOnlyList<SaveGroup> Groups() =>
    [
        new(Guid.NewGuid(), "当前组", DateTimeOffset.UtcNow, true, @"C:\groups\a"),
        new(Guid.NewGuid(), "备份组", DateTimeOffset.UtcNow, false, @"C:\groups\b")
    ];

    private static void Click(Window window, Control control)
    {
        var point = control.TranslatePoint(
            new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window);
        Assert.NotNull(point);
        window.MouseDown(point.Value, MouseButton.Left);
        window.MouseUp(point.Value, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task<HostingEnableDecision?> RunDialogAsync(
        IReadOnlyList<SaveGroup> groups,
        Action<DialogContext> body,
        string suggestedName = "导入组")
    {
        var owner = new Window { Width = 900, Height = 700 };
        owner.Show();
        Dispatcher.UIThread.RunJobs();

        var dialog = new HostingEnableDialog(
            "测试实例", @"C:\live", 2, groups, groups.FirstOrDefault()?.Id, suggestedName);
        var result = dialog.ShowDialog<HostingEnableDecision?>(owner);
        Dispatcher.UIThread.RunJobs();

        var context = new DialogContext(
            dialog,
            dialog.FindControl<RadioButton>("ImportRadio")!,
            dialog.FindControl<RadioButton>("OverwriteRadio")!,
            dialog.FindControl<TextBox>("NameBox")!,
            dialog.FindControl<ComboBox>("GroupCombo")!,
            dialog.FindControl<CheckBox>("BackupCheck")!,
            dialog.FindControl<Button>("ConfirmButton")!,
            dialog.FindControl<Button>("CancelButton")!);

        try
        {
            body(context);
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            if (dialog.IsVisible)
                dialog.Close(null);
            Dispatcher.UIThread.RunJobs();
        }

        var decision = await result;
        owner.Close();
        Dispatcher.UIThread.RunJobs();
        return decision;
    }

    private sealed record DialogContext(
        HostingEnableDialog Dialog,
        RadioButton Import,
        RadioButton Overwrite,
        TextBox NameBox,
        ComboBox GroupCombo,
        CheckBox BackupCheck,
        Button Confirm,
        Button Cancel);
}

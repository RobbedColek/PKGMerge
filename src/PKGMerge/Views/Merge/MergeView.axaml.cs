using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using PKGMerge.Core;
using PKGMerge.ViewModels.Merge;

namespace PKGMerge.Views.Merge;

public partial class MergeView : UserControl
{
    public MergeView()
    {
        InitializeComponent();

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => SetDropHighlight(false));
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private MergeViewModel? ViewModel => DataContext as MergeViewModel;

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        bool accepted = ViewModel is { IsRunning: false } && GetDroppedPkg(e) is not null;

        e.DragEffects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        SetDropHighlight(accepted);
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        SetDropHighlight(false);

        if (GetDroppedPkg(e) is { } path && ViewModel is { IsRunning: false } viewModel)
            viewModel.OpenSource(path);
    }

    private static string? GetDroppedPkg(DragEventArgs e) =>
        e.DataTransfer.TryGetFiles()?
            .Select(item => item.TryGetLocalPath())
            .FirstOrDefault(path => path is not null && PkgFile.HasPkgExtension(path));

    private void SetDropHighlight(bool highlighted) => FilesCard.Classes.Set("dropTarget", highlighted);
}

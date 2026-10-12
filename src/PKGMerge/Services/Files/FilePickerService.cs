using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace PKGMerge.Services.Files;

public interface IFilePickerService
{
    Task<string?> PickRootPackageAsync(string? startFolder);

    Task<string?> PickScFileAsync(string? startFolder);

    Task<string?> PickOutputFileAsync(string? suggestedPath);
}

public sealed class FilePickerService : IFilePickerService
{
    private static readonly FilePickerFileType PkgFileType = new("PKG file (*.pkg)") { Patterns = ["*.pkg"] };
    private static readonly FilePickerFileType ScFileType = new("PS5 _sc.pkg file (*_sc.pkg)") { Patterns = ["*_sc.pkg"] };

    public Task<string?> PickRootPackageAsync(string? startFolder) => PickFileAsync(
        "Select the root PKG file (<ID>_0.pkg or <ID>.pkg for a PS5 package that isn't split)",
        [PkgFileType, FilePickerFileTypes.All],
        startFolder);

    public Task<string?> PickScFileAsync(string? startFolder) =>
        PickFileAsync("Select the PS5 _sc.pkg file", [ScFileType, PkgFileType], startFolder);

    public async Task<string?> PickOutputFileAsync(string? suggestedPath)
    {
        if (GetStorageProvider() is not { } storage)
            return null;

        IStorageFile? file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save merged PKG as",
            SuggestedFileName = string.IsNullOrWhiteSpace(suggestedPath) ? null : Path.GetFileName(suggestedPath),
            SuggestedStartLocation = await TryGetFolderAsync(storage, Path.GetDirectoryName(suggestedPath)),
            DefaultExtension = "pkg",
            FileTypeChoices = [PkgFileType],
            // Overwriting is confirmed when the merge starts, no need to ask user now
            ShowOverwritePrompt = false,
        });

        return file?.TryGetLocalPath();
    }

    private static async Task<string?> PickFileAsync(string title, IReadOnlyList<FilePickerFileType> filter, string? startFolder)
    {
        if (GetStorageProvider() is not { } storage)
            return null;

        IReadOnlyList<IStorageFile> files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = filter,
            SuggestedStartLocation = await TryGetFolderAsync(storage, startFolder),
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private static IStorageProvider? GetStorageProvider() =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window }
            ? TopLevel.GetTopLevel(window)?.StorageProvider
            : null;

    private static async Task<IStorageFolder?> TryGetFolderAsync(IStorageProvider storage, string? folder) =>
        !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder)
            ? await storage.TryGetFolderFromPathAsync(folder)
            : null;
}

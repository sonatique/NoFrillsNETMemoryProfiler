using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using NoFrills.MemoryProfiler.App.ViewModels;

namespace NoFrills.MemoryProfiler.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel m_model = new();

    public MainWindow()
    {
        InitializeComponent();

        m_model.PickDumpFile = PickDumpFile;
        m_model.PickTraceFile = PickTraceFile;
        m_model.PickSaveFile = PickSaveFile;
        m_model.PickFolder = PickFolder;
        DataContext = m_model;

        OpenButton.Click += async (_, _) => await m_model.OpenDumpAsync(asBaseline: false);
        CompareButton.Click += async (_, _) => await m_model.OpenDumpAsync(asBaseline: true);

        m_model.RefreshProcessesCommand.Execute(null);
    }

    private async Task<string?> PickDumpFile(bool asBaseline)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = asBaseline ? "Open the dump to compare with" : "Open a heap dump",
            AllowMultiple = false,
            FileTypeFilter = new List<FilePickerFileType>
            {
                new("Heap dumps") { Patterns = new[] { "*.gcdump", "*.dmp" } },
                new("All files") { Patterns = new[] { "*" } }
            }
        });

        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    private async Task<string?> PickTraceFile()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open a GC recording",
            AllowMultiple = false,
            FileTypeFilter = new List<FilePickerFileType>
            {
                new("GC recordings") { Patterns = new[] { "*.nettrace" } },
                new("All files") { Patterns = new[] { "*" } }
            }
        });

        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    private async Task<string?> PickFolder(string current)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Where to store the dumps",
            AllowMultiple = false,
            SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(current)
        });

        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    private async Task<string?> PickSaveFile(string suggestedName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save the analysis",
            SuggestedFileName = suggestedName,
            DefaultExtension = "md",
            FileTypeChoices = new List<FilePickerFileType>
            {
                new("Markdown") { Patterns = new[] { "*.md" } }
            }
        });

        return file?.TryGetLocalPath();
    }
}

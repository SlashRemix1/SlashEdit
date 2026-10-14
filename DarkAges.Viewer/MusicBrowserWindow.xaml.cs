using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using DarkAges.Formats.Catalog;
using DarkAges.Formats.Data;
using DarkAges.Formats.IO;
using DarkAges.Viewer.Services;
using Microsoft.Win32;
using NAudio.Wave;

namespace DarkAges.Viewer;

public partial class MusicBrowserWindow : Window
{
    private readonly ArchiveCache? _cache;
    private readonly List<CatalogEntry> _catalogs;
    private readonly ObservableCollection<FileRow> _files = [];
    private readonly List<FileRow> _all = [];
    private IWavePlayer? _player;
    private WaveStream? _wave;

    public MusicBrowserWindow(ArchiveCache? cache, IReadOnlyList<CatalogEntry> catalogs)
    {
        InitializeComponent();
        _cache = cache;
        _catalogs = catalogs.ToList();
        FileList.ItemsSource = _files;
        Closed += (_, _) => Stop();
        LoadFiles();
    }

    private void LoadFiles()
    {
        _all.Clear();
        if (_cache is null)
        {
            StatusText.Text = "Load a client first.";
            return;
        }

        foreach (CatalogEntry catalog in _catalogs)
        {
            IArchive archive;
            try
            {
                archive = _cache.Get(catalog);
            }
            catch
            {
                continue;
            }

            foreach (ArchiveEntry entry in archive.Entries)
            {
                string ext = entry.Extension;
                if (!ext.Equals(".mp3", StringComparison.OrdinalIgnoreCase)
                    && !ext.Equals(".wav", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                _all.Add(new FileRow
                {
                    Name = entry.Name,
                    Catalog = catalog,
                    Entry = entry
                });
            }
        }

        ApplyFilter();
        StatusText.Text = _all.Count == 0
            ? "No .mp3 / .wav files in this client."
            : $"{_all.Count} audio files.";
    }

    private void FilterChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        string q = FilterBox.Text.Trim();
        _files.Clear();
        foreach (FileRow row in _all.Where(r => r.Matches(q)))
        {
            _files.Add(row);
        }
    }

    private void PlayClicked(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is not FileRow row || _cache is null)
        {
            return;
        }

        try
        {
            Stop();
            IArchive archive = _cache.Get(row.Catalog);
            ArchiveEntry? entry = archive.GetEntry(row.Name) ?? row.Entry;
            if (entry is null)
            {
                return;
            }

            byte[] data = entry.ReadAllBytes();
            var ms = new MemoryStream(data);
            _wave = row.Name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
                ? new WaveFileReader(ms)
                : new Mp3FileReader(ms);
            _player = new WaveOutEvent();
            _player.Init(_wave);
            _player.Play();
            StatusText.Text = $"Playing {row.Name}";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Play");
        }
    }

    private void StopClicked(object sender, RoutedEventArgs e) => Stop();

    private void Stop()
    {
        _player?.Stop();
        _player?.Dispose();
        _wave?.Dispose();
        _player = null;
        _wave = null;
    }

    private void ReplaceClicked(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is not FileRow row || _cache is null)
        {
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Audio|*.mp3;*.wav|All files|*.*"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            DatSave.Write(_cache, row.Catalog, row.Name, File.ReadAllBytes(dialog.FileName));
            StatusText.Text = $"Replaced {row.Name} in {row.Catalog.DisplayName}.";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Replace audio");
        }
    }

    private void OpenButtonMenu(object sender, RoutedEventArgs e) => ButtonMenu.Open(sender);

    private void ExportDataClicked(object sender, RoutedEventArgs e)
    {
        TableDump dump = TableDump.Create(
            "Music",
            ["Name", "Archive", "Size"],
            _files.Select(f => (IReadOnlyList<object?>)new object?[]
            {
                f.Name, f.Catalog.DisplayName, f.Entry?.Size ?? 0
            }));
        TableExport.Save(this, "music", dump, sender, StatusText);
    }

    private void ExtractClicked(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is not FileRow row || _cache is null)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = row.Name };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            IArchive archive = _cache.Get(row.Catalog);
            ArchiveEntry? entry = archive.GetEntry(row.Name) ?? row.Entry;
            if (entry is null)
            {
                return;
            }

            File.WriteAllBytes(dialog.FileName, entry.ReadAllBytes());
            StatusText.Text = $"Extracted {dialog.FileName}";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Extract");
        }
    }

    private sealed class FileRow
    {
        public string Name { get; set; } = "";
        public CatalogEntry Catalog { get; set; } = new();
        public ArchiveEntry? Entry { get; set; }
        public string Label => $"{Name}  ({Catalog.DisplayName})";

        public bool Matches(string q) =>
            string.IsNullOrWhiteSpace(q)
            || Name.Contains(q, StringComparison.OrdinalIgnoreCase);
    }
}

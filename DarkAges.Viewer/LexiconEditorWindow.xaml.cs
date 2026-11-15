using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using DarkAges.Formats.Catalog;

namespace DarkAges.Viewer;

public partial class LexiconEditorWindow : Window
{
    private readonly ObservableCollection<LexiconDraft> _entries = [];
    private bool _dirty;
    private bool _syncing;

    public LexiconEditorWindow()
    {
        InitializeComponent();
        EntryList.ItemsSource = _entries;
        LoadFromLexicon();
        UpdateStatus();
    }

    public bool Saved { get; private set; }

    private void LoadFromLexicon()
    {
        _syncing = true;
        _entries.Clear();
        foreach (LexiconEntry entry in SearchLexicon.AllEntries())
        {
            _entries.Add(new LexiconDraft(entry.English, string.Join(", ", entry.Korean)));
        }

        _dirty = false;
        _syncing = false;
        ApplyFilter();
        ClearEditor();
    }

    private void FilterChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        string q = FilterBox?.Text?.Trim() ?? "";
        IEnumerable<LexiconDraft> view = _entries;
        if (q.Length > 0)
        {
            view = _entries.Where(e =>
                e.English.Contains(q, StringComparison.OrdinalIgnoreCase)
                || e.Synonyms.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        EntryList.ItemsSource = view
            .OrderBy(e => e.English, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void EntrySelected(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || EntryList.SelectedItem is not LexiconDraft draft)
        {
            return;
        }

        _syncing = true;
        EnglishBox.Text = draft.English;
        SynonymBox.Text = draft.Synonyms;
        _syncing = false;
    }

    private void AddUpdateClicked(object sender, RoutedEventArgs e)
    {
        string english = EnglishBox.Text.Trim();
        string synonyms = SynonymBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(english))
        {
            UpdateStatus("Enter an English keyword.");
            return;
        }

        if (ParseSynonyms(synonyms).Count == 0)
        {
            UpdateStatus("Enter at least one synonym (comma-separated).");
            return;
        }

        LexiconDraft? existing = _entries.FirstOrDefault(x =>
            x.English.Equals(english, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.English = english;
            existing.Synonyms = synonyms;
        }
        else
        {
            _entries.Add(new LexiconDraft(english, synonyms));
        }

        _dirty = true;
        ApplyFilter();
        EntryList.SelectedItem = EntryList.Items.Cast<LexiconDraft>()
            .FirstOrDefault(x => x.English.Equals(english, StringComparison.OrdinalIgnoreCase));
        UpdateStatus($"Updated “{english}” (not saved yet).");
    }

    private void RemoveClicked(object sender, RoutedEventArgs e)
    {
        string english = EnglishBox.Text.Trim();
        if (EntryList.SelectedItem is LexiconDraft selected)
        {
            english = selected.English;
        }

        if (string.IsNullOrWhiteSpace(english))
        {
            UpdateStatus("Select an entry to remove.");
            return;
        }

        LexiconDraft? hit = _entries.FirstOrDefault(x =>
            x.English.Equals(english, StringComparison.OrdinalIgnoreCase));
        if (hit is null)
        {
            UpdateStatus($"“{english}” is not in the list.");
            return;
        }

        _entries.Remove(hit);
        _dirty = true;
        ClearEditor();
        ApplyFilter();
        UpdateStatus($"Removed “{english}” (not saved yet).");
    }

    private void SaveClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var payload = _entries
                .Select(d => new LexiconEntry(d.English, ParseSynonyms(d.Synonyms)))
                .Where(e => e.Korean.Count > 0)
                .ToList();
            SearchLexicon.SaveEntries(payload);
            Saved = true;
            _dirty = false;
            LoadFromLexicon();
            UpdateStatus($"Saved → {SearchLexicon.WritablePath}");
        }
        catch (Exception ex)
        {
            UpdateStatus("Save failed: " + ex.Message);
        }
    }

    private void ResetClicked(object sender, RoutedEventArgs e)
    {
        MessageBoxResult confirm = System.Windows.MessageBox.Show(
            this,
            "Delete the user lexicon override and restore the built-in dictionary?",
            "Reset lexicon",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            SearchLexicon.ResetToEmbedded();
            Saved = true;
            _dirty = false;
            LoadFromLexicon();
            UpdateStatus("Restored built-in lexicon.");
        }
        catch (Exception ex)
        {
            UpdateStatus("Reset failed: " + ex.Message);
        }
    }

    private void ClearEditor()
    {
        _syncing = true;
        EnglishBox.Text = "";
        SynonymBox.Text = "";
        EntryList.SelectedItem = null;
        _syncing = false;
    }

    private void UpdateStatus(string? message = null)
    {
        string source = SearchLexicon.HasUserOverride ? "user file" : "built-in";
        string dirty = _dirty ? " · unsaved changes" : "";
        string tail = $"{_entries.Count} entries ({source}){dirty}";
        StatusText.Text = string.IsNullOrWhiteSpace(message) ? tail : $"{message}  ·  {tail}";
    }

    private static IReadOnlyList<string> ParseSynonyms(string text) =>
        text.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private sealed class LexiconDraft
    {
        public LexiconDraft(string english, string synonyms)
        {
            English = english;
            Synonyms = synonyms;
        }

        public string English { get; set; }
        public string Synonyms { get; set; }
        public string Label => string.IsNullOrWhiteSpace(Synonyms)
            ? English
            : $"{English}  →  {Synonyms}";
    }
}

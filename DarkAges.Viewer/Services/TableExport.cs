using System.IO;
using System.Windows;
using System.Windows.Controls;
using DarkAges.Formats.IO;

namespace DarkAges.Viewer.Services;

public enum TableExportFormat
{
    Json,
    Csv,
    Sql
}

public static class TableExport
{
    public static TableExportFormat FormatOf(object? sender)
    {
        string? tag = (sender as FrameworkElement)?.Tag as string;
        return tag?.Trim().ToLowerInvariant() switch
        {
            "csv" => TableExportFormat.Csv,
            "sql" => TableExportFormat.Sql,
            _ => TableExportFormat.Json
        };
    }

    public static bool Save(
        Window? owner,
        string stem,
        TableDump dump,
        object? formatSource,
        TextBlock? status = null,
        string? jsonOverride = null) =>
        Save(owner, stem, dump, FormatOf(formatSource), status, jsonOverride);

    public static bool Save(
        Window? owner,
        string stem,
        TableDump dump,
        TableExportFormat format,
        TextBlock? status = null,
        string? jsonOverride = null)
    {
        (string filter, string ext) = DialogFor(format);
        string name = string.IsNullOrWhiteSpace(stem) ? dump.Table : stem;
        if (!name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
        {
            name += ext;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = filter,
            DefaultExt = ext.TrimStart('.'),
            AddExtension = true,
            FileName = name
        };
        bool ok = owner is null ? dialog.ShowDialog() == true : dialog.ShowDialog(owner) == true;
        if (!ok)
        {
            return false;
        }

        string path = EnsureExtension(dialog.FileName, ext);
        if (jsonOverride is not null && format == TableExportFormat.Json)
        {
            File.WriteAllText(path, jsonOverride);
        }
        else
        {
            dump.Write(path);
        }

        LastPath = path;
        if (status is not null)
        {
            status.Text = $"Exported {dump.Rows.Count} row(s) to {path}";
        }

        return true;
    }

    public static string? LastPath { get; private set; }

    private static (string Filter, string Extension) DialogFor(TableExportFormat format) => format switch
    {
        TableExportFormat.Csv => ("CSV (*.csv)|*.csv", ".csv"),
        TableExportFormat.Sql => ("SQL (*.sql)|*.sql", ".sql"),
        _ => ("JSON (*.json)|*.json", ".json")
    };

    private static string EnsureExtension(string path, string ext) =>
        Path.GetExtension(path).Equals(ext, StringComparison.OrdinalIgnoreCase)
            ? path
            : Path.ChangeExtension(path, ext);
}

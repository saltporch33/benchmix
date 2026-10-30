using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Benchmix.Core.Persistence;

namespace Benchmix.App;

/// <summary>
/// The dated versions kept beside a project, newest first, to pick one to go back to (Daniel:
/// "auto-save + version history"; built 2026-10-29). A plain list:
/// when each was written and its size. Restore goes through the view-model, which keeps what is on
/// screen as a version first and makes the restore one Ctrl+Z step. Never shown in a scripted run:
/// the view-model refuses before it gets here.
/// </summary>
public partial class VersionsWindow : Window
{
    public sealed record Entry(ProjectStore.ProjectVersion Version, string Label);

    private VersionsWindow(string projectName, IReadOnlyList<ProjectStore.ProjectVersion> versions)
    {
        InitializeComponent();
        Title = $"Versions of {projectName}";
        Intro.Text = $"{versions.Count} version{(versions.Count == 1 ? "" : "s")} of {projectName}, newest first. "
                   + "One is kept at every Save, and every ten minutes while you work. Pick one and click Restore: "
                   + "what you have now is kept as a version too, and Ctrl+Z puts it back.";
        List.ItemsSource = versions.Select(v => new Entry(v, $"{LabelFor(v.Written)}   ·   {SizeOf(v.Path)}")).ToList();
        if (List.Items.Count > 0) List.SelectedIndex = 0;
    }

    /// <summary>"Today 17:58:21", "Yesterday 09:15", or "Tue 27 Oct 2026 21:04".</summary>
    public static string LabelFor(DateTime when)
        => when.Date == DateTime.Today ? $"Today {when:HH:mm:ss}"
         : when.Date == DateTime.Today.AddDays(-1) ? $"Yesterday {when:HH:mm}"
         : when.ToString("ddd d MMM yyyy HH:mm");

    private static string SizeOf(string path)
    {
        try { long b = new FileInfo(path).Length; return b < 1024 ? $"{b} B" : $"{b / 1024.0:0} KB"; }
        catch { return ""; }
    }

    /// <summary>Shows the list over the main window and returns the version picked, or null.</summary>
    public static ProjectStore.ProjectVersion? Pick(string projectName, IReadOnlyList<ProjectStore.ProjectVersion> versions)
    {
        var w = new VersionsWindow(projectName, versions) { Owner = Application.Current.MainWindow };
        return w.ShowDialog() == true && w.List.SelectedItem is Entry e ? e.Version : null;
    }

    private void OnRestore(object sender, RoutedEventArgs e) { if (List.SelectedItem is not null) DialogResult = true; }
    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
    private void OnDoubleClick(object sender, MouseButtonEventArgs e) { if (List.SelectedItem is not null) DialogResult = true; }
}

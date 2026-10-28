using Benchmix.Core.Model;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Benchmix.App.ViewModels;

/// <summary>
/// One tab in the row above the tracks (2026-10-27): a separate arrangement in the same file.
/// The name is the model's; a rename types into <see cref="RenameDraft"/>, as a track's does.
/// </summary>
public sealed partial class TabViewModel : ObservableObject
{
    public TabViewModel(TabModel model)
    {
        Model = model;
        _name = model.Name;
    }

    public TabModel Model { get; }
    public Guid Id => Model.Id;

    [ObservableProperty] private string _name;

    /// <summary>True for the open tab: the one on screen, in the engine and in a render.</summary>
    [ObservableProperty] private bool _isCurrent;

    [ObservableProperty] private bool _isRenaming;
    [ObservableProperty] private string _renameDraft = "";

    /// <summary>How many tracks it holds, for its tooltip.</summary>
    public string Tip => $"{Name} — {Model.Tracks.Count} track{(Model.Tracks.Count == 1 ? "" : "s")}. Click to open it; double-click to rename it; right-click for more.";

    public void RefreshFromModel()
    {
        Name = Model.Name;
        OnPropertyChanged(nameof(Tip));
    }
}

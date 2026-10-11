using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Xunit;

namespace Benchmix.Tests;

/// <summary>
/// Reads the app's XAML as text, because this project does not load the WPF app. The defect it
/// guards against is invisible everywhere else: the build succeeds, the menu draws, the item is
/// enabled, and a click does nothing.
/// </summary>
public class MenuWiringTests
{
    static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    static string AppDirectory([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "src", "Benchmix.App"));

    /// <summary>
    /// Until 2026-10-03, "Split into vocals + instrumental" did nothing when clicked. Its command was
    /// bound by looking up the window, and a right-click menu is not inside the window — it opens in
    /// a popup of its own — so the binding found nothing and the click went nowhere, silently.
    /// ElementName fails the same way: the popup has its own name scope.
    /// </summary>
    [Fact]
    public void NoRightClickMenuItemReachesTheWindowThroughABinding()
    {
        var files = Directory.GetFiles(AppDirectory(), "*.xaml", SearchOption.AllDirectories);
        Assert.NotEmpty(files);

        var offenders = new List<string>();
        int menus = 0;
        foreach (var file in files)
        {
            var doc = XDocument.Load(file, LoadOptions.SetLineInfo);
            foreach (var menu in doc.Descendants(Xaml + "ContextMenu"))
            {
                menus++;
                foreach (var element in menu.DescendantsAndSelf())
                foreach (var attribute in element.Attributes())
                {
                    string v = attribute.Value;
                    if (v.Contains("AncestorType") || v.Contains("FindAncestor") || v.Contains("ElementName"))
                        offenders.Add($"{Path.GetFileName(file)}:{((System.Xml.IXmlLineInfo)element).LineNumber} "
                                    + $"{element.Name.LocalName} {attribute.Name.LocalName}=\"{v}\"");
                }
            }
        }

        Assert.True(menus > 0, "found no right-click menus to check — the search is looking in the wrong place");
        Assert.True(offenders.Count == 0,
            "A right-click menu item is bound through the window, which a menu cannot see: the item will "
          + "look normal and do nothing when clicked, as Split into vocals + instrumental did. Use a Click "
          + "handler like the other menu items.\n" + string.Join("\n", offenders));
    }
}

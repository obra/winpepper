#if WINDOWS
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Winpepper.App.Hosting;
using Winpepper.Core.ViewModels;

namespace Winpepper.App.Views;

public sealed partial class CorrectionsPage : Page
{
    private CorrectionsViewModel? _vm;

    public CorrectionsPage() { InitializeComponent(); }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _vm = ((AppShell)e.Parameter).CorrectionsVm;
        PreferredList.ItemsSource = _vm.Preferred;
        ReplacementsList.ItemsSource = _vm.Replacements;
    }

    private void OnAddPreferred(object sender, RoutedEventArgs e)
    {
        var text = NewPreferredBox.Text ?? "";
        var err = _vm!.AddPreferred(text);
        PreferredError.Text = err ?? "";
        if (err is null) NewPreferredBox.Text = "";
    }

    private void OnRemovePreferred(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PreferredEntry entry }) _vm!.RemovePreferred(entry);
    }

    private void OnAddReplacement(object sender, RoutedEventArgs e)
    {
        var w = NewWrongBox.Text ?? ""; var r = NewRightBox.Text ?? "";
        var err = _vm!.AddReplacement(w, r);
        ReplacementsError.Text = err ?? "";
        if (err is null) { NewWrongBox.Text = ""; NewRightBox.Text = ""; }
    }

    private void OnRemoveReplacement(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ReplacementEntry entry }) _vm!.RemoveReplacement(entry);
    }
}
#endif

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;

namespace Puck.Integrations.Claude;

/// <summary>The conversation so far, newest at the bottom.</summary>
public partial class ConversationWindow : Window
{
    readonly ObservableCollection<Exchange> _exchanges;

    public ConversationWindow(ObservableCollection<Exchange> exchanges)
    {
        InitializeComponent();
        _exchanges = exchanges;
        List.ItemsSource = exchanges;
        exchanges.CollectionChanged += OnChanged;
        Closed += (_, _) => exchanges.CollectionChanged -= OnChanged;
        Loaded += (_, _) => Refresh();
    }

    void OnChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refresh();

    void Refresh()
    {
        Empty.Visibility = _exchanges.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Scroller.ScrollToEnd();
    }
}

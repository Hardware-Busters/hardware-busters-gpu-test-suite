using System.Collections.Specialized;
using System.Windows.Controls;

namespace GpuSuite.App.Views;

public partial class RunView : UserControl
{
    public RunView()
    {
        InitializeComponent();
        // Auto-scroll the log to the newest line as entries stream in.
        ((INotifyCollectionChanged)LogItems.Items).CollectionChanged += (_, e) =>
        {
            if (e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset)
                LogScroll.ScrollToEnd();
        };
    }
}

using System.Collections.Specialized;
using System.Windows.Controls;

namespace GpuSuite.App.Views;

public partial class CoolerView : UserControl
{
    public CoolerView()
    {
        InitializeComponent();
        // Auto-scroll the sweep log to the newest line as entries stream in.
        ((INotifyCollectionChanged)LogItems.Items).CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add) LogScroll.ScrollToEnd();
        };
    }
}

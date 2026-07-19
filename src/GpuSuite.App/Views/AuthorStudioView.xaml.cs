using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using GpuSuite.App.ViewModels;
using GpuSuite.Authoring;
using System.Windows.Controls;

namespace GpuSuite.App.Views;
public partial class AuthorStudioView : UserControl
{
    private readonly HashSet<DataGrid> _hookedGrids = [];
    private bool _checkpointQueued;
    private bool _transitionCommitInProgress;
    private IInputElement? _blockedEditor;

    public AuthorStudioView()
    {
        InitializeComponent();
        AddHandler(UIElement.LostFocusEvent, new RoutedEventHandler(CheckpointAfterUiEdit), true);
        AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(CheckpointAfterUiEdit), true);
        AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler(CheckpointAfterSelection), true);
        AddHandler(UIElement.PreviewMouseDownEvent, new MouseButtonEventHandler(PreviewTransitionMouseDown), true);
        AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(PreviewTransitionKeyDown), true);
        PreviewLostKeyboardFocus += PreviewTransitionLostKeyboardFocus;
        DataContextChanged += AuthorStudioDataContextChanged;
        Loaded += (_, _) => HookDataGridCommitEvents();
    }

    private void CheckpointAfterUiEdit(object sender, RoutedEventArgs e) => QueueCheckpoint();
    private void CheckpointAfterSelection(object sender, SelectionChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, HookDataGridCommitEvents);
        QueueCheckpoint();
    }
    private void CheckpointAfterGridCellCommit(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Commit) QueueCheckpoint();
    }
    private void CheckpointAfterGridRowCommit(object? sender, DataGridRowEditEndingEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Commit) QueueCheckpoint();
    }
    private void QueueCheckpoint()
    {
        if (_checkpointQueued) return;
        _checkpointQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            _checkpointQueued = false;
            if (DataContext is AuthorStudioViewModel viewModel) _ = viewModel.TryCaptureUndoCheckpoint();
        });
    }

    private void AuthorStudioDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is AuthorStudioViewModel oldViewModel) oldViewModel.PendingUiEditsCommitRequested -= CommitPendingGridEdits;
        if (e.NewValue is AuthorStudioViewModel newViewModel) newViewModel.PendingUiEditsCommitRequested += CommitPendingGridEdits;
    }

    private bool CommitPendingGridEdits()
    {
        if (!UpdateFocusedBinding()) return false;
        HookDataGridCommitEvents();
        foreach (var grid in FindVisualChildren<DataGrid>(this))
        {
            if (!grid.CommitEdit(DataGridEditingUnit.Cell, exitEditingMode: true)) { RememberBlockedEditor(); return false; }
            if (!grid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true)) { RememberBlockedEditor(); return false; }
        }
        return true;
    }

    private bool UpdateFocusedBinding()
    {
        if (Keyboard.FocusedElement is not DependencyObject focused) return true;
        BindingExpressionBase? expression = null;
        switch (focused)
        {
            case TextBox textBox:
                expression = BindingOperations.GetBindingExpressionBase(textBox, TextBox.TextProperty);
                break;
            case ComboBox comboBox:
                expression = BindingOperations.GetBindingExpressionBase(comboBox, Selector.SelectedItemProperty)
                    ?? BindingOperations.GetBindingExpressionBase(comboBox, Selector.SelectedValueProperty);
                break;
            case ToggleButton toggleButton:
                expression = BindingOperations.GetBindingExpressionBase(toggleButton, ToggleButton.IsCheckedProperty);
                break;
        }
        expression?.UpdateSource();
        bool valid = AuthoringEditCommitCoordinator.IsScalarBindingValid(expression?.HasError == true, Validation.GetHasError(focused));
        if (!valid) RememberBlockedEditor();
        return valid;
    }

    private void PreviewTransitionMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsActiveEditorInput(e.OriginalSource as DependencyObject)) return;
        if (AuthoringEditCommitCoordinator.RequiresOutboundPointerCommit(GetInputSurface(e.OriginalSource as DependencyObject)) && !TryCommitForTransition()) e.Handled = true;
    }

    private void PreviewTransitionKeyDown(object sender, KeyEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (IsActiveEditorInput(source)) return;
        if (AuthoringEditCommitCoordinator.RequiresOutboundKeyCommit(GetInputSurface(source), e.Key.ToString()) && !TryCommitForTransition()) e.Handled = true;
    }

    private void PreviewTransitionLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (IsSameEditorSurface(e.OldFocus as DependencyObject, e.NewFocus as DependencyObject)) return;
        if (TryCommitForTransition()) return;
        _blockedEditor = e.OldFocus;
        e.Handled = true;
        RefocusBlockedEditor();
    }

    private bool TryCommitForTransition()
    {
        if (_transitionCommitInProgress) return true;
        _transitionCommitInProgress = true;
        try
        {
            bool committed = DataContext is not AuthorStudioViewModel viewModel || viewModel.TryCommitPendingUiEdits();
            if (!committed) RefocusBlockedEditor(); else _blockedEditor = null;
            return committed;
        }
        finally { _transitionCommitInProgress = false; }
    }

    private AuthoringEditCommitCoordinator.InputSurface GetInputSurface(DependencyObject? source)
    {
        if (IsActiveEditorInput(source)) return AuthoringEditCommitCoordinator.InputSurface.Editor;
        if (FindAncestor<TabItem>(source) is not null || FindAncestor<TabControl>(source) is not null) return AuthoringEditCommitCoordinator.InputSurface.Tab;
        if (FindAncestor<ListBox>(source) is ListBox listBox && IsDocumentSelector(listBox)) return AuthoringEditCommitCoordinator.InputSurface.DocumentSelector;
        if (FindAncestor<ButtonBase>(source) is not null) return AuthoringEditCommitCoordinator.InputSurface.Button;
        return AuthoringEditCommitCoordinator.InputSurface.Other;
    }

    private bool IsDocumentSelector(ListBox listBox)
        => DataContext is AuthorStudioViewModel viewModel
           && (ReferenceEquals(listBox.ItemsSource, viewModel.Profiles)
               || ReferenceEquals(listBox.ItemsSource, viewModel.Bots)
               || ReferenceEquals(listBox.ItemsSource, viewModel.Routes)
               || ReferenceEquals(listBox.ItemsSource, viewModel.Templates)
               || ReferenceEquals(listBox.ItemsSource, viewModel.Assets));

    private static bool IsActiveEditorInput(DependencyObject? source)
        => FindAncestor<TextBox>(source) is not null || FindAncestor<ComboBox>(source) is not null || FindAncestor<DataGrid>(source) is not null;

    private static bool IsSameEditorSurface(DependencyObject? oldFocus, DependencyObject? newFocus)
    {
        if (oldFocus is null || newFocus is null) return false;
        var oldGrid = FindAncestor<DataGrid>(oldFocus);
        if (oldGrid is not null && ReferenceEquals(oldGrid, FindAncestor<DataGrid>(newFocus))) return true;
        var oldCombo = FindAncestor<ComboBox>(oldFocus);
        return oldCombo is not null && ReferenceEquals(oldCombo, FindAncestor<ComboBox>(newFocus));
    }

    private void RememberBlockedEditor() => _blockedEditor ??= Keyboard.FocusedElement;
    private void RefocusBlockedEditor()
    {
        if (_blockedEditor is not UIElement editor) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => editor.Focus());
    }

    private void HookDataGridCommitEvents()
    {
        foreach (var grid in FindVisualChildren<DataGrid>(this))
        {
            if (!_hookedGrids.Add(grid)) continue;
            grid.CellEditEnding += CheckpointAfterGridCellCommit;
            grid.RowEditEnding += CheckpointAfterGridRowCommit;
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is T typed) yield return typed;
            foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }

    private static T? FindAncestor<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T typed) return typed;
            // A ComboBox drop-down is hosted in a Popup visual tree. Its logical parent still
            // identifies the owning ComboBox, so prefer it before falling back to visual ancestry.
            child = LogicalTreeHelper.GetParent(child)
                ?? (child is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                    ? System.Windows.Media.VisualTreeHelper.GetParent(child)
                    : null);
        }
        return null;
    }
}

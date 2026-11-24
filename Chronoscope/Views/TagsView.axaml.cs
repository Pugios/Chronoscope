using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Chronoscope.ViewModels;

namespace Chronoscope.Views;

public partial class TagsView : UserControl
{
    public TagsView()
    {
        InitializeComponent();

        // The grid's selection is not bindable; hand the view model a snapshot on every change
        ProcessGrid.SelectionChanged += (_, _) =>
        {
            if (DataContext is TagsViewModel vm)
                vm.SelectedRows = ProcessGrid.SelectedItems.OfType<ProcessRow>().ToList();
        };

        // handledEventsToo: the chip is captured during a drag, and controls on the way up may
        // mark the events handled - the board still has to see every one of them
        Board.AddHandler(PointerPressedEvent, OnBoardPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        Board.AddHandler(PointerMovedEvent, OnBoardMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        Board.AddHandler(PointerReleasedEvent, OnBoardReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        Board.AddHandler(PointerCaptureLostEvent, (_, _) => EndDrag(), RoutingStrategies.Bubble, handledEventsToo: true);
        Board.AddHandler(KeyDownEvent, OnBoardKeyDown, RoutingStrategies.Tunnel);
        Board.AddHandler(LostFocusEvent, OnBoardLostFocus, RoutingStrategies.Bubble);

        // "Add Tag" picked in the process table's tag picker
        DataContextChanged += (_, _) =>
        {
            if (_subscribed is not null) _subscribed.AddTagRequested -= OnAddTagRequested;
            _subscribed = ViewModel;
            if (_subscribed is not null) _subscribed.AddTagRequested += OnAddTagRequested;
        };
    }

    private TagsViewModel? ViewModel => DataContext as TagsViewModel;
    private TagsViewModel? _subscribed;

    // Up to the board, with its Add Tag cell open and ready for a name. Deferred until the picker
    // has closed: closing hands the focus back to the picker, which would otherwise take it
    // straight from the new name box - and an empty box losing focus closes itself.
    private void OnAddTagRequested()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (ViewModel is not { } vm || BoardItems.ContainerFromItem(vm.AddTag) is not { } container) return;

            var cell = container.GetVisualDescendants().OfType<Panel>().FirstOrDefault(p => p.Classes.Contains("AddTagCell"));
            if (cell is null) return;

            cell.BringIntoView();
            TryStartAddingTag(cell);
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Dragging Tags
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Done by hand rather than with the system's drag and drop, which is built for carrying data
    // between applications: here a copy of the chip follows the pointer on the board itself, and
    // whatever it would land on is outlined. A chip is picked up anywhere on it except its colour
    // picker, which keeps its own clicks.
    //
    // Where it lands decides what happens (TagsViewModel does the rest):
    //  - on a loose tag:            the two form a new group
    //  - on a group, or one of its tags: it joins that group
    //  - on the board's free space: it leaves its group
    //  - outside the board:         nothing

    // Movement before a press becomes a drag, so a plain click never moves anything
    const double DragThreshold = 4;

    private Border? _chip;            // the chip pressed on
    private TagColorRow? _tag;
    private Point _pressPoint;        // on the board
    private Point _grabOffset;        // within the chip, so the copy stays under the pointer as held
    private bool _dragging;
    private Border? _ghost;
    private Border? _highlighted;

    private void OnBoardPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(Board).Properties.IsLeftButtonPressed) return;
        if (e.Source is not Visual source) return;

        if (TryStartAddingTag(source))
        {
            e.Handled = true;
            return;
        }

        if (!IsDragHandle(source)) return;
        if (source.FindAncestorOfType<Border>(includeSelf: true) is not { } border) return;

        var chip = Ancestors(border).OfType<Border>().FirstOrDefault(b => b.Classes.Contains("TagChip"));
        if (chip?.DataContext is not TagColorRow tag) return;

        _chip = chip;
        _tag = tag;
        _pressPoint = e.GetPosition(Board);
        _grabOffset = e.GetPosition(chip);
        e.Pointer.Capture(chip);
        e.Handled = true;
    }

    private void OnBoardMoved(object? sender, PointerEventArgs e)
    {
        if (_chip is null || _tag is null) return;

        var point = e.GetPosition(Board);
        if (!_dragging)
        {
            var moved = point - _pressPoint;
            if (Math.Abs(moved.X) < DragThreshold && Math.Abs(moved.Y) < DragThreshold) return;
            StartDrag();
        }

        if (_ghost is not null)
        {
            Canvas.SetLeft(_ghost, point.X - _grabOffset.X);
            Canvas.SetTop(_ghost, point.Y - _grabOffset.Y);
        }

        Highlight(TargetAt(point));
    }

    private void OnBoardReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_chip is null || _tag is null) return;

        var tag = _tag;
        var target = _dragging ? TargetAt(e.GetPosition(Board)) : default;
        bool dropped = _dragging;

        // Tidy up BEFORE the view model rearranges the board: that replaces every chip on it
        e.Pointer.Capture(null);
        EndDrag();

        if (!dropped || ViewModel is not { } vm) return;

        switch (target.Kind)
        {
            case TargetKind.Tag: vm.DropOnTag(tag, target.Tag!); break;
            case TargetKind.Group: vm.DropOnGroup(tag, target.Group!); break;
            case TargetKind.Empty: vm.DropOnEmpty(tag); break;
        }
    }

    private void StartDrag()
    {
        _dragging = true;
        _chip!.Classes.Add("Dragged");

        _ghost = BuildGhost(_tag!);
        DragLayer.Children.Add(_ghost);
    }

    // Also the answer to a lost capture (the window losing focus mid-drag): drop nothing
    private void EndDrag()
    {
        Highlight(default);
        if (_ghost is not null) DragLayer.Children.Remove(_ghost);
        _chip?.Classes.Remove("Dragged");

        _ghost = null;
        _chip = null;
        _tag = null;
        _dragging = false;
    }

    // A copy of the chip - colour swatch and name - lifted off the board with a shadow
    private Border BuildGhost(TagColorRow tag)
    {
        this.TryFindResource("SolidBackgroundFillColorBaseBrush", ActualThemeVariant, out var background);

        return new Border
        {
            Padding = new Thickness(10, 6, 12, 6),
            CornerRadius = new CornerRadius(6),
            Background = background as IBrush ?? Brushes.Gray,
            BorderBrush = new SolidColorBrush(tag.Color),
            BorderThickness = new Thickness(1),
            BoxShadow = BoxShadows.Parse("0 4 12 0 #40000000"),
            Opacity = 0.95,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new Border
                    {
                        Width = 14, Height = 14, CornerRadius = new CornerRadius(3),
                        Background = new SolidColorBrush(tag.Color),
                        VerticalAlignment = VerticalAlignment.Center
                    },
                    new TextBlock { Text = tag.Tag, VerticalAlignment = VerticalAlignment.Center }
                }
            }
        };
    }

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Drop Targets
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

    private enum TargetKind { None, Tag, Group, Empty }

    private readonly record struct DropTarget(TargetKind Kind, Border? Element, TagColorRow? Tag, TagGroupBox? Group);

    // What lies under a point of the board: the innermost chip or group box, else the board
    // itself. The chip being dragged is not a target - dropping it on itself does nothing.
    private DropTarget TargetAt(Point point)
    {
        if (!new Rect(Board.Bounds.Size).Contains(point)) return default;
        if (Board.InputHitTest(point) is not Visual hit) return default;

        foreach (var visual in Ancestors(hit))
        {
            if (visual == Board) return new DropTarget(TargetKind.Empty, null, null, null);
            if (visual is not Border border) continue;

            if (border.Classes.Contains("TagChip") && border.DataContext is TagColorRow tag)
                return tag == _tag ? default : new DropTarget(TargetKind.Tag, border, tag, null);

            if (border.Classes.Contains("GroupBox") && border.DataContext is TagGroupBox group)
                return new DropTarget(TargetKind.Group, border, null, group);
        }
        return default;
    }

    private void Highlight(DropTarget target)
    {
        if (target.Element == _highlighted) return;

        _highlighted?.Classes.Remove("DropTarget");
        _highlighted = target.Element;
        _highlighted?.Classes.Add("DropTarget");
    }

    // Inside a tag chip, and not on (or in the flyout of) its colour picker
    private static bool IsDragHandle(Visual source)
    {
        foreach (var visual in Ancestors(source))
        {
            if (visual is ColorPicker) return false;
            if (visual is Border { Classes: var classes } && classes.Contains("TagChip")) return true;
        }
        return false;
    }

    private static IEnumerable<Visual> Ancestors(Visual visual)
    {
        for (Visual? v = visual; v is not null; v = v.GetVisualParent())
            yield return v;
    }

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Adding Tags
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // A click on the dashed "Add Tag" cell turns it into a name box. Enter adds the tag, as does
    // clicking elsewhere with a name typed; Escape, or leaving it empty, puts the cell back.

    private bool TryStartAddingTag(Visual source)
    {
        var cellPanel = Ancestors(source).OfType<Panel>().FirstOrDefault(p => p.Classes.Contains("AddTagCell"));
        if (cellPanel?.DataContext is not AddTagCell cell || cell.IsEditing) return false;

        cell.IsEditing = true;

        // The box only becomes visible with the next layout pass; focus it once it is there
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            cellPanel.GetVisualDescendants().OfType<TextBox>().FirstOrDefault()?.Focus(),
            Avalonia.Threading.DispatcherPriority.Background);
        return true;
    }

    private void OnBoardLostFocus(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not TextBox { Classes: var classes, DataContext: AddTagCell cell } || !classes.Contains("NewTagName"))
            return;
        if (!cell.IsEditing) return;   // Enter or Escape already dealt with it

        if (string.IsNullOrWhiteSpace(cell.Name)) ViewModel?.CancelAddTag();
        else ViewModel?.AddNewTag(cell.Name);
    }

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Renaming Groups
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // The name commits when its box loses focus. Enter commits by giving the focus up; Escape
    // first puts the old name back.

    private void OnBoardKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is not TextBox { Classes: var classes } box) return;
        if (e.Key is not (Key.Enter or Key.Escape)) return;

        if (classes.Contains("NewTagName") && box.DataContext is AddTagCell cell)
        {
            e.Handled = true;
            if (e.Key == Key.Enter) ViewModel?.AddNewTag(cell.Name);
            else ViewModel?.CancelAddTag();
            return;
        }

        if (!classes.Contains("GroupName")) return;

        if (e.Key == Key.Escape && box.DataContext is TagGroupBox group)
            box.Text = group.Name;

        // Avalonia 12 has no "clear focus": hand it to the board, which commits the name the same way
        e.Handled = true;
        Board.Focus();
    }
}

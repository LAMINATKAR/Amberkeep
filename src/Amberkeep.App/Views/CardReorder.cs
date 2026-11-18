using Amberkeep.App.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Amberkeep.App.Views;

/// <summary>
/// Dragging a backup card by its grip (<c>Border.drag-handle</c>) to another place on the dashboard. The cards move
/// live while dragging; releasing the button saves the order, Esc or losing the pointer puts them back.
/// </summary>
internal sealed class CardReorder
{
    /// <summary>The pointer must move this far (DIP) before the card is picked up, so a click does nothing.</summary>
    public const double PickUpDistance = 4;

    /// <summary>Near the top or bottom edge of the page (DIP), the page scrolls while dragging.</summary>
    public const double ScrollZone = 40;

    private const double ScrollStep = 24;

    private readonly ItemsControl _list;
    private JobCardViewModel? _card;
    private Point _pressedAt;
    private bool _dragging;
    private TopLevel? _topLevel;

    private CardReorder(ItemsControl list)
    {
        _list = list;
        list.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
        list.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        list.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        list.AddHandler(InputElement.PointerCaptureLostEvent, (_, _) => End(commit: false), RoutingStrategies.Bubble, handledEventsToo: true);
        // Esc reaches the window, not the list, which has no focus.
        list.AttachedToVisualTree += (_, _) =>
        {
            _topLevel = TopLevel.GetTopLevel(list);
            _topLevel?.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        };
        list.DetachedFromVisualTree += (_, _) =>
        {
            End(commit: false);
            _topLevel?.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
            _topLevel = null;
        };
    }

    public static CardReorder Attach(ItemsControl list) => new(list);

    /// <summary>
    /// Where a dragged card belongs: it passes a neighbour once the pointer crosses the neighbour's middle.
    /// </summary>
    /// <param name="cards">Top and bottom of each card, in order.</param>
    /// <param name="current">The dragged card's index.</param>
    /// <param name="y">The pointer, in the same coordinates.</param>
    public static int TargetIndex(IReadOnlyList<(double Top, double Bottom)> cards, int current, double y)
    {
        var index = current;
        while (index > 0 && y < Middle(cards[index - 1]))
        {
            index--;
        }

        while (index < cards.Count - 1 && y > Middle(cards[index + 1]))
        {
            index++;
        }

        return index;

        static double Middle((double Top, double Bottom) card) => (card.Top + card.Bottom) / 2;
    }

    private DashboardViewModel? Dashboard => _list.DataContext as DashboardViewModel;

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_list).Properties.IsLeftButtonPressed
            || e.Source is not Visual source
            || source.GetSelfAndVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("drag-handle")) is not { } handle
            || handle.DataContext is not JobCardViewModel { CanEdit: true } card)
        {
            return;
        }

        _card = card;
        _pressedAt = e.GetPosition(_list);
        _dragging = false;
        // The list keeps the capture: the card's own controls may be re-created when it moves.
        e.Pointer.Capture(_list);
        e.Handled = true;
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_card is null || Dashboard is not { } dashboard)
        {
            return;
        }

        var position = e.GetPosition(_list);
        if (!_dragging)
        {
            if (Math.Abs(position.Y - _pressedAt.Y) < PickUpDistance && Math.Abs(position.X - _pressedAt.X) < PickUpDistance)
            {
                return;
            }

            _dragging = true;
            _card.IsDragging = true;
        }

        var current = dashboard.Jobs.IndexOf(_card);
        if (current >= 0 && CardBounds() is { } bounds)
        {
            var target = TargetIndex(bounds, current, position.Y);
            if (target != current)
            {
                dashboard.MoveJob(_card, target);
            }
        }

        ScrollNearEdge(e);
        e.Handled = true;
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_card is null || e.InitialPressMouseButton != MouseButton.Left)
        {
            return;
        }

        e.Handled = _dragging;
        End(commit: true);
        e.Pointer.Capture(null);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_dragging && e.Key == Key.Escape)
        {
            End(commit: false);
            e.Handled = true;
        }
    }

    private void End(bool commit)
    {
        if (_card is not { } card)
        {
            return;
        }

        var dragged = _dragging;
        _card = null;
        _dragging = false;
        card.IsDragging = false;
        if (!dragged || Dashboard is not { } dashboard)
        {
            return;
        }

        if (commit)
        {
            dashboard.CommitOrder();
        }
        else
        {
            dashboard.CancelReorder();
        }
    }

    /// <summary>Top and bottom of every card in list coordinates, or null while the list is being rebuilt.</summary>
    private List<(double Top, double Bottom)>? CardBounds()
    {
        var bounds = new List<(double, double)>();
        for (var i = 0; i < _list.ItemCount; i++)
        {
            if (_list.ContainerFromIndex(i) is not { } container || container.TranslatePoint(default, _list) is not { } top)
            {
                return null;
            }

            bounds.Add((top.Y, top.Y + container.Bounds.Height));
        }

        return bounds;
    }

    private void ScrollNearEdge(PointerEventArgs e)
    {
        if (_list.FindAncestorOfType<ScrollViewer>() is not { } viewer)
        {
            return;
        }

        var y = e.GetPosition(viewer).Y;
        var step = y < ScrollZone ? -ScrollStep : y > viewer.Viewport.Height - ScrollZone ? ScrollStep : 0;
        if (step != 0)
        {
            viewer.Offset = new Vector(viewer.Offset.X, Math.Clamp(viewer.Offset.Y + step, 0, Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height)));
        }
    }
}

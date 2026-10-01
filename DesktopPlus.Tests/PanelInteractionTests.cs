using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Xunit;

namespace DesktopPlus.Tests;

public sealed class PanelInteractionTests
{
    [Fact]
    public void AutomaticActivationPreservesPanelOrder()
    {
        RunSta(() =>
        {
            var panel = CreatePanel();
            try
            {
                panel.IsPreviewPanel = false;
                new WindowInteropHelper(panel).EnsureHandle();
                long order = GetField<long>(panel, "_panelZOrderToken");

                Invoke(panel, "DesktopPanel_Activated", panel, EventArgs.Empty);

                Assert.Equal(order, GetField<long>(panel, "_panelZOrderToken"));
                Assert.True(GetField<bool>(panel, "_panelWindowOrderUpdateQueued"));
            }
            finally
            {
                panel.IsPreviewPanel = true;
                panel.Close();
            }
        });
    }

    [Fact]
    public void DirectMouseInputPromotesOnlyTheClickedPanel()
    {
        RunSta(() =>
        {
            var panel = CreatePanel();
            var trash = CreatePanel();
            try
            {
                panel.IsPreviewPanel = false;
                long oldOrder = GetField<long>(panel, "_panelZOrderToken");
                long trashOrder = GetField<long>(trash, "_panelZOrderToken");
                var mouseDown = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                {
                    RoutedEvent = UIElement.PreviewMouseDownEvent
                };

                ((Button)panel.FindName("CollapseButton")).RaiseEvent(mouseDown);

                Assert.True(GetField<long>(panel, "_panelZOrderToken") > oldOrder);
                Assert.Equal(trashOrder, GetField<long>(trash, "_panelZOrderToken"));
                Assert.True(GetField<bool>(panel, "_panelWindowOrderUpdateQueued"));
            }
            finally
            {
                panel.IsPreviewPanel = true;
                panel.Close();
                trash.Close();
            }
        });
    }

    [Fact]
    public void MinusClickOverridesHoverExpansionWithoutChangingTrash()
    {
        RunSta(() =>
        {
            var panel = CreatePanel();
            var trash = CreatePanel();
            try
            {
                panel.ForceCollapseState(true);
                SetField(panel, "_hoverExpanded", true);
                Invoke(panel, "ToggleCollapseAnimated");
                SetField(panel, "_queuedHoverTargetVisible", (bool?)true);
                Assert.True(GetField<bool>(panel, "_isCollapseAnimationRunning"));

                Click(panel, "CollapseButton");

                Assert.False(panel.isContentVisible);
                Assert.False(GetField<bool>(panel, "_isCollapseAnimationRunning"));
                Assert.Null(GetField<bool?>(panel, "_queuedHoverTargetVisible"));
                Assert.False(GetField<bool>(panel, "_hoverExpanded"));
                Assert.True(GetField<bool>(panel, "_hoverExpansionSuppressedUntilMouseLeave"));
                Assert.True(trash.isContentVisible);
                Assert.False(GetField<bool>(trash, "_isCollapseAnimationRunning"));

                Click(panel, "CollapseButton");
                Assert.False(GetField<bool>(panel, "_isCollapsedVisualState"));
                Assert.False(GetField<bool>(panel, "_hoverExpansionSuppressedUntilMouseLeave"));
            }
            finally
            {
                panel.Close();
                trash.Close();
            }
        });
    }

    [Fact]
    public void CloseClickCancelsPendingHoverAndAnimationOnlyOnItsOwnPanel()
    {
        RunSta(() =>
        {
            var panel = CreatePanel();
            var trash = CreatePanel();
            try
            {
                panel.ForceCollapseState(true);
                Invoke(panel, "ToggleCollapseAnimated");
                SetField(panel, "_queuedHoverTargetVisible", (bool?)true);
                SetField(panel, "_hoverLeaveCts", new CancellationTokenSource());

                Click(panel, "CloseButton");

                Assert.True(GetField<bool>(panel, "_isClosed"));
                Assert.False(GetField<bool>(panel, "_isCollapseAnimationRunning"));
                Assert.Null(GetField<bool?>(panel, "_queuedHoverTargetVisible"));
                Assert.Null(GetField<CancellationTokenSource?>(panel, "_hoverLeaveCts"));
                Assert.False(GetField<bool>(trash, "_isClosed"));
                Assert.True(trash.isContentVisible);
            }
            finally
            {
                if (!GetField<bool>(panel, "_isClosed")) panel.Close();
                trash.Close();
            }
        });
    }

    [Fact]
    public void HiddenPanelCannotStartOrQueueHoverExpansion()
    {
        RunSta(() =>
        {
            var panel = CreatePanel();
            try
            {
                panel.ForceCollapseState(true);
                Invoke(panel, "Window_MouseEnter", panel, new MouseEventArgs(Mouse.PrimaryDevice, 0));

                Assert.False(panel.isContentVisible);
                Assert.False(GetField<bool>(panel, "_isCollapseAnimationRunning"));
                Assert.False(GetField<bool>(panel, "_hoverExpanded"));
                Assert.Null(GetField<bool?>(panel, "_queuedHoverTargetVisible"));
            }
            finally
            {
                panel.Close();
            }
        });
    }

    private static DesktopPanel CreatePanel() => new() { IsPreviewPanel = true, Left = 100, Top = 100 };

    private static void Click(DesktopPanel panel, string buttonName)
    {
        var button = (Button)panel.FindName(buttonName);
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
    }

    private static T GetField<T>(DesktopPanel panel, string name) =>
        (T)typeof(DesktopPanel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;

    private static void SetField(DesktopPanel panel, string name, object? value) =>
        typeof(DesktopPanel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(panel, value);

    private static void Invoke(DesktopPanel panel, string name, params object[] arguments) =>
        typeof(DesktopPanel).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, arguments);

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Panel interaction test timed out.");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

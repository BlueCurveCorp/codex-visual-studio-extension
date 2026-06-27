using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

using CodexVsix.Models;
using CodexVsix.Services;
using CodexVsix.ViewModels;

using Microsoft.VisualStudio.Shell;

namespace CodexVsix;

public partial class CodexToolWindowControl : UserControl
{
    private readonly CodexToolWindowViewModel _viewModel;
    private readonly List<FrameworkElement> _chatSelectableElements = [];
    private readonly HashSet<ChatMessage> _subscribedChatMessages = [];
    private FrameworkElement? _selectionAnchorElement;
    private object? _selectionAnchorPosition;
    private bool _isSelectingAcrossBubbles;
    private bool _chatScrollToEndScheduled;
    private UserInputPromptWindow? _userInputPromptWindow;
    private bool _suppressUserInputWindowClosedCancel;

    public CodexToolWindowControl()
    {
        Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            this.InitializeComponent();
        }
        catch (Exception ex)
        {
            _ = ActivityLog.TryLogError("CodexVsix", new LocalizationService().ToolWindowXamlLoadLogMessage + Environment.NewLine + ex);
            throw;
        }

        try
        {
            this._viewModel = CodexViewModelHost.GetOrCreate();
        }
        catch (Exception ex)
        {
            _ = ActivityLog.TryLogError("CodexVsix", new LocalizationService().ToolWindowViewModelCreateLogMessage + Environment.NewLine + ex);
            throw;
        }

        this.DataContext = this._viewModel;
        this._viewModel.Messages.CollectionChanged += this.OnMessagesCollectionChanged;
        foreach (ChatMessage message in this._viewModel.Messages)
        {
            this.SubscribeMessage(message);
        }

        this._viewModel.PropertyChanged += this.OnViewModelPropertyChanged;
        Loaded += this.OnLoaded;
        Unloaded += this.OnUnloaded;
        SizeChanged += this.OnSizeChanged;
        PreviewKeyDown += this.OnPreviewKeyDown;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        this._viewModel.EnsureToolWindowStartupState();
        this.UpdatePromptTextBoxMaxHeight();
        this.ScrollChatToEnd();
        this.SyncUserInputPromptWindow();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        this.CloseUserInputPromptWindow(suppressCancel: true);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        this.UpdatePromptTextBoxMaxHeight();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && this.PromptTextBox.IsKeyboardFocusWithin)
        {
            this.ExecuteSendShortcut(e);
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.V && System.Windows.Clipboard.ContainsImage())
        {
            this._viewModel.PasteImageFromClipboard();
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.A && this.GetFocusedChatSelectableElement() is not null)
        {
            this.SelectAllChatText();
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.C)
        {
            string selectedChatText = this.GetSelectedChatText();
            if (!string.IsNullOrWhiteSpace(selectedChatText))
            {
                Clipboard.SetText(selectedChatText);
                e.Handled = true;
            }
        }
    }

    private void UpdatePromptTextBoxMaxHeight()
    {
        double availableHeight = this.ActualHeight > 0 ? this.ActualHeight : SystemParameters.WorkArea.Height;
        this.PromptTextBox.MaxHeight = Math.Max(this.PromptTextBox.MinHeight, availableHeight * 0.5d);
    }

    private void OnMessagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (ChatMessage item in e.OldItems.OfType<ChatMessage>())
            {
                this.UnsubscribeMessage(item);
            }
        }

        if (e.NewItems is not null)
        {
            foreach (ChatMessage item in e.NewItems.OfType<ChatMessage>())
            {
                this.SubscribeMessage(item);
            }
        }

        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (ChatMessage? item in this._subscribedChatMessages.ToList())
            {
                this.UnsubscribeMessage(item);
            }

            foreach (ChatMessage item in this._viewModel.Messages)
            {
                this.SubscribeMessage(item);
            }
        }

        this.ScrollChatToEnd();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(CodexToolWindowViewModel.IsBusy), StringComparison.Ordinal))
        {
            this.ScrollChatToEnd();
        }

        if (string.IsNullOrEmpty(e.PropertyName)
            || string.Equals(e.PropertyName, nameof(CodexToolWindowViewModel.CurrentUserInputPrompt), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(CodexToolWindowViewModel.HasCurrentUserInputPrompt), StringComparison.Ordinal))
        {
            this.SyncUserInputPromptWindow();
        }
    }

    private void ScrollChatToEnd()
    {
        if (this._chatScrollToEndScheduled)
        {
            return;
        }

        this._chatScrollToEndScheduled = true;
        _ = this.Dispatcher.InvokeAsync(() =>
        {
            this._chatScrollToEndScheduled = false;
            if (this._viewModel.Messages.Count == 0)
            {
                return;
            }

            this.ChatContentHost.ScrollIntoView(this._viewModel.Messages[this._viewModel.Messages.Count - 1]);
        }, DispatcherPriority.Background);
    }

    private void SubscribeMessage(ChatMessage message)
    {
        if (this._subscribedChatMessages.Add(message))
        {
            message.PropertyChanged += this.OnMessagePropertyChanged;
        }
    }

    private void UnsubscribeMessage(ChatMessage message)
    {
        if (this._subscribedChatMessages.Remove(message))
        {
            message.PropertyChanged -= this.OnMessagePropertyChanged;
        }
    }

    private void OnMessagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not ChatMessage message || !ReferenceEquals(message, this._viewModel.Messages.LastOrDefault()))
        {
            return;
        }

        if (string.Equals(e.PropertyName, nameof(ChatMessage.Text), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(ChatMessage.DisplayText), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(ChatMessage.Detail), StringComparison.Ordinal))
        {
            this.ScrollChatToEnd();
        }
    }

    private void OnChatTextBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && !this._chatSelectableElements.Contains(element))
        {
            this._chatSelectableElements.Add(element);

            switch (element)
            {
                case TextBox textBox:
                    textBox.ContextMenu ??= this.CreateChatContextMenu();
                    break;
                case RichTextBox richTextBox:
                    richTextBox.ContextMenu ??= this.CreateChatContextMenu();
                    break;
            }
        }
    }

    private void OnChatTextBoxUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element)
        {
            return;
        }

        _ = this._chatSelectableElements.Remove(element);
        if (ReferenceEquals(this._selectionAnchorElement, element))
        {
            this._selectionAnchorElement = null;
            this._selectionAnchorPosition = null;
            this._isSelectingAcrossBubbles = false;
        }
    }

    private void OnChatTextBoxPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element)
        {
            return;
        }

        this._selectionAnchorElement = element;
        this._selectionAnchorPosition = GetSelectionPoint(element, e.GetPosition(element));
        this._isSelectingAcrossBubbles = true;
    }

    private void OnMenuButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.ContextMenu is null)
        {
            return;
        }

        element.ContextMenu.DataContext = element.DataContext;
        element.ContextMenu.PlacementTarget = element;
        element.ContextMenu.IsOpen = true;
        e.Handled = true;
    }

    private void OnOpenHistoryPanelClick(object sender, RoutedEventArgs e)
    {
        ExecuteViewModelCommand(this._viewModel.OpenHistoryPanelCommand);
        e.Handled = true;
    }

    private void OnOpenSettingsPanelClick(object sender, RoutedEventArgs e)
    {
        ExecuteViewModelCommand(this._viewModel.OpenSettingsPanelCommand);
        e.Handled = true;
    }

    private void OnSetMarkdownViewClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ChatMessage message, Tag: string modeTag })
        {
            message.SetMarkdownView(string.Equals(modeTag, "rendered", StringComparison.OrdinalIgnoreCase));
        }

        e.Handled = true;
    }

    private void OnRateLimitsContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu contextMenu)
        {
            return;
        }

        object? placementDataContext = (contextMenu.PlacementTarget as FrameworkElement)?.DataContext;
        contextMenu.DataContext = placementDataContext ?? contextMenu.DataContext;
        if (contextMenu.DataContext is not CodexToolWindowViewModel viewModel)
        {
            return;
        }

        const int staticItemCount = 4;
        while (contextMenu.Items.Count > staticItemCount)
        {
            contextMenu.Items.RemoveAt(contextMenu.Items.Count - 1);
        }

        if (viewModel.HasRateLimitData)
        {
            foreach (CodexRateLimitWindowSummary? entry in viewModel.RateLimitEntries.Where(item => item is not null && item.HasData))
            {
                _ = contextMenu.Items.Add(this.CreateRateLimitMenuItem(entry));
            }

            return;
        }

        _ = contextMenu.Items.Add(new MenuItem
        {
            Header = viewModel.Localization.RateLimitsUnavailable,
            IsEnabled = false
        });
    }

    private void OnCloseSidebarClick(object sender, RoutedEventArgs e)
    {
        ExecuteViewModelCommand(this._viewModel.CloseSidebarCommand);
        e.Handled = true;
    }

    private void OnSelectSettingsSectionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element)
        {
            return;
        }

        object parameter = element.Tag ?? element.DataContext;
        ExecuteViewModelCommand(this._viewModel.SelectSettingsSectionCommand, parameter);
        e.Handled = true;
    }

    private void OnPromptTextBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            this.ExecuteSendShortcut(e);
        }
    }

    private void OnLanguageOptionsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
        if (sender is not ListBox listBox || listBox.SelectedValue is not string value)
        {
            return;
        }

        this._viewModel.SelectedLanguageTag = value;
    }

    private void OnLanguageOptionClick(object sender, RoutedEventArgs e)
    {
        Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
        if (sender is not FrameworkElement element || element.Tag is not string value)
        {
            return;
        }

        this._viewModel.SelectedLanguageTag = value;
        if (this._viewModel.CloseSidebarCommand.CanExecute(null))
        {
            this._viewModel.CloseSidebarCommand.Execute(null);
        }
        e.Handled = true;
    }

    private void OnLanguageOptionPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
        if (sender is not FrameworkElement element || element.Tag is not string value)
        {
            return;
        }

        this._viewModel.SelectedLanguageTag = value;
        if (this._viewModel.CloseSidebarCommand.CanExecute(null))
        {
            this._viewModel.CloseSidebarCommand.Execute(null);
        }
        e.Handled = true;
    }

    private void OnLanguageOptionsPanelIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true)
        {
            return;
        }

        _ = this.Dispatcher.BeginInvoke(new Action(() =>
        {
            _ = this.LanguageSearchTextBox.Focus();
            _ = Keyboard.Focus(this.LanguageSearchTextBox);
        }), DispatcherPriority.Input);
    }

    private void OnHistorySearchPanelIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true)
        {
            return;
        }

        _ = this.Dispatcher.BeginInvoke(new Action(() =>
        {
            _ = this.HistorySearchTextBox.Focus();
            _ = Keyboard.Focus(this.HistorySearchTextBox);
        }), DispatcherPriority.Input);
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);

        if (!this._isSelectingAcrossBubbles || e.LeftButton != MouseButtonState.Pressed || this._selectionAnchorElement is null || this._selectionAnchorPosition is null)
        {
            return;
        }

        Point point = e.GetPosition(this.ChatContentHost);
        FrameworkElement? currentElement = this.FindChatSelectableElementAtPoint(point);
        if (currentElement is null || ReferenceEquals(currentElement, this._selectionAnchorElement))
        {
            return;
        }

        if (Mouse.Captured != this.ChatContentHost)
        {
            _ = Mouse.Capture(this.ChatContentHost, CaptureMode.SubTree);
        }

        this.ExtendSelectionAcrossBubbles(currentElement, e.GetPosition(currentElement));
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);

        this._isSelectingAcrossBubbles = false;
        if (Mouse.Captured == this.ChatContentHost)
        {
            _ = Mouse.Capture(null);
        }
    }

    private void ExtendSelectionAcrossBubbles(FrameworkElement currentElement, Point point)
    {
        if (this._selectionAnchorElement is null || this._selectionAnchorPosition is null)
        {
            return;
        }

        List<FrameworkElement> orderedElements = this.GetOrderedChatSelectableElements();
        int anchorPosition = orderedElements.IndexOf(this._selectionAnchorElement);
        int currentPosition = orderedElements.IndexOf(currentElement);
        if (anchorPosition < 0 || currentPosition < 0)
        {
            return;
        }

        object? currentPositionValue = GetSelectionPoint(currentElement, point);
        if (currentPositionValue is null)
        {
            return;
        }

        this.ClearChatSelection();

        if (anchorPosition == currentPosition)
        {
            SelectRange(currentElement, this._selectionAnchorPosition, currentPositionValue);
            return;
        }

        bool forwardSelection = currentPosition > anchorPosition;
        int start = forwardSelection ? anchorPosition : currentPosition;
        int end = forwardSelection ? currentPosition : anchorPosition;

        for (int index = start; index <= end; index++)
        {
            FrameworkElement element = orderedElements[index];
            if (ReferenceEquals(element, this._selectionAnchorElement))
            {
                if (forwardSelection)
                {
                    SelectRange(element, this._selectionAnchorPosition, GetSelectionEnd(element));
                }
                else
                {
                    SelectRange(element, GetSelectionStart(element), this._selectionAnchorPosition);
                }

                continue;
            }

            if (ReferenceEquals(element, currentElement))
            {
                if (forwardSelection)
                {
                    SelectRange(element, GetSelectionStart(element), currentPositionValue);
                }
                else
                {
                    SelectRange(element, currentPositionValue, GetSelectionEnd(element));
                }

                continue;
            }

            SelectAll(element);
        }
    }

    private void OnChatCopyMenuItemClick(object sender, RoutedEventArgs e)
    {
        string selectedChatText = this.GetSelectedChatText();
        if (string.IsNullOrWhiteSpace(selectedChatText))
        {
            FrameworkElement? placementTarget = (sender as FrameworkElement)?.Parent is ContextMenu contextMenu
                ? contextMenu.PlacementTarget as FrameworkElement
                : null;

            string fallbackSelection = placementTarget is null ? string.Empty : GetSelectedText(placementTarget);
            if (!string.IsNullOrWhiteSpace(fallbackSelection))
            {
                Clipboard.SetText(fallbackSelection);
            }

            return;
        }

        Clipboard.SetText(selectedChatText);
    }

    private void OnChatSelectAllMenuItemClick(object sender, RoutedEventArgs e)
    {
        this.SelectAllChatText();
    }

    private ContextMenu CreateChatContextMenu()
    {
        ContextMenu contextMenu = new();
        _ = contextMenu.Items.Add(new MenuItem
        {
            Header = this._viewModel.Localization.CopyButton
        });
        _ = contextMenu.Items.Add(new MenuItem
        {
            Header = this._viewModel.Localization.SelectAllButton
        });

        if (contextMenu.Items[0] is MenuItem copyMenuItem)
        {
            copyMenuItem.Click += this.OnChatCopyMenuItemClick;
        }

        if (contextMenu.Items[1] is MenuItem selectAllMenuItem)
        {
            selectAllMenuItem.Click += this.OnChatSelectAllMenuItemClick;
        }

        return contextMenu;
    }

    private MenuItem CreateRateLimitMenuItem(CodexRateLimitWindowSummary entry)
    {
        MenuItem menuItem = new()
        {
            Header = entry,
            HeaderTemplate = this.TryFindResource("RateLimitPopupEntryTemplate") as DataTemplate,
            IsEnabled = false
        };

        if (this.TryFindResource("PopupMenuItemStyle") is Style style)
        {
            menuItem.Style = style;
        }

        return menuItem;
    }

    private void SelectAllChatText()
    {
        foreach (FrameworkElement element in this.GetOrderedChatSelectableElements())
        {
            SelectAll(element);
        }
    }

    private void ClearChatSelection()
    {
        foreach (FrameworkElement element in this._chatSelectableElements)
        {
            ClearSelection(element);
        }
    }

    private List<FrameworkElement> GetOrderedChatSelectableElements()
    {
        return this._chatSelectableElements
            .Where(element => element.IsLoaded)
            .OrderBy(element => element.TranslatePoint(new Point(0, 0), this.ChatContentHost).Y)
            .ThenBy(element => element.TranslatePoint(new Point(0, 0), this.ChatContentHost).X)
            .ToList();
    }

    private FrameworkElement? FindChatSelectableElementAtPoint(Point point)
    {
        DependencyObject? hit = this.ChatContentHost.InputHitTest(point) as DependencyObject;
        FrameworkElement? directMatch = FindSelectableChatTextBox(hit);
        if (directMatch is not null)
        {
            return directMatch;
        }

        FrameworkElement? nearestElement = null;
        double nearestDistance = double.MaxValue;

        foreach (FrameworkElement element in this.GetOrderedChatSelectableElements())
        {
            Point origin = element.TranslatePoint(new Point(0, 0), this.ChatContentHost);
            Rect bounds = new(origin, new Size(element.ActualWidth, element.ActualHeight));
            if (bounds.Contains(point))
            {
                return element;
            }

            double deltaX = point.X < bounds.Left
                ? bounds.Left - point.X
                : point.X > bounds.Right
                    ? point.X - bounds.Right
                    : 0d;
            double deltaY = point.Y < bounds.Top
                ? bounds.Top - point.Y
                : point.Y > bounds.Bottom
                    ? point.Y - bounds.Bottom
                    : 0d;
            double distance = (deltaX * deltaX) + (deltaY * deltaY);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearestElement = element;
            }
        }

        return nearestElement;
    }

    private string GetSelectedChatText()
    {
        List<string> fragments = this.GetOrderedChatSelectableElements()
            .Select(GetSelectedText)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToList();

        return fragments.Count == 0 ? string.Empty : string.Join(Environment.NewLine, fragments);
    }

    private FrameworkElement? GetFocusedChatSelectableElement()
    {
        return this._chatSelectableElements.FirstOrDefault(element => element.IsKeyboardFocusWithin || element.IsFocused);
    }

    private static void SelectRange(FrameworkElement element, object start, object end)
    {
        switch (element)
        {
            case TextBox textBox when start is int startIndex && end is int endIndex:
                int boundedAnchor = Math.Max(0, Math.Min(startIndex, textBox.Text.Length));
                int boundedCurrent = Math.Max(0, Math.Min(endIndex, textBox.Text.Length));
                int selectionStart = Math.Min(boundedAnchor, boundedCurrent);
                int selectionLength = Math.Abs(boundedCurrent - boundedAnchor);
                textBox.Select(selectionStart, selectionLength);
                break;
            case RichTextBox richTextBox when start is TextPointer startPointer && end is TextPointer endPointer:
                richTextBox.Selection.Select(startPointer, endPointer);
                break;
        }
    }

    private static object? GetSelectionPoint(FrameworkElement element, Point point)
    {
        switch (element)
        {
            case TextBox textBox:
                int index = textBox.GetCharacterIndexFromPoint(point, true);
                return index >= 0 ? index : point.X <= 0 ? 0 : textBox.Text.Length;
            case RichTextBox richTextBox:
                return richTextBox.GetPositionFromPoint(point, true) ?? richTextBox.Document.ContentEnd;
            default:
                return null;
        }
    }

    private static object GetSelectionStart(FrameworkElement element)
    {
        return element switch
        {
            TextBox => 0,
            RichTextBox richTextBox => richTextBox.Document.ContentStart,
            _ => 0
        };
    }

    private static object GetSelectionEnd(FrameworkElement element)
    {
        return element switch
        {
            TextBox textBox => textBox.Text.Length,
            RichTextBox richTextBox => richTextBox.Document.ContentEnd,
            _ => 0
        };
    }

    private static void ClearSelection(FrameworkElement element)
    {
        switch (element)
        {
            case TextBox textBox:
                textBox.Select(0, 0);
                break;
            case RichTextBox richTextBox:
                richTextBox.Selection.Select(richTextBox.Document.ContentStart, richTextBox.Document.ContentStart);
                break;
        }
    }

    private static void SelectAll(FrameworkElement element)
    {
        switch (element)
        {
            case TextBox textBox:
                textBox.SelectAll();
                break;
            case RichTextBox richTextBox:
                richTextBox.Selection.Select(richTextBox.Document.ContentStart, richTextBox.Document.ContentEnd);
                break;
        }
    }

    private static string GetSelectedText(FrameworkElement element)
    {
        return element switch
        {
            TextBox textBox when textBox.SelectionLength > 0 => textBox.SelectedText,
            RichTextBox richTextBox when !richTextBox.Selection.IsEmpty => new TextRange(richTextBox.Selection.Start, richTextBox.Selection.End).Text.TrimEnd('\r', '\n'),
            _ => string.Empty
        };
    }

    private void ExecuteSendShortcut(KeyEventArgs e)
    {
        if (this._viewModel.SendCommand.CanExecute(null))
        {
            this._viewModel.SendCommand.Execute(null);
        }

        e.Handled = true;
    }

    private static void ExecuteViewModelCommand(ICommand command, object? parameter = null)
    {
        if (command.CanExecute(parameter))
        {
            command.Execute(parameter);
        }
    }

    private static FrameworkElement? FindSelectableChatTextBox(DependencyObject? origin)
    {
        DependencyObject? current = origin;
        while (current is not null)
        {
            if (current is FrameworkElement element && string.Equals(element.Tag as string, "ChatSelectable", StringComparison.Ordinal))
            {
                return element;
            }

            current = GetParentDependencyObject(current);
        }

        return null;
    }

    private static DependencyObject? GetParentDependencyObject(DependencyObject current)
    {
        if (current is FrameworkContentElement frameworkContentElement)
        {
            return frameworkContentElement.Parent
                ?? ContentOperations.GetParent(frameworkContentElement)
                ?? LogicalTreeHelper.GetParent(frameworkContentElement);
        }

        if (current is ContentElement contentElement)
        {
            return ContentOperations.GetParent(contentElement)
                ?? LogicalTreeHelper.GetParent(contentElement);
        }

        if (current is FrameworkElement frameworkElement)
        {
            return frameworkElement.Parent
                ?? LogicalTreeHelper.GetParent(frameworkElement)
                ?? GetVisualParent(frameworkElement);
        }

        return current switch
        {
            Visual => GetVisualParent(current),
            Visual3D => GetVisualParent(current),
            _ => LogicalTreeHelper.GetParent(current)
        };
    }

    private static DependencyObject? GetVisualParent(DependencyObject current)
    {
        try
        {
            return VisualTreeHelper.GetParent(current);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private void SyncUserInputPromptWindow()
    {
        if (!this._viewModel.HasCurrentUserInputPrompt)
        {
            this.CloseUserInputPromptWindow(suppressCancel: true);
            return;
        }

        if (this._userInputPromptWindow is null)
        {
            this._userInputPromptWindow = new UserInputPromptWindow
            {
                DataContext = this._viewModel,
                Owner = Window.GetWindow(this)
            };
            this._userInputPromptWindow.Closed += this.OnUserInputPromptWindowClosed;
            this._userInputPromptWindow.Show();
            return;
        }

        if (!this._userInputPromptWindow.IsVisible)
        {
            this._userInputPromptWindow.Show();
        }

        _ = this._userInputPromptWindow.Activate();
    }

    private void CloseUserInputPromptWindow(bool suppressCancel)
    {
        if (this._userInputPromptWindow is null)
        {
            return;
        }

        this._suppressUserInputWindowClosedCancel = suppressCancel;
        this._userInputPromptWindow.Close();
        this._suppressUserInputWindowClosedCancel = false;
    }

    private void OnUserInputPromptWindowClosed(object? sender, EventArgs e)
    {
        this._userInputPromptWindow?.Closed -= this.OnUserInputPromptWindowClosed;
        this._userInputPromptWindow = null;

        if (!this._suppressUserInputWindowClosedCancel && this._viewModel.HasCurrentUserInputPrompt)
        {
            this._viewModel.DismissUserInputPrompt();
        }
    }
}

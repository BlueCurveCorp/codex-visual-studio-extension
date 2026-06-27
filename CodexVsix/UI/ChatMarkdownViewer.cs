using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CodexVsix.UI;

public sealed class ChatMarkdownViewer : RichTextBox
{
    private const double MinimumNaturalWidth = 24d;
    private const double NaturalWidthPadding = 20d;

    private bool _isRenderingDocument;
    private bool _pendingRender = true;
    private string? _renderedMarkdownText;
    private string? _renderedWorkspaceRoot;
    private ICommand? _renderedLinkCommand;
    private Brush? _renderedForeground;

    public static readonly DependencyProperty MarkdownTextProperty = DependencyProperty.Register(
        nameof(MarkdownText),
        typeof(string),
        typeof(ChatMarkdownViewer),
        new PropertyMetadata(string.Empty, OnMarkdownTextChanged));

    public static readonly DependencyProperty LinkCommandProperty = DependencyProperty.Register(
        nameof(LinkCommand),
        typeof(ICommand),
        typeof(ChatMarkdownViewer),
        new PropertyMetadata(null, OnRenderContextChanged));

    public static readonly DependencyProperty WorkspaceRootProperty = DependencyProperty.Register(
        nameof(WorkspaceRoot),
        typeof(string),
        typeof(ChatMarkdownViewer),
        new PropertyMetadata(string.Empty, OnRenderContextChanged));

    public ChatMarkdownViewer()
    {
        this.IsReadOnly = true;
        this.IsUndoEnabled = false;
        this.IsDocumentEnabled = true;
        this.Background = Brushes.Transparent;
        this.BorderBrush = Brushes.Transparent;
        this.BorderThickness = new Thickness(0);
        this.Padding = new Thickness(0);
        this.Margin = new Thickness(0);
        this.AcceptsReturn = true;
        this.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        this.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        ContextMenuService.SetIsEnabled(this, true);
        IsVisibleChanged += this.OnIsVisibleChanged;
    }

    public string MarkdownText
    {
        get => (string)this.GetValue(MarkdownTextProperty);
        set => this.SetValue(MarkdownTextProperty, value);
    }

    public ICommand? LinkCommand
    {
        get => (ICommand?)this.GetValue(LinkCommandProperty);
        set => this.SetValue(LinkCommandProperty, value);
    }

    public string WorkspaceRoot
    {
        get => (string)this.GetValue(WorkspaceRootProperty);
        set => this.SetValue(WorkspaceRootProperty, value);
    }

    private static void OnMarkdownTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ChatMarkdownViewer viewer)
        {
            return;
        }

        viewer.RequestRender();
    }

    private static void OnRenderContextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ChatMarkdownViewer viewer)
        {
            viewer.RequestRender();
        }
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (this._isRenderingDocument)
        {
            return;
        }

        if (e.Property == ForegroundProperty)
        {
            // Rebuild the FlowDocument so markdown brushes follow VS theme/foreground updates.
            this.RequestRender();
        }
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (this.IsVisible && this._pendingRender)
        {
            this.RenderDocument();
        }
    }

    protected override Size MeasureOverride(Size constraint)
    {
        if (double.IsInfinity(constraint.Width) || constraint.Width <= 0)
        {
            return base.MeasureOverride(constraint);
        }

        double naturalWidth = this.CalculateNaturalWidth(constraint.Width);
        double targetWidth = Math.Max(MinimumNaturalWidth, Math.Min(constraint.Width, naturalWidth));
        Size measured = base.MeasureOverride(new Size(targetWidth, constraint.Height));

        return new Size(targetWidth, measured.Height);
    }

    private void RequestRender()
    {
        this._pendingRender = true;
        if (this.IsVisible)
        {
            this.RenderDocument();
        }
    }

    private void RenderDocument()
    {
        string markdown = this.MarkdownText ?? string.Empty;
        string workspaceRoot = this.WorkspaceRoot ?? string.Empty;
        if (!this._pendingRender
            && string.Equals(this._renderedMarkdownText, markdown, StringComparison.Ordinal)
            && string.Equals(this._renderedWorkspaceRoot, workspaceRoot, StringComparison.Ordinal)
            && ReferenceEquals(this._renderedLinkCommand, this.LinkCommand)
            && ReferenceEquals(this._renderedForeground, this.Foreground))
        {
            return;
        }

        if (!this.IsVisible)
        {
            this._pendingRender = true;
            return;
        }

        this._isRenderingDocument = true;
        try
        {
            this.Document = MarkdownRenderer.CreateDocument(
                markdown,
                this.Foreground,
                new MarkdownRenderOptions(this.LinkCommand, workspaceRoot));
            this._renderedMarkdownText = markdown;
            this._renderedWorkspaceRoot = workspaceRoot;
            this._renderedLinkCommand = this.LinkCommand;
            this._renderedForeground = this.Foreground;
            this._pendingRender = false;
        }
        finally
        {
            this._isRenderingDocument = false;
        }
    }

    private double CalculateNaturalWidth(double maxWidth)
    {
        string markdown = this.MarkdownText ?? string.Empty;
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return MinimumNaturalWidth;
        }

        if (ContainsWidthHungryMarkdown(markdown))
        {
            return maxWidth;
        }

        double maxLineWidth = 0d;
        string[] lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        foreach (string? rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            maxLineWidth = Math.Max(maxLineWidth, this.MeasureLineWidth(line));
            if (maxLineWidth + NaturalWidthPadding >= maxWidth)
            {
                return maxWidth;
            }
        }

        return Math.Ceiling(maxLineWidth + NaturalWidthPadding);
    }

    private static bool ContainsWidthHungryMarkdown(string markdown)
    {
        return markdown.IndexOf("```", StringComparison.Ordinal) >= 0;
    }

    private double MeasureLineWidth(string line)
    {
        Typeface typeface = new(this.FontFamily, this.FontStyle, this.FontWeight, this.FontStretch);
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        FormattedText formattedText = new(
            line,
            CultureInfo.CurrentUICulture,
            this.FlowDirection,
            typeface,
            this.FontSize,
            this.Foreground,
            pixelsPerDip);

        return formattedText.WidthIncludingTrailingWhitespace;
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdBlock = Markdig.Syntax.Block;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableCell = Markdig.Extensions.Tables.TableCell;
using MdTableRow = Markdig.Extensions.Tables.TableRow;
using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;
using WpfApplication = System.Windows.Application;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfSolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace DeskWidgets.Services;

/// <summary>把常见 Markdown 转成 WPF 控件（粗体/列表/代码/链接等）。</summary>
public static class MarkdownBubble
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    public static FrameworkElement Build(string markdown, MediaBrush foreground, double fontSize = 13)
    {
        var doc = Markdown.Parse(markdown ?? "", Pipeline);
        var root = new StackPanel();
        foreach (var block in doc)
            AppendBlock(root, block, foreground, fontSize);
        if (root.Children.Count == 0)
        {
            root.Children.Add(new TextBlock
            {
                Text = markdown ?? "",
                FontSize = fontSize,
                Foreground = foreground,
                TextWrapping = TextWrapping.Wrap,
            });
        }
        return root;
    }

    private static void AppendBlock(StackPanel parent, MdBlock block, MediaBrush fg, double fontSize)
    {
        switch (block)
        {
            case HeadingBlock heading:
                parent.Children.Add(MakeParagraph(heading.Inline, fg, fontSize + Math.Max(0, 4 - heading.Level), FontWeights.SemiBold, bottom: 6));
                break;
            case ParagraphBlock para:
                parent.Children.Add(MakeParagraph(para.Inline, fg, fontSize, FontWeights.Normal, bottom: 6));
                break;
            case QuoteBlock quote:
            {
                var inner = new StackPanel();
                foreach (var child in quote)
                    AppendBlock(inner, child, fg, fontSize);
                parent.Children.Add(new Border
                {
                    BorderBrush = new WpfSolidColorBrush(MediaColor.FromArgb(0x55, 0x00, 0x78, 0xD4)),
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(8, 2, 0, 2),
                    Margin = new Thickness(0, 0, 0, 6),
                    Child = inner,
                });
                break;
            }
            case ListBlock list:
            {
                var i = 1;
                foreach (var item in list)
                {
                    if (item is not ListItemBlock listItem) continue;
                    var row = new DockPanel { Margin = new Thickness(0, 0, 0, 2), LastChildFill = true };
                    var bullet = new TextBlock
                    {
                        Text = list.IsOrdered ? $"{i++}." : "•",
                        FontSize = fontSize,
                        Foreground = fg,
                        Width = 18,
                        Margin = new Thickness(0, 0, 4, 0),
                    };
                    DockPanel.SetDock(bullet, Dock.Left);
                    row.Children.Add(bullet);
                    var body = new StackPanel();
                    foreach (var child in listItem)
                        AppendBlock(body, child, fg, fontSize);
                    // 列表项里段落少留底边距
                    foreach (var c in body.Children.OfType<TextBlock>())
                        c.Margin = new Thickness(0, 0, 0, 2);
                    row.Children.Add(body);
                    parent.Children.Add(row);
                }
                break;
            }
            case FencedCodeBlock code:
            case CodeBlock plainCode:
            {
                var text = GetCodeText(block);
                parent.Children.Add(new Border
                {
                    Background = new WpfSolidColorBrush(MediaColor.FromArgb(0x10, 0x00, 0x00, 0x00)),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(8, 6, 8, 6),
                    Margin = new Thickness(0, 0, 0, 6),
                    Child = new TextBlock
                    {
                        Text = text,
                        FontFamily = new WpfFontFamily("Consolas, Cascadia Mono, Microsoft YaHei UI"),
                        FontSize = fontSize - 0.5,
                        Foreground = fg,
                        TextWrapping = TextWrapping.Wrap,
                    },
                });
                break;
            }
            case ThematicBreakBlock:
                parent.Children.Add(new Border
                {
                    Height = 1,
                    Background = new WpfSolidColorBrush(MediaColor.FromArgb(0x33, 0x00, 0x00, 0x00)),
                    Margin = new Thickness(0, 4, 0, 8),
                });
                break;
            case MdTable table:
                parent.Children.Add(MakeTable(table, fg, fontSize));
                break;
            default:
                // 未专门处理的块：尽量抽纯文本，避免 ToString() 打出类型名
                var fallback = ExtractPlainText(block);
                if (!string.IsNullOrWhiteSpace(fallback))
                {
                    parent.Children.Add(new TextBlock
                    {
                        Text = fallback,
                        FontSize = fontSize,
                        Foreground = fg,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 0, 0, 6),
                    });
                }
                break;
        }
    }

    private static FrameworkElement MakeTable(MdTable table, MediaBrush fg, double fontSize)
    {
        var colCount = 0;
        foreach (var rowBlock in table)
        {
            if (rowBlock is MdTableRow row)
                colCount = Math.Max(colCount, row.Count);
        }
        if (colCount == 0) return new TextBlock { Text = "", FontSize = fontSize };

        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        for (var c = 0; c < colCount; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var borderBrush = new WpfSolidColorBrush(MediaColor.FromArgb(0x33, 0x00, 0x00, 0x00));
        var headerBg = new WpfSolidColorBrush(MediaColor.FromArgb(0x14, 0x00, 0x78, 0xD4));
        var rowIndex = 0;
        foreach (var rowBlock in table)
        {
            if (rowBlock is not MdTableRow row) continue;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var isHeader = row.IsHeader;
            var col = 0;
            foreach (var cellBlock in row)
            {
                if (cellBlock is not MdTableCell cell) continue;
                var cellPanel = new StackPanel();
                foreach (var child in cell)
                    AppendBlock(cellPanel, child, fg, fontSize);
                foreach (var tb in cellPanel.Children.OfType<TextBlock>())
                {
                    tb.Margin = new Thickness(0);
                    if (isHeader) tb.FontWeight = FontWeights.SemiBold;
                }

                var cellBorder = new Border
                {
                    BorderBrush = borderBrush,
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Padding = new Thickness(6, 4, 6, 4),
                    Background = isHeader ? headerBg : null,
                    Child = cellPanel,
                };
                if (col == 0) cellBorder.BorderThickness = new Thickness(1, 0, 1, 1);
                if (rowIndex == 0) cellBorder.BorderThickness = new Thickness(
                    cellBorder.BorderThickness.Left, 1, cellBorder.BorderThickness.Right, 1);

                Grid.SetRow(cellBorder, rowIndex);
                Grid.SetColumn(cellBorder, col);
                grid.Children.Add(cellBorder);
                col++;
            }
            rowIndex++;
        }

        return new Border
        {
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Child = grid,
            ClipToBounds = true,
        };
    }

    private static string ExtractPlainText(MdBlock block)
    {
        switch (block)
        {
            case LeafBlock leaf when leaf.Inline != null:
                return InlineToPlain(leaf.Inline);
            case LeafBlock leaf when leaf.Lines.Count > 0:
                return leaf.Lines.ToString() ?? "";
            case ContainerBlock container:
            {
                var parts = new List<string>();
                foreach (var child in container)
                {
                    var t = ExtractPlainText(child);
                    if (!string.IsNullOrWhiteSpace(t)) parts.Add(t);
                }
                return string.Join("\n", parts);
            }
            default:
                return "";
        }
    }

    private static string InlineToPlain(ContainerInline container)
    {
        var sb = new System.Text.StringBuilder();
        for (var inline = container.FirstChild; inline != null; inline = inline.NextSibling)
        {
            switch (inline)
            {
                case LiteralInline lit:
                    sb.Append(lit.Content.ToString());
                    break;
                case CodeInline code:
                    sb.Append(code.Content);
                    break;
                case LineBreakInline:
                    sb.Append('\n');
                    break;
                case ContainerInline nested:
                    sb.Append(InlineToPlain(nested));
                    break;
            }
        }
        return sb.ToString();
    }

    private static string GetCodeText(MdBlock block)
    {
        if (block is LeafBlock leaf && leaf.Lines.Count > 0)
            return leaf.Lines.ToString() ?? "";
        return "";
    }

    private static TextBlock MakeParagraph(ContainerInline? inline, MediaBrush fg, double fontSize, FontWeight weight, double bottom)
    {
        var tb = new TextBlock
        {
            FontSize = fontSize,
            FontWeight = weight,
            Foreground = fg,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, bottom),
        };
        if (inline != null)
            AppendInlines(tb.Inlines, inline, fg, fontSize);
        return tb;
    }

    private static void AppendInlines(InlineCollection target, ContainerInline container, MediaBrush fg, double fontSize)
    {
        for (var inline = container.FirstChild; inline != null; inline = inline.NextSibling)
        {
            switch (inline)
            {
                case LiteralInline lit:
                    target.Add(new Run(lit.Content.ToString()) { Foreground = fg });
                    break;
                case EmphasisInline em:
                {
                    var span = new Span
                    {
                        FontWeight = em.DelimiterCount >= 2 ? FontWeights.SemiBold : FontWeights.Normal,
                        FontStyle = em.DelimiterCount == 1 ? FontStyles.Italic : FontStyles.Normal,
                        Foreground = fg,
                    };
                    AppendInlines(span.Inlines, em, fg, fontSize);
                    target.Add(span);
                    break;
                }
                case CodeInline code:
                    target.Add(new Run(code.Content)
                    {
                        FontFamily = new WpfFontFamily("Consolas, Cascadia Mono, Microsoft YaHei UI"),
                        Background = new WpfSolidColorBrush(MediaColor.FromArgb(0x14, 0x00, 0x00, 0x00)),
                        Foreground = fg,
                    });
                    break;
                case LinkInline link:
                {
                    var url = link.Url ?? "";
                    var accent = (MediaBrush)WpfApplication.Current.FindResource("Accent");
                    var uri = TryUri(url);
                    if (uri != null)
                    {
                        var hl = new Hyperlink { NavigateUri = uri, Foreground = accent };
                        AppendInlines(hl.Inlines, link, accent, fontSize);
                        if (hl.Inlines.Count == 0)
                            hl.Inlines.Add(new Run(link.Title ?? url));
                        hl.RequestNavigate += (_, e) =>
                        {
                            try
                            {
                                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                                {
                                    FileName = e.Uri.AbsoluteUri,
                                    UseShellExecute = true,
                                });
                            }
                            catch { /* ignore */ }
                            e.Handled = true;
                        };
                        target.Add(hl);
                    }
                    else
                    {
                        var span = new Span { Foreground = accent };
                        AppendInlines(span.Inlines, link, accent, fontSize);
                        if (span.Inlines.Count == 0)
                            span.Inlines.Add(new Run(url));
                        target.Add(span);
                    }
                    break;
                }
                case LineBreakInline:
                    target.Add(new LineBreak());
                    break;
                case ContainerInline nested:
                    AppendInlines(target, nested, fg, fontSize);
                    break;
                default:
                    // 其它内联：尽量 ToString
                    var t = inline.ToString();
                    if (!string.IsNullOrEmpty(t))
                        target.Add(new Run(t) { Foreground = fg });
                    break;
            }
        }
    }

    private static Uri? TryUri(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var u)
            && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeMailto))
            return u;
        return null;
    }
}

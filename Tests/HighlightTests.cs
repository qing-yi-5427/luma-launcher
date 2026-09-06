using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using LumaLauncher.Services;

namespace LumaLauncher.Tests;

internal static class HighlightTests
{
    internal static void Run()
    {
        Check("03baddd-d-DDD", "ddd", "ddd|DDD");
        Check("dddd", "ddd", "dddd");
        Check("DDDddd", "DdD", "DDDddd");
        Check("d-d-d", "ddd", "");
        Check("文档😀文档", "文档", "文档|文档");
        Check("a😀b😀", "😀", "😀|😀");
        Check("École école", "école", "École|école");
        Check("e\u0301", "e", ""); // never split a grapheme
        Check(@"C:\ddd\DDD\03baddd.txt", "ddd", "ddd|DDD|ddd");
        Check(@"C:\ddd\file.txt", @"C:\ddd", @"C:\ddd");
        Check("report FINAL report", "report final", "report|FINAL|report");
        Check("Notepad", "ntpd", "N|t|p|d");
        Check("n........t........p........d", "ntpd", "");
        Check("微信", "wx", "");
        Check("Visual Studio Code", "vsc", "");
        foreach (var query in new[] { "", "  ", "ext:ddd", "ddd | pdf", "ddd OR pdf", "!ddd", "\"ddd\"", "ddd*", "size:>1mb ddd", "regex:ddd", "<ddd>", "=ddd" })
            Check("ddd ext pdf OR size regex", query, "");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var parent = new StackPanel();
                var block = new TextBlock();
                parent.Children.Add(block);
                parent.Resources["AccentBrush"] = Brushes.OrangeRed;
                SearchHighlight.SetQuery(parent, "ddd");
                block.SetBinding(SearchHighlight.TextProperty, new Binding("Title"));
                block.DataContext = new { Title = "03baddd" };
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Assert(new TextRange(block.ContentStart, block.ContentEnd).Text == "03baddd" && Highlighted(block) == "ddd", $"initial inherited query/binding: text=[{block.Text}], hits=[{Highlighted(block)}], query=[{SearchHighlight.GetQuery(block)}], source=[{SearchHighlight.GetText(block)}]");
                var run = block.Inlines.OfType<Run>().Single(r => r.FontWeight == FontWeights.SemiBold);
                Assert(Equals(run.Foreground, Brushes.OrangeRed), "theme resource resolves");
                parent.Resources["AccentBrush"] = Brushes.DeepSkyBlue;
                Assert(Equals(run.Foreground, Brushes.DeepSkyBlue), "theme resource changes live");
                SearchHighlight.SetQuery(parent, "03");
                Assert(Highlighted(block) == "03", "query changes replace runs");
                block.DataContext = new { Title = "another item" };
                Assert(new TextRange(block.ContentStart, block.ContentEnd).Text == "another item" && Highlighted(block) == "", "recycled data context clears old runs");
                block.DataContext = new { Title = "03DDD" };
                SearchHighlight.SetQuery(parent, "ddd");
                Assert(Highlighted(block) == "DDD", "recycled item highlights new text");
                SearchHighlight.SetQuery(parent, "");
                Assert(new TextRange(block.ContentStart, block.ContentEnd).Text == "03DDD" && Highlighted(block) == "", "empty query clears all styling");
                SearchHighlight.SetText(block, "");
                Assert(block.Inlines.Count == 0, "empty text clears runs");
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure is not null) throw failure;
        Console.WriteLine("PASS highlight ranges, Unicode, syntax, inherited query, recycling and dynamic theme resources");
    }

    private static string Highlighted(TextBlock block) => string.Concat(block.Inlines.OfType<Run>()
        .Where(r => r.FontWeight == FontWeights.SemiBold).Select(r => r.Text));
    private static void Check(string text, string query, string expected) => Assert(
        string.Join("|", SearchHighlight.Find(text, query).Select(r => text.Substring(r.Start, r.Length))) == expected,
        $"text={text}, query={query}, expected={expected}");
    private static void Assert(bool value, string message)
    {
        if (!value) throw new InvalidOperationException("Highlight: " + message);
    }
}

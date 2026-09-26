namespace OneCode.App.Tui;

/// <summary>
/// Renders a minimal welcome screen on startup / after conversation reset.
/// Block-char "OneCode" logo with compact layout.
/// Model / workspace / branch live in status &amp; context bars — not duplicated here.
/// MCP 三态同理：状态栏是唯一实时出口，失败明细由 startup hint 一次性给出，
/// 欢迎页不再重复渲染（与状态栏/底部 hint 三重重复，2026-09 去重）。
/// </summary>
public static class WelcomeRenderer
{
    // Block-char letters — 5 rows, mixed-case block font.
    // Uppercase O/C: full 5-row height. Lowercase n/e/o: 3 rows bottom-aligned.
    // d has a right-side ascender spanning all 5 rows.
    private static readonly string[][] LogoLetters =
    {
        // O — 4w
        new[] { "████", "█  █", "█  █", "█  █", "████" },
        // n — 3w (lowercase, rows 2-4)
        new[] { "   ", "   ", "███", "█ █", "█ █" },
        // e — 4w (lowercase, rows 2-4)
        new[] { "    ", "    ", "████", "█   ", "████" },
        // C — 4w
        new[] { "████", "█   ", "█   ", "█   ", "████" },
        // o — 3w (lowercase, rows 2-4)
        new[] { "   ", "   ", "███", "█ █", "███" },
        // d — 4w (lowercase, ascender + body)
        new[] { "   █", "   █", "████", "█  █", "████" },
        // e — 4w (lowercase, rows 2-4)
        new[] { "    ", "    ", "████", "█   ", "████" },
    };

    private static readonly string[] PixelLogo = BuildLogo();

    private static string[] BuildLogo()
    {
        var rows = new string[5];
        for (var r = 0; r < 5; r++)
        {
            var parts = new List<string>(LogoLetters.Length);
            foreach (var letter in LogoLetters)
                parts.Add(letter[r]);
            rows[r] = string.Join(" ", parts);
        }
        return rows;
    }

    /// <summary>Render the welcome block for the given viewport.</summary>
    /// <param name="info">Product version and related welcome data.</param>
    /// <param name="viewWidth">Conversation viewport width (columns).</param>
    /// <param name="viewHeight">
    /// Conversation viewport height (rows). When &gt; 0, top padding is chosen so
    /// the welcome block sits in the upper-middle of tall / maximized terminals
    /// instead of clinging to the top edge with a huge empty void below.
    /// </param>
    public static IReadOnlyList<FormattedLine> Render(WelcomeInfo info, int viewWidth, int viewHeight = 0)
    {
        List<FormattedLine> body = [];

        // pixel logo (centered, gray blocks)
        var logoWidth = PixelLogo.Length > 0 ? TextWidthHelper.GetDisplayWidth(PixelLogo[0]) : 0;
        var logoPad = new string(' ', Math.Max(0, (viewWidth - logoWidth) / 2));

        foreach (var row in PixelLogo)
            body.Add(FormattedLine.Plain($"{logoPad}{row}", TuiPalette.FgSecondary));

        body.Add(FormattedLine.Plain("", TuiPalette.BgPrimary));

        var verText = $"v{info.Version}";
        var verPad = new string(' ', Math.Max(0, (viewWidth - TextWidthHelper.GetDisplayWidth(verText)) / 2));
        body.Add(FormattedLine.Plain($"{verPad}{verText}", TuiPalette.FgMuted));

        body.Add(FormattedLine.Plain("", TuiPalette.BgPrimary));

        // tips — discoverability only; runtime state lives in chrome bars.
        // Two rows: input basics first, then mode-direct / panel entry.
        RenderTipLine(
        [
            ("/ ", "斜杠命令"),
            ("@ ", "提及文件"),
            ("Tab ", "循环切模式"),
            ("Esc ", "中断"),
            ("/find ", "搜索会话"),
        ]);

        body.Add(FormattedLine.Plain("", TuiPalette.BgPrimary));

        RenderTipLine(
        [
            ("Alt+1..4 ", "直达模式"),
            ("/keybindings ", "快捷键面板"),
        ]);

        void RenderTipLine((string Key, string Desc)[] tips)
        {
            List<LineSegment> tipSegs = [];
            for (var i = 0; i < tips.Length; i++)
            {
                var (key, desc) = tips[i];
                tipSegs.Add(new(key, TuiPalette.Accent));
                tipSegs.Add(new(desc, TuiPalette.FgMuted));
                if (i < tips.Length - 1)
                    tipSegs.Add(new(" · ", TuiPalette.FgMuted));
            }

            var tipFullWidth = tipSegs.Sum(s => TextWidthHelper.GetDisplayWidth(s.Text));
            var tipPad = new string(' ', Math.Max(0, (viewWidth - tipFullWidth) / 2));
            tipSegs.Insert(0, new(tipPad, TuiPalette.BgPrimary));
            body.Add(FormattedLine.FromSegments(tipSegs.ToArray()));
        }

        body.Add(FormattedLine.Plain("", TuiPalette.BgPrimary));

        // Vertical placement: optical center slightly above geometric mid on tall screens.
        var topPad = 4;
        if (viewHeight > body.Count + 6)
        {
            // Leave ~40% of leftover space above so the logo sits in the upper-middle.
            topPad = Math.Max(2, (viewHeight - body.Count) * 2 / 5);
        }

        var lines = new List<FormattedLine>(topPad + body.Count);
        for (var i = 0; i < topPad; i++)
            lines.Add(FormattedLine.Plain("", TuiPalette.BgPrimary));
        lines.AddRange(body);
        return lines;
    }
}

/// <summary>Data needed to render the welcome screen.</summary>
/// <param name="Version">Product version.</param>
public sealed record WelcomeInfo(string Version);

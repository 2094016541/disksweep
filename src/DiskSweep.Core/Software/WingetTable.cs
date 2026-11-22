using System.Text;

namespace DiskSweep.Core.Software;

/// <summary>
/// winget 控制台表格的定宽解析器。
///
/// 为什么不能「按两个以上空格分列」：winget 的列宽取「表头与该列最宽单元格」的较大值，
/// 表格只有一两行短数据时，列与列之间就只剩 <b>1 个空格</b>——
/// 实测 <c>winget search --query Apifox</c> 输出的是
/// <c>Apifox Ruihu.Apifox 2.8.55 winget</c>，整行没有任何一处双空格，
/// 按旧规则会得到 0 行，于是「自动匹配 winget 包」永远失败。
///
/// 真正稳定的依据是表头各列的起始位置：winget 会先把表头补齐到列宽，所以表头的列起点
/// 就是数据行的列起点。注意它按<b>显示宽度</b>对齐——中文表头占两列，
/// 用字符串下标切会整体错位，这里逐字符累加显示宽度来切。
///
/// 另一个坑是折行：控制台宽度不够时 winget 会把一行硬切成两段（连 ID 都会断在词中间）。
/// 这里靠「ID 列为空」「末列为空且整行已顶到表宽」两条判据把断行拼回去。
/// </summary>
public static class WingetTable
{
    /// <summary>一张解析好的表：表头文本 + 每行的单元格（列数与表头一致）。</summary>
    public sealed record WingetTableData(
        IReadOnlyList<string> Header, IReadOnlyList<IReadOnlyList<string>> Rows)
    {
        /// <summary>按别名找列下标（不区分大小写）；找不到返回 -1。</summary>
        public int Column(params string[] aliases)
        {
            foreach (string alias in aliases)
                for (int i = 0; i < Header.Count; i++)
                    if (Header[i].Equals(alias, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        /// <summary>安全取单元格：列下标为 -1 或越界时返回空串。</summary>
        public static string Cell(IReadOnlyList<string> row, int column) =>
            column >= 0 && column < row.Count ? row[column] : "";
    }

    /// <summary>找不到表头时返回 null，由调用方退回旧的「按多空格分列」解析。</summary>
    public static WingetTableData? Parse(string? output)
    {
        string[] lines = (output ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        int headerIndex = -1;
        List<(int Start, string Text)>? header = null;
        for (int i = 0; i < lines.Length; i++)
        {
            var tokens = Tokenize(lines[i]);
            // winget 的 ID 列在中文/英文界面里都叫 ID，是最稳的表头锚点。
            if (tokens.Count >= 3 &&
                tokens.Any(t => t.Text.Equals("ID", StringComparison.OrdinalIgnoreCase)))
            {
                headerIndex = i;
                header = tokens;
                break;
            }
        }
        if (header is null) return null;

        int[] starts = header.Select(t => t.Start).ToArray();
        int idIndex = header.FindIndex(t => t.Text.Equals("ID", StringComparison.OrdinalIgnoreCase));

        // 表宽 = 数据行里最长的行宽：折行的两段都会顶到这个宽度。
        int wrapWidth = 0;
        for (int i = headerIndex + 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Trim().Length > 0 && !IsSeparator(line) && line.Length > wrapWidth)
                wrapWidth = line.Length;
        }

        var rows = new List<IReadOnlyList<string>>();
        string? pending = null;
        for (int i = headerIndex + 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Trim().Length == 0 || IsSeparator(line)) continue;

            bool merged = pending is not null;
            string text = merged ? pending + line.TrimStart() : line;
            var cells = Slice(text, starts);

            // ID 列空着 → 这是被折到下一行的续段，继续往后拼。
            if (cells[idIndex].Trim().Length == 0)
            {
                pending = text;
                continue;
            }
            // 末列空着且整行顶到表宽 → 行被切断，等下一段拼完整再判断。
            if (!merged && cells[^1].Trim().Length == 0 && wrapWidth > 0 && text.Length >= wrapWidth)
            {
                pending = text;
                continue;
            }

            rows.Add(cells);
            pending = null;
        }

        return new WingetTableData(header.Select(t => t.Text).ToArray(), rows);
    }

    /// <summary>把一行按表头的列起点切成若干单元格，按显示宽度推进（中文/全角算两列）。</summary>
    private static string[] Slice(string line, int[] starts)
    {
        var builders = new StringBuilder[starts.Length];
        for (int i = 0; i < builders.Length; i++) builders[i] = new StringBuilder();

        int column = 0;
        foreach (char c in line)
        {
            int index = IndexAt(starts, column);
            if (index >= 0 && c != '\t') builders[index].Append(c);
            column += CharWidth(c);
        }

        var cells = new string[starts.Length];
        for (int i = 0; i < cells.Length; i++) cells[i] = builders[i].ToString().Trim();
        return cells;
    }

    /// <summary>显示列 → 所属列下标；落在列间空白里也算前一列（随后会被 Trim 掉）。</summary>
    private static int IndexAt(int[] starts, int displayColumn)
    {
        int index = starts.Length - 1;
        while (index > 0 && starts[index] > displayColumn) index--;
        return starts[index] <= displayColumn ? index : -1;
    }

    private static List<(int Start, string Text)> Tokenize(string line)
    {
        var tokens = new List<(int, string)>();
        var buffer = new StringBuilder();
        int column = 0, start = 0;
        foreach (char c in line)
        {
            if (char.IsWhiteSpace(c))
            {
                if (buffer.Length > 0)
                {
                    tokens.Add((start, buffer.ToString()));
                    buffer.Clear();
                }
            }
            else
            {
                if (buffer.Length == 0) start = column;
                buffer.Append(c);
            }
            column += CharWidth(c);
        }
        if (buffer.Length > 0) tokens.Add((start, buffer.ToString()));
        return tokens;
    }

    /// <summary>CJK / 全角字符占两列，其余（含拉丁字母与数字）占一列。</summary>
    private static int CharWidth(char c) => c switch
    {
        < '\u1100' => 1,
        >= '\u1100' and <= '\u115F' => 2,   // 谚文字母
        >= '\u2E80' and <= '\u303E' => 2,   // CJK 部首、标点、假名
        >= '\u3041' and <= '\u33FF' => 2,
        >= '\u3400' and <= '\u4DBF' => 2,   // CJK 扩展 A
        >= '\u4E00' and <= '\u9FFF' => 2,   // CJK 统一表意文字
        >= '\uA000' and <= '\uA4CF' => 2,   // 彝文
        >= '\uAC00' and <= '\uD7A3' => 2,   // 谚文音节
        >= '\uF900' and <= '\uFAFF' => 2,   // CJK 兼容表意文字
        >= '\uFE30' and <= '\uFE6F' => 2,   // CJK 兼容形式
        >= '\uFF00' and <= '\uFF60' => 2,   // 全角 ASCII
        >= '\uFFE0' and <= '\uFFE6' => 2,   // 全角符号
        _ => 1
    };

    private static bool IsSeparator(string line) => line.All(c => c is '-' or '=' or ' ' or '\t');
}

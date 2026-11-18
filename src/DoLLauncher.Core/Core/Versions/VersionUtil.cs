namespace DoLLauncher.Core.Versions;

/// <summary>版本号比较与依赖约束判断（只覆盖 DoL 模组常见的几种写法）。</summary>
public static class VersionUtil
{
    /// <summary>从 "0.5.12.13-v2.101.1" 这样的目录名里取游戏版本。</summary>
    public static string? FromFolderName(string folder)
    {
        var m = System.Text.RegularExpressions.Regex.Match(folder, @"\b([0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?)\b");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>比较两个版本串，返回 -1 / 0 / 1。非数字部分按数字段比较，缺失段补 0。</summary>
    public static int Compare(string a, string b)
    {
        var pa = Split(a);
        var pb = Split(b);
        var n = Math.Max(pa.Count, pb.Count);
        for (var i = 0; i < n; i++)
        {
            var x = i < pa.Count ? pa[i] : 0;
            var y = i < pb.Count ? pb[i] : 0;
            if (x != y) return x < y ? -1 : 1;
        }
        return 0;
    }

    private static List<int> Split(string v)
    {
        var list = new List<int>();
        var part = string.Empty;
        foreach (var ch in v.Trim())
        {
            if (char.IsDigit(ch))
            {
                part += ch;
            }
            else if (part.Length > 0)
            {
                list.Add(int.TryParse(part, out var n) ? n : 0);
                part = string.Empty;
                if (ch == '-' || ch == '+') break;
            }
        }
        if (part.Length > 0) list.Add(int.TryParse(part, out var n) ? n : 0);
        return list;
    }

    /// <summary>判断 actual 是否满足 constraint（形如 "=1.2.3"、">=1.0"、"^1.2.0"、"~1.2.0"）。</summary>
    public static bool Satisfies(string? constraint, string? actual)
    {
        if (string.IsNullOrWhiteSpace(constraint)) return true;
        if (string.IsNullOrWhiteSpace(actual)) return false;

        var c = constraint.Trim();
        var op = "=";
        foreach (var candidate in new[] { ">=", "<=", "^", "~", ">", "<", "=" })
        {
            if (c.StartsWith(candidate, StringComparison.Ordinal))
            {
                op = candidate;
                c = c[candidate.Length..].Trim();
                break;
            }
        }

        if (c.Length == 0) return true;
        var cmp = Compare(actual, c);
        var majorMinor = Split(c);
        var actualParts = Split(actual);

        switch (op)
        {
            case ">=": return cmp >= 0;
            case "<=": return cmp < 0 || cmp == 0;
            case ">": return cmp > 0;
            case "<": return cmp < 0;
            case "^":
                // DoL 模组把 ^ 当作「不低于这个版本」的宽松写法，不限制主版本：
                // 前置写 ^3.0.0、实际装 5.6.2 也算满足。
                return cmp >= 0;
            case "~":
                if (cmp < 0) return false;
                var am = actualParts.Count > 0 ? actualParts[0] : 0;
                var an = actualParts.Count > 1 ? actualParts[1] : 0;
                var cm = majorMinor.Count > 0 ? majorMinor[0] : 0;
                var cn = majorMinor.Count > 1 ? majorMinor[1] : 0;
                return am == cm && an == cn;
            default: return cmp == 0;
        }
    }
}

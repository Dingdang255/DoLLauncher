using System.Globalization;
using System.Text;

namespace DoLLauncher.Core.Skin;

/// <summary>
/// 轻量脚本值：只支持 null / 布尔 / 数字 / 文本四种类型。
/// 由平台层提供数据（<see cref="ISkinScriptHost.ResolveData"/>），脚本本身不能访问文件、网络或反射。
/// </summary>
public readonly struct SkinScriptValue : IEquatable<SkinScriptValue>
{
    public enum Kind
    {
        Null,
        Bool,
        Number,
        Text,
    }

    private readonly object? _value;

    private SkinScriptValue(Kind kind, object? value)
    {
        ValueKind = kind;
        _value = value;
    }

    public Kind ValueKind { get; }

    public bool IsNull => ValueKind == Kind.Null;

    public static SkinScriptValue Null => new(Kind.Null, null);

    public static SkinScriptValue From(bool value) => new(Kind.Bool, value);

    public static SkinScriptValue From(double value) => new(Kind.Number, value);

    public static SkinScriptValue From(string? value)
        => value == null ? Null : new SkinScriptValue(Kind.Text, value);

    public bool AsBool() => ValueKind switch
    {
        Kind.Bool => (bool)_value!,
        Kind.Number => Math.Abs((double)_value!) > double.Epsilon,
        Kind.Text => AsBoolText((string)_value!),
        _ => false,
    };

    public double AsNumber() => ValueKind switch
    {
        Kind.Number => (double)_value!,
        Kind.Bool => (bool)_value! ? 1 : 0,
        Kind.Text => double.TryParse((string)_value!, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0,
        _ => 0,
    };

    public string AsText() => ValueKind switch
    {
        Kind.Text => (string)_value!,
        Kind.Bool => (bool)_value! ? "true" : "false",
        Kind.Number => FormatNumber((double)_value!),
        _ => string.Empty,
    };

    private static string FormatNumber(double number)
    {
        if (double.IsNaN(number) || double.IsInfinity(number)) return "0";
        return Math.Abs(number - Math.Round(number)) < 1e-9
            ? ((long)Math.Round(number)).ToString(CultureInfo.InvariantCulture)
            : number.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static bool AsBoolText(string text)
    {
        var value = text.Trim();
        if (value.Length == 0) return false;
        return value.ToLowerInvariant() switch
        {
            "false" or "0" or "off" or "no" or "null" => false,
            _ => true,
        };
    }

    public bool Equals(SkinScriptValue other)
    {
        if (ValueKind == Kind.Number && other.ValueKind == Kind.Number)
            return Math.Abs(AsNumber() - other.AsNumber()) < 1e-9;
        if (ValueKind == Kind.Null || other.ValueKind == Kind.Null)
            return ValueKind == other.ValueKind;
        if (ValueKind == Kind.Bool || other.ValueKind == Kind.Bool)
            return AsBool() == other.AsBool();
        return string.Equals(AsText(), other.AsText(), StringComparison.Ordinal);
    }

    public override bool Equals(object? obj) => obj is SkinScriptValue other && Equals(other);

    public override int GetHashCode() => AsText().GetHashCode(StringComparison.Ordinal);

    public override string ToString() => AsText();
}

/// <summary>
/// 脚本动作与数据的白名单宿主。所有副作用都必须经过该接口，
/// 引擎本身不持有任何文件、网络或 UI 能力。
/// </summary>
public interface ISkinScriptHost
{
    /// <summary>读取内置数据或变量，例如 <c>version.count</c>、<c>vars.page</c>；未知路径返回 null 值。</summary>
    SkinScriptValue ResolveData(string path);

    /// <summary>执行白名单动作；返回值仅用于日志，失败不影响后续语句。</summary>
    bool InvokeAction(string action, IReadOnlyList<SkinScriptValue> args);
}

/// <summary>
/// 轻量脚本（dolskin/3 的 <c>events</c>）：不带循环、不嵌 JS，
/// 语法固定为 <c>let</c> / 赋值 / <c>if…else</c> / 表达式语句。
/// </summary>
public static class SkinScriptEngine
{
    public const int MaxSourceBytes = 16 * 1024;
    public const int MaxStatements = 200;
    public const int MaxExpressionDepth = 32;
    public const int MaxCallArguments = 6;

    /// <summary>脚本里允许当作语句调用的动作名（其余函数名按纯函数处理）。</summary>
    public static readonly string[] Actions =
    {
        "setTheme", "setMotion", "setParticles", "setOrnament",
        "openPage", "openSettings", "show", "hide", "toggle",
        "setVar", "launch", "openDir", "toast",
    };

    public static bool IsAction(string? name)
        => Actions.Any(item => item.Equals((name ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>解析整段脚本；失败时返回中文原因。</summary>
    public static bool TryParse(string? source, out SkinScriptProgram program, out string error)
    {
        program = SkinScriptProgram.Empty;
        error = string.Empty;

        var text = source ?? string.Empty;
        if (Encoding.UTF8.GetByteCount(text) > MaxSourceBytes)
        {
            error = $"脚本超过 {MaxSourceBytes / 1024}KB 上限。";
            return false;
        }

        try
        {
            var parser = new Parser(text);
            var statements = parser.ParseProgram();
            if (statements.Count > MaxStatements)
            {
                error = $"脚本语句超过 {MaxStatements} 条上限。";
                return false;
            }

            program = new SkinScriptProgram(statements);
            return true;
        }
        catch (ScriptException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>只解析一个表达式（用于 <c>{{ … }}</c> 数据绑定）。</summary>
    public static bool TryParseExpression(string? source, out SkinScriptExpression expression, out string error)
    {
        expression = SkinScriptExpression.Empty;
        error = string.Empty;

        var text = (source ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            error = "表达式为空。";
            return false;
        }

        try
        {
            var parser = new Parser(text);
            var node = parser.ParseSingleExpression();
            expression = new SkinScriptExpression(node);
            return true;
        }
        catch (ScriptException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    // ---------- 语法树 ----------

    private sealed class ScriptException : Exception
    {
        public ScriptException(string message) : base(message)
        {
        }
    }

    internal sealed class EvalContext
    {
        public Dictionary<string, SkinScriptValue> Locals { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    internal abstract class Node
    {
        public abstract SkinScriptValue Evaluate(ISkinScriptHost? host, EvalContext context, bool allowActions);
    }

    private sealed class LiteralNode : Node
    {
        private readonly SkinScriptValue _value;

        public LiteralNode(SkinScriptValue value) => _value = value;

        public override SkinScriptValue Evaluate(ISkinScriptHost? host, EvalContext context, bool allowActions) => _value;
    }

    private sealed class DataNode : Node
    {
        public string Path { get; }

        public DataNode(string path) => Path = path;

        public override SkinScriptValue Evaluate(ISkinScriptHost? host, EvalContext context, bool allowActions)
        {
            if (context.Locals.TryGetValue(Path, out var local)) return local;

            var dot = Path.IndexOf('.');
            if (dot > 0 && context.Locals.TryGetValue(Path[..dot], out var root))
            {
                return Path[(dot + 1)..].ToLowerInvariant() switch
                {
                    "length" or "len" => SkinScriptValue.From(root.AsText().Length),
                    _ => SkinScriptValue.Null,
                };
            }

            return host?.ResolveData(Path) ?? SkinScriptValue.Null;
        }
    }

    private sealed class UnaryNode : Node
    {
        private readonly string _operator;
        private readonly Node _operand;

        public UnaryNode(string op, Node operand)
        {
            _operator = op;
            _operand = operand;
        }

        public override SkinScriptValue Evaluate(ISkinScriptHost? host, EvalContext context, bool allowActions)
        {
            var value = _operand.Evaluate(host, context, allowActions);
            return _operator switch
            {
                "!" => SkinScriptValue.From(!value.AsBool()),
                "-" => SkinScriptValue.From(-value.AsNumber()),
                _ => value,
            };
        }
    }

    private sealed class BinaryNode : Node
    {
        private readonly string _operator;
        private readonly Node _left;
        private readonly Node _right;

        public BinaryNode(string op, Node left, Node right)
        {
            _operator = op;
            _left = left;
            _right = right;
        }

        public override SkinScriptValue Evaluate(ISkinScriptHost? host, EvalContext context, bool allowActions)
        {
            if (_operator == "&&")
            {
                var leftBool = _left.Evaluate(host, context, allowActions);
                return SkinScriptValue.From(leftBool.AsBool() && _right.Evaluate(host, context, allowActions).AsBool());
            }

            if (_operator == "||")
            {
                var leftBool = _left.Evaluate(host, context, allowActions);
                return SkinScriptValue.From(leftBool.AsBool() || _right.Evaluate(host, context, allowActions).AsBool());
            }

            var left = _left.Evaluate(host, context, allowActions);
            var right = _right.Evaluate(host, context, allowActions);

            switch (_operator)
            {
                case "==": return SkinScriptValue.From(left.Equals(right));
                case "!=": return SkinScriptValue.From(!left.Equals(right));
                case "<": return SkinScriptValue.From(left.AsNumber() < right.AsNumber());
                case "<=": return SkinScriptValue.From(left.AsNumber() <= right.AsNumber());
                case ">": return SkinScriptValue.From(left.AsNumber() > right.AsNumber());
                case ">=": return SkinScriptValue.From(left.AsNumber() >= right.AsNumber());
                case "+":
                    if (left.ValueKind == SkinScriptValue.Kind.Text || right.ValueKind == SkinScriptValue.Kind.Text)
                        return SkinScriptValue.From(left.AsText() + right.AsText());
                    return SkinScriptValue.From(left.AsNumber() + right.AsNumber());
                case "-": return SkinScriptValue.From(left.AsNumber() - right.AsNumber());
                case "*": return SkinScriptValue.From(left.AsNumber() * right.AsNumber());
                case "/":
                {
                    var divisor = right.AsNumber();
                    return SkinScriptValue.From(Math.Abs(divisor) < double.Epsilon ? 0 : left.AsNumber() / divisor);
                }
                case "%":
                {
                    var divisor = right.AsNumber();
                    return SkinScriptValue.From(Math.Abs(divisor) < double.Epsilon ? 0 : left.AsNumber() % divisor);
                }
                default: return SkinScriptValue.Null;
            }
        }
    }

    private sealed class TernaryNode : Node
    {
        private readonly Node _condition;
        private readonly Node _whenTrue;
        private readonly Node _whenFalse;

        public TernaryNode(Node condition, Node whenTrue, Node whenFalse)
        {
            _condition = condition;
            _whenTrue = whenTrue;
            _whenFalse = whenFalse;
        }

        public override SkinScriptValue Evaluate(ISkinScriptHost? host, EvalContext context, bool allowActions)
            => _condition.Evaluate(host, context, allowActions).AsBool()
                ? _whenTrue.Evaluate(host, context, allowActions)
                : _whenFalse.Evaluate(host, context, allowActions);
    }

    private sealed class CallNode : Node
    {
        private readonly string _name;
        private readonly List<Node> _arguments;

        public CallNode(string name, List<Node> arguments)
        {
            _name = name;
            _arguments = arguments;
        }

        public override SkinScriptValue Evaluate(ISkinScriptHost? host, EvalContext context, bool allowActions)
        {
            var arguments = new List<SkinScriptValue>(_arguments.Count);
            foreach (var argument in _arguments)
                arguments.Add(argument.Evaluate(host, context, allowActions));

            var name = _name.ToLowerInvariant();
            if (IsAction(name))
            {
                if (!allowActions || host == null) return SkinScriptValue.Null;
                return SkinScriptValue.From(host.InvokeAction(name, arguments));
            }

            if (host == null) return SkinScriptValue.Null;

            switch (name)
            {
                case "getvar":
                    return arguments.Count > 0
                        ? host.ResolveData("vars." + arguments[0].AsText().Trim())
                        : SkinScriptValue.Null;

                case "str":
                    return SkinScriptValue.From(arguments.Count > 0 ? arguments[0].AsText() : string.Empty);

                case "num":
                    if (arguments.Count == 0) return SkinScriptValue.From(0d);
                    if (arguments.Count > 1 && arguments[0].ValueKind == SkinScriptValue.Kind.Text
                        && !double.TryParse(arguments[0].AsText(), NumberStyles.Float,
                            CultureInfo.InvariantCulture, out _))
                        return SkinScriptValue.From(arguments[1].AsNumber());
                    return SkinScriptValue.From(arguments[0].AsNumber());

                case "bool":
                    return SkinScriptValue.From(arguments.Count > 0 && arguments[0].AsBool());

                case "len":
                    return SkinScriptValue.From(arguments.Count > 0 ? arguments[0].AsText().Length : 0);

                case "lower":
                    return SkinScriptValue.From(
                        (arguments.Count > 0 ? arguments[0].AsText() : string.Empty).ToLowerInvariant());

                case "upper":
                    return SkinScriptValue.From(
                        (arguments.Count > 0 ? arguments[0].AsText() : string.Empty).ToUpperInvariant());

                case "round":
                {
                    var value = arguments.Count > 0 ? arguments[0].AsNumber() : 0;
                    var digits = arguments.Count > 1 ? (int)Math.Clamp(arguments[1].AsNumber(), 0, 6) : 0;
                    return SkinScriptValue.From(Math.Round(value, digits));
                }

                case "clamp":
                {
                    var value = arguments.Count > 0 ? arguments[0].AsNumber() : 0;
                    var min = arguments.Count > 1 ? arguments[1].AsNumber() : 0;
                    var max = arguments.Count > 2 ? arguments[2].AsNumber() : 100;
                    return SkinScriptValue.From(Math.Clamp(value, min, max));
                }

                case "min":
                    return SkinScriptValue.From(arguments.Count == 0 ? 0 : arguments.Min(item => item.AsNumber()));

                case "max":
                    return SkinScriptValue.From(arguments.Count == 0 ? 0 : arguments.Max(item => item.AsNumber()));

                case "coalesce":
                    foreach (var argument in arguments)
                        if (!argument.IsNull && argument.AsText().Length > 0) return argument;
                    return SkinScriptValue.Null;

                default:
                    return SkinScriptValue.Null;
            }
        }
    }

    internal abstract class Statement
    {
        public abstract void Execute(ISkinScriptHost? host, EvalContext context, bool allowActions);
    }

    private sealed class LetStatement : Statement
    {
        private readonly string _name;
        private readonly Node _value;

        public LetStatement(string name, Node value)
        {
            _name = name;
            _value = value;
        }

        public override void Execute(ISkinScriptHost? host, EvalContext context, bool allowActions)
            => context.Locals[_name] = _value.Evaluate(host, context, allowActions);
    }

    private sealed class AssignStatement : Statement
    {
        private readonly string _name;
        private readonly Node _value;

        public AssignStatement(string name, Node value)
        {
            _name = name;
            _value = value;
        }

        public override void Execute(ISkinScriptHost? host, EvalContext context, bool allowActions)
        {
            var result = _value.Evaluate(host, context, allowActions);
            if (context.Locals.ContainsKey(_name)) context.Locals[_name] = result;
            else if (allowActions) host?.InvokeAction("setvar", new[] { SkinScriptValue.From(_name), result });
        }
    }

    private sealed class ExpressionStatement : Statement
    {
        private readonly Node _value;

        public ExpressionStatement(Node value) => _value = value;

        public override void Execute(ISkinScriptHost? host, EvalContext context, bool allowActions)
            => _value.Evaluate(host, context, allowActions);
    }

    private sealed class IfStatement : Statement
    {
        private readonly Node _condition;
        private readonly List<Statement> _then;
        private readonly List<Statement>? _otherwise;

        public IfStatement(Node condition, List<Statement> then, List<Statement>? otherwise)
        {
            _condition = condition;
            _then = then;
            _otherwise = otherwise;
        }

        public override void Execute(ISkinScriptHost? host, EvalContext context, bool allowActions)
        {
            if (_condition.Evaluate(host, context, allowActions).AsBool())
            {
                foreach (var statement in _then) statement.Execute(host, context, allowActions);
            }
            else if (_otherwise != null)
            {
                foreach (var statement in _otherwise) statement.Execute(host, context, allowActions);
            }
        }
    }

    // ---------- 词法 ----------

    private enum TokenKind
    {
        End,
        Identifier,
        Number,
        String,
        Operator,
        Punctuation,
    }

    private readonly record struct Token(TokenKind Kind, string Text, double Number);

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var index = 0;

        while (index < text.Length)
        {
            var c = text[index];
            if (char.IsWhiteSpace(c))
            {
                index++;
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                var start = index++;
                while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] == '_')) index++;
                tokens.Add(new Token(TokenKind.Identifier, text[start..index], 0));
                continue;
            }

            if (char.IsDigit(c) || (c == '.' && index + 1 < text.Length && char.IsDigit(text[index + 1])))
            {
                var start = index++;
                while (index < text.Length && (char.IsDigit(text[index]) || text[index] == '.')) index++;
                var raw = text[start..index];
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    throw new ScriptException($"无效的数字：{raw}");
                tokens.Add(new Token(TokenKind.Number, raw, number));
                continue;
            }

            if (c is '"' or '\'')
            {
                index++;
                var builder = new StringBuilder();
                var closed = false;
                while (index < text.Length)
                {
                    var current = text[index++];
                    if (current == c)
                    {
                        closed = true;
                        break;
                    }
                    if (current == '\\' && index < text.Length)
                    {
                        var escaped = text[index++];
                        builder.Append(escaped switch
                        {
                            'n' => '\n',
                            't' => '\t',
                            'r' => '\r',
                            _ => escaped,
                        });
                        continue;
                    }
                    builder.Append(current);
                }
                if (!closed) throw new ScriptException("字符串缺少结束引号。");
                tokens.Add(new Token(TokenKind.String, builder.ToString(), 0));
                continue;
            }

            var matched = false;
            foreach (var op in new[] { "==", "!=", "<=", ">=", "&&", "||" })
            {
                if (string.CompareOrdinal(text, index, op, 0, op.Length) != 0) continue;
                tokens.Add(new Token(TokenKind.Operator, op, 0));
                index += op.Length;
                matched = true;
                break;
            }
            if (matched) continue;

            if ("+-*/%!<>=".Contains(c))
            {
                tokens.Add(new Token(TokenKind.Operator, c.ToString(), 0));
                index++;
                continue;
            }

            if ("(){};,:?.".Contains(c))
            {
                tokens.Add(new Token(TokenKind.Punctuation, c.ToString(), 0));
                index++;
                continue;
            }

            throw new ScriptException($"不支持的字符：{c}");
        }

        if (tokens.Count > 4096) throw new ScriptException("脚本内容过长。");
        tokens.Add(new Token(TokenKind.End, string.Empty, 0));
        return tokens;
    }

    // ---------- 递归下降解析 ----------

    private sealed class Parser
    {
        private readonly List<Token> _tokens;
        private int _index;
        private int _depth;
        private int _statementCount;

        public Parser(string text)
        {
            _tokens = Tokenize(text);
        }

        private Token Current => _tokens[_index];

        public List<Statement> ParseProgram()
        {
            var statements = new List<Statement>();
            while (Current.Kind != TokenKind.End) statements.Add(ParseStatement());
            return statements;
        }

        public Node ParseSingleExpression()
        {
            var node = ParseExpression();
            if (Current.Kind != TokenKind.End) throw new ScriptException("表达式后存在多余内容。");
            return node;
        }

        private Statement ParseStatement()
        {
            if (++_statementCount > MaxStatements) throw new ScriptException($"脚本语句超过 {MaxStatements} 条上限。");

            if (IsIdentifier("let"))
            {
                Advance();
                var name = ExpectIdentifier();
                ExpectOperator("=");
                var value = ParseExpression();
                ExpectPunctuation(";");
                return new LetStatement(name, value);
            }

            if (IsIdentifier("if"))
            {
                Advance();
                ExpectPunctuation("(");
                var condition = ParseExpression();
                ExpectPunctuation(")");
                var then = ParseBlock();
                List<Statement>? otherwise = null;
                if (IsIdentifier("else"))
                {
                    Advance();
                    otherwise = IsIdentifier("if") ? new List<Statement> { ParseStatement() } : ParseBlock();
                }
                return new IfStatement(condition, then, otherwise);
            }

            if (Current.Kind == TokenKind.Identifier
                && _tokens[_index + 1].Kind == TokenKind.Operator
                && _tokens[_index + 1].Text == "=")
            {
                var name = ExpectIdentifier();
                ExpectOperator("=");
                var value = ParseExpression();
                ExpectPunctuation(";");
                return new AssignStatement(name, value);
            }

            var expression = ParseExpression();
            ExpectPunctuation(";");
            return new ExpressionStatement(expression);
        }

        private List<Statement> ParseBlock()
        {
            ExpectPunctuation("{");
            var statements = new List<Statement>();
            while (!IsPunctuation("}"))
            {
                if (Current.Kind == TokenKind.End) throw new ScriptException("代码块缺少右花括号。");
                statements.Add(ParseStatement());
            }
            ExpectPunctuation("}");
            return statements;
        }

        private Node ParseExpression()
        {
            if (++_depth > MaxExpressionDepth) throw new ScriptException($"表达式嵌套超过 {MaxExpressionDepth} 层上限。");
            var node = ParseTernary();
            _depth--;
            return node;
        }

        private Node ParseTernary()
        {
            var condition = ParseOr();
            if (!IsPunctuation("?")) return condition;
            Advance();
            var whenTrue = ParseExpression();
            ExpectPunctuation(":");
            var whenFalse = ParseExpression();
            return new TernaryNode(condition, whenTrue, whenFalse);
        }

        private Node ParseOr()
        {
            var node = ParseAnd();
            while (IsOperator("||"))
            {
                Advance();
                node = new BinaryNode("||", node, ParseAnd());
            }
            return node;
        }

        private Node ParseAnd()
        {
            var node = ParseEquality();
            while (IsOperator("&&"))
            {
                Advance();
                node = new BinaryNode("&&", node, ParseEquality());
            }
            return node;
        }

        private Node ParseEquality()
        {
            var node = ParseComparison();
            while (IsOperator("==") || IsOperator("!="))
            {
                var op = Current.Text;
                Advance();
                node = new BinaryNode(op, node, ParseComparison());
            }
            return node;
        }

        private Node ParseComparison()
        {
            var node = ParseAdditive();
            while (IsOperator("<") || IsOperator("<=") || IsOperator(">") || IsOperator(">="))
            {
                var op = Current.Text;
                Advance();
                node = new BinaryNode(op, node, ParseAdditive());
            }
            return node;
        }

        private Node ParseAdditive()
        {
            var node = ParseMultiplicative();
            while (IsOperator("+") || IsOperator("-"))
            {
                var op = Current.Text;
                Advance();
                node = new BinaryNode(op, node, ParseMultiplicative());
            }
            return node;
        }

        private Node ParseMultiplicative()
        {
            var node = ParseUnary();
            while (IsOperator("*") || IsOperator("/") || IsOperator("%"))
            {
                var op = Current.Text;
                Advance();
                node = new BinaryNode(op, node, ParseUnary());
            }
            return node;
        }

        private Node ParseUnary()
        {
            if (IsOperator("!") || IsOperator("-") || IsOperator("+"))
            {
                var op = Current.Text;
                Advance();
                return new UnaryNode(op, ParseUnary());
            }
            return ParsePostfix();
        }

        private Node ParsePostfix()
        {
            var node = ParsePrimary();
            while (IsPunctuation("."))
            {
                Advance();
                var property = ExpectIdentifier();
                if (node is DataNode data) node = new DataNode(data.Path + "." + property);
                else node = new DataNode(property);
            }
            return node;
        }

        private Node ParsePrimary()
        {
            switch (Current.Kind)
            {
                case TokenKind.Number:
                {
                    var number = Current.Number;
                    Advance();
                    return new LiteralNode(SkinScriptValue.From(number));
                }

                case TokenKind.String:
                {
                    var text = Current.Text;
                    Advance();
                    return new LiteralNode(SkinScriptValue.From(text));
                }

                case TokenKind.Identifier:
                {
                    var name = Current.Text;
                    if (name.Equals("true", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("false", StringComparison.OrdinalIgnoreCase))
                    {
                        Advance();
                        return new LiteralNode(
                            SkinScriptValue.From(name.Equals("true", StringComparison.OrdinalIgnoreCase)));
                    }
                    if (name.Equals("null", StringComparison.OrdinalIgnoreCase))
                    {
                        Advance();
                        return new LiteralNode(SkinScriptValue.Null);
                    }

                    Advance();
                    if (!IsPunctuation("(")) return new DataNode(name);

                    Advance();
                    var arguments = new List<Node>();
                    if (!IsPunctuation(")"))
                    {
                        while (true)
                        {
                            arguments.Add(ParseExpression());
                            if (arguments.Count > MaxCallArguments)
                                throw new ScriptException($"函数参数超过 {MaxCallArguments} 个上限。");
                            if (!IsPunctuation(",")) break;
                            Advance();
                        }
                    }
                    ExpectPunctuation(")");
                    return new CallNode(name, arguments);
                }

                default:
                    if (!IsPunctuation("(")) throw new ScriptException("表达式中缺少操作数。");
                    Advance();
                    var inner = ParseExpression();
                    ExpectPunctuation(")");
                    return inner;
            }
        }

        private bool IsIdentifier(string name)
            => Current.Kind == TokenKind.Identifier && Current.Text.Equals(name, StringComparison.OrdinalIgnoreCase);

        private bool IsPunctuation(string text)
            => Current.Kind == TokenKind.Punctuation && Current.Text == text;

        private bool IsOperator(string text)
            => Current.Kind == TokenKind.Operator && Current.Text == text;

        private void Advance() => _index++;

        private string ExpectIdentifier()
        {
            if (Current.Kind != TokenKind.Identifier) throw new ScriptException("此处应为标识符。");
            var name = Current.Text;
            Advance();
            return name;
        }

        private void ExpectPunctuation(string text)
        {
            if (!IsPunctuation(text)) throw new ScriptException($"此处应为 “{text}”，实际为 “{Current.Text}”。");
            Advance();
        }

        private void ExpectOperator(string text)
        {
            if (!IsOperator(text)) throw new ScriptException($"此处应为 “{text}”，实际为 “{Current.Text}”。");
            Advance();
        }
    }
}

/// <summary>已解析的脚本表达式（只读、可重复求值）。</summary>
public sealed class SkinScriptExpression
{
    internal static readonly SkinScriptExpression Empty = new(null);

    private readonly SkinScriptEngine.Node? _node;

    internal SkinScriptExpression(SkinScriptEngine.Node? node) => _node = node;

    public bool IsEmpty => _node == null;

    /// <summary>求值；<paramref name="allowActions"/> 为 false 时动作调用一律返回 null。</summary>
    public SkinScriptValue Evaluate(ISkinScriptHost? host, bool allowActions = false)
        => _node?.Evaluate(host, new SkinScriptEngine.EvalContext(), allowActions) ?? SkinScriptValue.Null;
}

/// <summary>已解析的脚本；<see cref="Run"/> 可重复执行，每次执行使用独立的局部变量表。</summary>
public sealed class SkinScriptProgram
{
    internal static readonly SkinScriptProgram Empty = new(Array.Empty<SkinScriptEngine.Statement>());

    private readonly IReadOnlyList<SkinScriptEngine.Statement> _statements;

    internal SkinScriptProgram(IReadOnlyList<SkinScriptEngine.Statement> statements) => _statements = statements;

    public bool IsEmpty => _statements.Count == 0;

    /// <summary>执行脚本；任何运行时异常都会被吞掉并返回 false。</summary>
    public bool Run(ISkinScriptHost? host)
    {
        if (host == null || _statements.Count == 0) return false;

        try
        {
            var context = new SkinScriptEngine.EvalContext();
            foreach (var statement in _statements) statement.Execute(host, context, true);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary><c>{{ 表达式 }}</c> 数据绑定：文本模板 + 只读表达式，不允许动作。</summary>
public sealed class SkinBindingTemplate
{
    private readonly List<Segment> _segments = new();

    private readonly record struct Segment(string Text, SkinScriptExpression? Expression);

    public bool IsEmpty => _segments.Count == 0;

    public static bool TryParse(string? template, out SkinBindingTemplate result, out string error)
    {
        result = new SkinBindingTemplate();
        error = string.Empty;

        var text = template ?? string.Empty;
        var index = 0;
        while (index < text.Length)
        {
            var open = text.IndexOf("{{", index, StringComparison.Ordinal);
            if (open < 0)
            {
                result._segments.Add(new Segment(text[index..], null));
                break;
            }

            if (open > index) result._segments.Add(new Segment(text[index..open], null));

            var close = text.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0)
            {
                error = "数据绑定缺少右花括号 “}}”。";
                return false;
            }

            var expressionText = text[(open + 2)..close];
            if (!SkinScriptEngine.TryParseExpression(expressionText, out var expression, out var expressionError))
            {
                error = "数据绑定表达式无效：" + expressionError;
                return false;
            }

            result._segments.Add(new Segment(string.Empty, expression));
            index = close + 2;
        }

        return result._segments.Count > 0;
    }

    /// <summary>渲染文本；表达式返回 null 时输出空串。</summary>
    public string Render(ISkinScriptHost? host)
    {
        var builder = new StringBuilder();
        foreach (var segment in _segments)
        {
            if (segment.Expression == null) builder.Append(segment.Text);
            else builder.Append(segment.Expression.Evaluate(host, false).AsText());
        }
        return builder.ToString();
    }

    /// <summary>只有一个表达式且没有静态文本时返回该表达式，供非文本属性（如 visible）使用。</summary>
    public SkinScriptExpression? SingleExpression
        => _segments.Count == 1 && _segments[0].Expression != null ? _segments[0].Expression : null;

    public bool IsStatic => _segments.All(segment => segment.Expression == null);

    public string StaticText => string.Concat(_segments
        .Where(segment => segment.Expression == null)
        .Select(segment => segment.Text));
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Harbor.Abstractions.Dsl;

namespace Harbor.Tui.CellForge.CodeGen;

/// <summary>
///     Turns the layout nodes of a parsed <see cref="SpecArena"/> into C# that
///     constructs the corresponding runtime nodes (issue #2518, from the #1195
///     recon). Row and Column become a <c>FlexSplitNode</c> on the
///     horizontal/vertical axis; Overlay becomes a <c>ZStackNode</c>. Widget
///     nodes are leaves in this slice: their real builders belong to a
///     separate step, so here they become content-free measured leaves.
/// </summary>
/// <remarks>
///     The emitter only concatenates strings and binds neither the CellForge
///     runtime nor Roslyn symbols. It is appended to the phase-2 partial class
///     produced by <see cref="BuildRootEmitter"/>, so no extra source is
///     added to the compilation. Runtime types are named by fully qualified
///     <c>global::</c> names and no reflection, <c>dynamic</c> or emit is used,
///     which keeps the output AOT-safe. Names and literals come from already
///     classified <see cref="SpecValue"/> tokens; numeric values are reparsed
///     with the invariant culture before they are written back.
/// </remarks>
internal static class SpecLayoutEmitter
{
    private const string Render = "global::Harbor.Tui.CellForge.Rendering.";
    private const string Indent = "        ";

    /// <summary>
    ///     Lines of the <c>BuildLayout</c> method, already indented for the
    ///     phase-2 partial class. The first root is returned; a file with
    ///     several roots still constructs each one, keeping emission
    ///     deterministic.
    /// </summary>
    internal static IReadOnlyList<string> EmitMethod(SpecArena arena)
    {
        var body = new List<string>();
        var next = 0;
        string? first = null;
        foreach (var root in arena.Roots)
        {
            var variable = EmitNode(root, body, ref next);
            first ??= variable;
        }

        var method = new List<string>
        {
            "    /// <summary>Builds the layout node tree for the markup file.</summary>",
            "    internal static " + Render + "INode? BuildLayout()",
            "    {",
        };
        method.AddRange(body);
        method.Add(Indent + "return " + (first ?? "null") + ";");
        method.Add("    }");
        method.Add(string.Empty);
        return method;
    }

    private static string EmitNode(SpecNode node, List<string> body, ref int next)
    {
        var variable = "n" + next++;
        var horizontal = node.Kind == SpecKind.Row;
        var vertical = node.Kind == SpecKind.Column;
        var overlay = node.Kind == SpecKind.Overlay;
        var isLayout = horizontal || vertical || overlay;

        if (horizontal || vertical)
        {
            var gap = FindInt(node, "gap", 0);
            var justify = FindJustify(node);
            var line = new StringBuilder(Indent).Append("var ").Append(variable).Append(" = new ")
                .Append(Render).Append("FlexSplitNode(")
                .Append(Render).Append("SplitDir.").Append(horizontal ? "Horizontal" : "Vertical")
                .Append(", ").Append(gap.ToString(CultureInfo.InvariantCulture));
            if (justify is not null)
                line.Append(", ").Append(Render).Append("FlexJustify.").Append(justify);
            line.Append(");");
            body.Add(line.ToString());
        }
        else if (overlay)
        {
            body.Add(Indent + "var " + variable + " = new " + Render + "ZStackNode();");
        }
        else
        {
            body.Add(Indent + "var " + variable + " = new " + Render + "MeasuredLeaf(static _ => default);");
        }

        if (isLayout)
        {
            foreach (var child in node.Children)
            {
                var childVariable = EmitNode(child, body, ref next);
                var add = new StringBuilder(Indent).Append(variable).Append(".Add(").Append(childVariable);
                if (!overlay)
                    add.Append(", ").Append(FindTrack(child));
                add.Append(");");
                body.Add(add.ToString());
            }
        }

        return variable;
    }

    private static string FindTrack(SpecNode node)
    {
        foreach (var op in node.Operations)
        {
            if (!op.Name.Equals("track", StringComparison.OrdinalIgnoreCase) || op.Arguments.Count == 0)
                continue;

            var kind = op.Arguments[0].Text.ToLowerInvariant();
            var argument = op.Arguments.Count > 1 ? op.Arguments[1].Text : null;
            return kind switch
            {
                "fixed" => Render + "FlexTrack.Fixed(" + AsInt(argument, 0) + ")",
                "percent" => Render + "FlexTrack.Percent(" + AsFloat(argument, "0f") + ")",
                "fill" => Render + "FlexTrack.Fill(" + AsFloat(argument, "1f") + ")",
                "min" => Render + "FlexTrack.Min(" + AsInt(argument, 0) + ")",
                "max" => Render + "FlexTrack.Max(" + AsInt(argument, 0) + ")",
                _ => Render + "FlexTrack.Fill()"
            };
        }

        return Render + "FlexTrack.Fill()";
    }

    private static int FindInt(SpecNode node, string name, int fallback)
    {
        foreach (var op in node.Operations)
        {
            if (op.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                && op.Arguments.Count > 0
                && int.TryParse(op.Arguments[0].Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                return value;
        }

        return fallback;
    }

    private static string? FindJustify(SpecNode node)
    {
        foreach (var op in node.Operations)
        {
            if (!op.Name.Equals("justify", StringComparison.OrdinalIgnoreCase) || op.Arguments.Count == 0)
                continue;

            var name = op.Arguments[0].Text.Replace("_", string.Empty, StringComparison.Ordinal)
                .Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
            return name switch
            {
                "start" => "Start",
                "center" => "Center",
                "end" => "End",
                "spacebetween" => "SpaceBetween",
                "spacearound" => "SpaceAround",
                "spaceevenly" => "SpaceEvenly",
                "legacy" => "Legacy",
                _ => null
            };
        }

        return null;
    }

    private static string AsInt(string? text, int fallback) =>
        text is not null && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value.ToString(CultureInfo.InvariantCulture)
            : fallback.ToString(CultureInfo.InvariantCulture);

    private static string AsFloat(string? text, string fallback) =>
        text is not null && float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
            ? text + "f"
            : fallback;
}

using System.Text;

namespace SmartFileOrganizer.Infrastructure.Content;

/// <summary>
/// The plain text of an RTF document (https://github.com/crack666/smart_file_organizer/issues/109): control words are
/// dropped, <c>\par</c> and <c>\line</c> become line breaks, <c>\'hh</c> is read as Windows-1252 and <c>\uN</c> as
/// Unicode; groups that hold no text (font and colour tables, style sheet, document info, pictures, objects, any
/// <c>\*</c> destination) are skipped whole. No layout, tables or images.
/// </summary>
public static class RtfText
{
    private static readonly HashSet<string> SkippedDestinations =
    [
        "fonttbl", "colortbl", "stylesheet", "info", "pict", "object", "themedata", "colorschememapping", "latentstyles",
        "datastore", "xmlnstbl", "listtable", "listoverridetable", "rsidtbl", "generator", "filetbl", "revtbl", "header",
        "footer", "headerl", "headerr", "headerf", "footerl", "footerr", "footerf", "fldinst", "pgdsctbl", "mmathPr",
        "blipuid", "shpinst", "shppict", "nonshppict"
    ];

    public static string Extract(string rtf, int maxChars)
    {
        var sb = new StringBuilder();
        // Per group: whether it is skipped, and how many characters follow a \uN as its fallback.
        var stack = new Stack<(bool Skip, int Uc)>();
        var skip = false;
        var uc = 1;
        var pendingFallback = 0;
        var i = 0;
        while (i < rtf.Length && sb.Length < maxChars)
        {
            var c = rtf[i];
            switch (c)
            {
                case '{':
                    stack.Push((skip, uc));
                    i++;
                    break;
                case '}':
                    if (stack.Count > 0) (skip, uc) = stack.Pop();
                    pendingFallback = 0;
                    i++;
                    break;
                case '\\':
                    i = ControlWord(rtf, i + 1, sb, ref skip, ref uc, ref pendingFallback, stack.Count);
                    break;
                case '\r' or '\n':
                    i++;
                    break;
                default:
                    if (pendingFallback > 0) pendingFallback--;
                    else if (!skip) sb.Append(c);
                    i++;
                    break;
            }
        }
        var text = sb.ToString();
        return text.Length > maxChars ? text[..maxChars] : text;
    }

    private static int ControlWord(string rtf, int i, StringBuilder sb, ref bool skip, ref int uc, ref int pendingFallback, int depth)
    {
        if (i >= rtf.Length) return i;
        var c = rtf[i];

        // Control symbols.
        if (c is '\\' or '{' or '}')
        {
            Emit(c, sb, skip, ref pendingFallback);
            return i + 1;
        }
        if (c == '*')
        {
            skip = true;
            return i + 1;
        }
        if (c == '\'')
        {
            if (i + 2 < rtf.Length && byte.TryParse(rtf.AsSpan(i + 1, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
            {
                Emit(LegacyWordReader.Cp1252(b), sb, skip, ref pendingFallback);
                return i + 3;
            }
            return i + 1;
        }
        if (c == '~') { Emit(' ', sb, skip, ref pendingFallback); return i + 1; }
        if (c is '-' or '_') { if (c == '_') Emit('-', sb, skip, ref pendingFallback); return i + 1; }
        if (c is '\r' or '\n') { if (!skip) sb.Append('\n'); return i + 1; }
        if (!char.IsAsciiLetter(c)) return i + 1;

        var start = i;
        while (i < rtf.Length && char.IsAsciiLetter(rtf[i])) i++;
        var word = rtf[start..i];
        var numStart = i;
        if (i < rtf.Length && rtf[i] == '-') i++;
        while (i < rtf.Length && char.IsAsciiDigit(rtf[i])) i++;
        int? param = i > numStart && int.TryParse(rtf.AsSpan(numStart, i - numStart), out var p) ? p : null;
        if (i < rtf.Length && rtf[i] == ' ') i++; // the delimiting space belongs to the control word

        if (SkippedDestinations.Contains(word) && depth > 0)
        {
            skip = true;
            return i;
        }
        switch (word)
        {
            case "par" or "line" or "sect" or "page" or "row":
                if (!skip) sb.Append('\n');
                break;
            case "tab" or "cell":
                if (!skip) sb.Append('\t');
                break;
            case "uc":
                uc = param ?? 1;
                break;
            case "u" when param is { } code:
                Emit((char)(code < 0 ? code + 65536 : code), sb, skip, ref pendingFallback);
                pendingFallback = uc;
                break;
            case "emdash": Emit('—', sb, skip, ref pendingFallback); break;
            case "endash": Emit('–', sb, skip, ref pendingFallback); break;
            case "bullet": Emit('•', sb, skip, ref pendingFallback); break;
            case "lquote": Emit('‘', sb, skip, ref pendingFallback); break;
            case "rquote": Emit('’', sb, skip, ref pendingFallback); break;
            case "ldblquote": Emit('“', sb, skip, ref pendingFallback); break;
            case "rdblquote": Emit('”', sb, skip, ref pendingFallback); break;
        }
        return i;
    }

    private static void Emit(char c, StringBuilder sb, bool skip, ref int pendingFallback)
    {
        if (pendingFallback > 0) { pendingFallback--; return; }
        if (!skip) sb.Append(c);
    }
}

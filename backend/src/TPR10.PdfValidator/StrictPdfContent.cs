using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Graphics;
using UglyToad.PdfPig.Logging;
using UglyToad.PdfPig.Tokenization.Scanner;
using UglyToad.PdfPig.Tokens;

namespace TPR10.PdfValidator;

// PdfPig can silently drop unknown operators, including the first operation. Preflight each
// content stream before GetPage. Inline images are outside this deliberately narrow subset.
internal static class StrictPdfContent
{
    public static void Validate(Memory<byte> bytes)
    {
        using var input = new MemoryInputBytes(bytes);
        var scanner = new CoreTokenScanner(input, false, new StackDepthGuard(64), useLenientParsing: false);
        var operands = new List<IToken>(); var tokens = 0;
        while (scanner.MoveNext())
        {
            if (++tokens > 100000) throw new InvalidDataException("Content token limit.");
            var token = scanner.CurrentToken;
            if (token is CommentToken) continue;
            if (token is OperatorToken operation)
            {
                if (operation.Data is "BI" or "ID" or "EI"
                    || ReflectionGraphicsStateOperationFactory.Instance.Create(operation, operands) is null)
                    throw new InvalidDataException("Unsupported content operation.");
                operands.Clear();
            }
            else
            {
                if (token is InlineImageDataToken || operands.Count >= 10000) throw new InvalidDataException("Unsupported content operands.");
                operands.Add(token);
            }
        }
        if (operands.Count != 0) throw new InvalidDataException("Unconsumed content operands.");
    }
}

// Diagnostics are verdict inputs, never emitted with document data. Fail closed even when
// PdfPig attempts recovery rather than throwing; this policy also covers nested page parsing.
internal sealed class RejectingPdfLog : ILog
{
    public bool Rejected { get; private set; }
    public void Debug(string message) { }
    public void Debug(string message, Exception ex) => Rejected = true;
    public void Warn(string message) => Rejected = true;
    public void Error(string message) => Rejected = true;
    public void Error(string message, Exception ex) => Rejected = true;
}

using System.Security.Cryptography;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Iced.Intel;

namespace GameValueEditor.Modules.Deskrawl;

// Full analyzed method, not a prologue/AOB. Keep constants, field displacements,
// operand widths and control-flow destinations. Only module relocations are erased.
internal static class DeskrawlSemanticCode
{
    internal static string Fingerprint(byte[] code, ulong address, int instructionCount, ulong moduleBase, ulong moduleEnd)
        => Hash(Canonical(code, address, instructionCount, moduleBase, moduleEnd));

    internal static string Hash(string canonical) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

    internal static string Canonical(byte[] code, ulong address, int instructionCount, ulong moduleBase, ulong moduleEnd)
    {
        if (instructionCount is < 1 or > 4096) throw new InvalidDataException("语义指令数量无效。");
        var decoder = Iced.Intel.Decoder.Create(64, new ByteArrayCodeReader(code));
        decoder.IP = address;
        var formatter = new IntelFormatter();
        var instructions = new List<Instruction>();
        for (var index = 0; index < instructionCount; index++)
        {
            var instruction = decoder.Decode();
            if (instruction.IsInvalid) throw new InvalidDataException("语义指令解析失败。");
            instructions.Add(instruction);
        }
        var end = decoder.IP;
        var result = new List<string>();
        foreach (var instruction in instructions)
        {
            var output = new StringOutput();
            formatter.Format(instruction, output);
            // Capture only CLR primitives. A closure containing Iced.Instruction
            // forces dependency resolution during GetTypes, before module init.
            var hasRelocation = instruction.IsIPRelativeMemoryOperand || instruction.Op0Kind == OpKind.NearBranch64;
            var isBranch = instruction.Op0Kind == OpKind.NearBranch64 && instruction.FlowControl != FlowControl.Call;
            var line = Regex.Replace(output.ToString(), @"(?<![A-Za-z0-9_])[0-9A-Fa-f]{8,}h\b", match =>
            {
                if (!hasRelocation ||
                    !ulong.TryParse(match.Value.AsSpan(0, match.Length - 1), System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out var pointer) ||
                    pointer < moduleBase || pointer >= moduleEnd) return match.Value;
                // Local branches retain exact destinations relative to this method.
                if (isBranch && pointer >= address && pointer < end)
                    return "REL:" + unchecked((long)(pointer - address));
                return "MODULE";
            });
            result.Add(line);
        }
        return string.Join('\n', result);
    }

    internal static T Unique<T>(IEnumerable<T> matches, string role)
    {
        var items = matches.Take(2).ToArray();
        return items.Length == 1 ? items[0] : throw new InvalidOperationException($"{role} 的完整语义候选缺失或不唯一；未执行修改。");
    }

}

using System.Reflection;
using System.Runtime.InteropServices;
using Harbor.Tui.ConsoleEx.Input;
using TUnit.Core;

namespace Harbor.Tui.ConsoleEx.Tests;

/// <summary>
///     CE-5 Зона 3 — стражи регрессий termios-слоя ConsoleEx.
///     Ровно класс бага, который породил спринт: CE-4 упал в живом ghostty с
///     AccessViolationException, потому что приватный struct Termios был 49
///     байт вместо 60 (kernel пишет c_ispeed/c_ospeed) — tcgetattr затирал
///     стек. Ни один из 366 тестов это не ловил, потому что TestBackend не
///     вызывает настоящий termios. Эти тесты — структурный заслон: раскладка
///     struct'а фиксируется против kernel-канона (asm-generic/termbits.h),
///     graceful-путь при stdin-не-TTY фиксируется как типизированное
///     InvalidOperationException, а не AV.
/// </summary>
/// <remarks>
///     Reflection на приватный nested-struct разрешён: это тестовый код, в
///     прод-бинарник не попадает (AOT-ограничения действуют только на src/).
/// </remarks>
public sealed class TermiosLayoutGuards
{
    [DllImport("libc", SetLastError = true)]
    private static extern int isatty(int fd);

    /// <summary>
    ///     Раскладка приватного struct Termios должна точно совпадать с
    ///     kernel-каноном Linux (asm-generic/termbits.h, NCCS=32):
    ///     4×tcflag_t (uint) + cc_t c_line + cc_t c_cc[32] + speed_t
    ///     c_ispeed/c_ospeed = 60 байт. Смещения: iflag@0, oflag@4, cflag@8,
    ///     lflag@12, line@16, cc@17..48, затем выравнивание speed_t на 4 байта
    ///     → ispeed@52, ospeed@56, итого 60. Kernel копирует в переданный
    ///     указатель ровно sizeof(struct termios)=60 байт — любой недостающий
    ///     хвост = запись мимо struct'а = стек-коррупция (CE-4).
    /// </summary>
    [Test]
    public async Task TermiosStruct_MatchesKernelLayout()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip.Test("This guard asserts asm-generic (Linux) struct layouts — other OSes differ.");
            return;
        }

        var termiosType = typeof(UnixTermiosModeController).GetNestedType(
            "Termios", BindingFlags.NonPublic | BindingFlags.Instance);
        await Assert.That(termiosType).IsNotNull();

        // Total size — the CE-4 invariant: kernel writes 60 bytes, the struct
        // must be able to absorb all of them. 49 bytes = 11 bytes of stack smash.
        int size = Marshal.SizeOf(termiosType!);
        await Assert.That(size).IsEqualTo(60)
            .Because($"sizeof(Termios) must be 60 (kernel canon), was {size} — tcgetattr would write past the struct.");

        // Per-field offsets against the kernel layout.
        await Assert.That(OffsetOf(termiosType!, "CIflag")).IsEqualTo(0);
        await Assert.That(OffsetOf(termiosType!, "COflag")).IsEqualTo(4);
        await Assert.That(OffsetOf(termiosType!, "CCflag")).IsEqualTo(8);
        await Assert.That(OffsetOf(termiosType!, "CLflag")).IsEqualTo(12);
        await Assert.That(OffsetOf(termiosType!, "CLine")).IsEqualTo(16);
        await Assert.That(OffsetOf(termiosType!, "ControlCharacters")).IsEqualTo(17);
        await Assert.That(OffsetOf(termiosType!, "CIspeed")).IsEqualTo(52);
        await Assert.That(OffsetOf(termiosType!, "COspeed")).IsEqualTo(56);

        // c_cc[NCCS=32] — the control-character block must be exactly 32 bytes.
        var ccField = termiosType!.GetField("ControlCharacters");
        await Assert.That(ccField).IsNotNull();
        int ccSize = Marshal.SizeOf(ccField!.FieldType);
        await Assert.That(ccSize).IsEqualTo(32);
    }

    /// <summary>
    ///     stdin-не-TTY → Enter() обязан кидать типизированное
    ///     InvalidOperationException (graceful-путь), а не падать с
    ///     AccessViolationException. Защищает от регрессии в обратную сторону:
    ///     даже идеальная раскладка не должна превращать отсутствующий
    ///     терминал в AV — tcgetattr на не-tty fd возвращает -1/ENOTTY, и
    ///     контроллер обязан это обрабатывать.
    /// </summary>
    [Test]
    public async Task Enter_WithNonTtyStdin_ThrowsInvalidOperationException_NotAccessViolation()
    {
        if (isatty(0) != 0)
        {
            Skip.Test("stdin is a real terminal in this run — the non-TTY graceful path is unreachable here.");
            return;
        }

        var controller = new UnixTermiosModeController();

        await Assert.That(() => controller.Enter())
            .Throws<InvalidOperationException>()
            .Because("tcgetattr(stdin) fails on a non-terminal fd — must surface as typed InvalidOperationException, not AV.");

        // Failed Enter must not leave the controller in raw mode.
        await Assert.That(controller.IsRaw).IsFalse();
    }

    private static long OffsetOf(Type type, string fieldName) =>
        Marshal.OffsetOf(type, fieldName).ToInt64();
}

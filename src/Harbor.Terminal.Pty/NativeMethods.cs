using System.Runtime.InteropServices;
using System.Text;

namespace Harbor.Terminal.Pty;

/// <summary>
///     Raw libc surface for PTY hosting (posix_openpt → posix_spawnp with
///     SETSID + slave-open file actions — the child acquires the controlling
///     tty exactly like a login shell). Flag/request constants resolve per-OS
///     at first touch: asm-generic (Linux) vs Darwin values.
/// </summary>
internal static class NativeMethods
{
    private static readonly bool IsDarwin = OperatingSystem.IsMacOS();

    // open(2) flags — O_NOCTTY/O_CLOEXEC differ between asm-generic and Darwin.
    internal const int O_RDWR = 0x2;
    internal static readonly int O_NOCTTY = IsDarwin ? 0x20000 : 0x400;
    internal static readonly int O_CLOEXEC = IsDarwin ? 0x1000000 : 0x80000;

    // ioctl request for TIOCSWINSZ: Linux x86_64/arm64 is 0x5414, while Darwin
    // encodes it through the IOC machinery (0x80087414).
    internal static readonly uint TIOCSWINSZ = IsDarwin ? 0x80087414u : 0x5414u;

    // posix_spawn attribute flag: setsid(2) in the spawned child.
    // Darwin pins POSIX_SPAWN_SETSID to 0x400; glibc uses 0x80.
    internal static readonly ushort POSIX_SPAWN_SETSID = IsDarwin ? (ushort)0x400 : (ushort)0x80;

    internal const int SIGKILL = 9;
    internal const int EINTR = 4;

    /// <summary>struct winsize — kernel layout, 8 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct WinSize
    {
        public ushort Rows;
        public ushort Cols;
        public ushort XPixel;
        public ushort YPixel;
    }

    [DllImport("libc", SetLastError = true)]
    internal static extern int posix_openpt(int flags);

    [DllImport("libc", SetLastError = true)]
    internal static extern int grantpt(int fd);

    [DllImport("libc", SetLastError = true)]
    internal static extern int unlockpt(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int ptsname_r(int fd, [Out] byte[] buf, nuint buflen);

    [DllImport("libc", SetLastError = true)]
    internal static extern int open(string path, int flags);

    [DllImport("libc", SetLastError = true)]
    internal static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    internal static extern int ioctl(int fd, uint request, ref WinSize winsize);

    [DllImport("libc", SetLastError = true)]
    internal static extern int read(int fd, [Out] byte[] buffer, int count);

    [DllImport("libc", SetLastError = true)]
    internal static extern int write(int fd, ref byte buffer, int count);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawn_file_actions_init(IntPtr fileActions);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawn_file_actions_addopen(
        IntPtr fileActions, int fileDescriptor, string path, int flags, int mode);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawn_file_actions_adddup2(
        IntPtr fileActions, int fromFileDescriptor, int toFileDescriptor);

    // chdir(2) inside the spawned child — glibc: *_np (≥ 2.29), Darwin: public symbol.
    [DllImport("libc", SetLastError = true, EntryPoint = "posix_spawn_file_actions_addchdir_np")]
    private static extern int posix_spawn_file_actions_addchdir_linux(IntPtr fileActions, string path);

    [DllImport("libc", SetLastError = true, EntryPoint = "posix_spawn_file_actions_addchdir")]
    private static extern int posix_spawn_file_actions_addchdir_darwin(IntPtr fileActions, string path);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawn_file_actions_destroy(IntPtr fileActions);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawnattr_init(IntPtr attr);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawnattr_setflags(IntPtr attr, ushort flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawnattr_destroy(IntPtr attr);

    /// <summary>Exact-path spawn (no PATH search). argv/envp are native char*[] blocks.</summary>
    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawn(
        out int pid, string path, IntPtr fileActions, IntPtr attrp, IntPtr argv, IntPtr envp);

    /// <summary>PATH-searching spawn variant (<c>posix_spawnp</c>).</summary>
    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawnp(
        out int pid, string path, IntPtr fileActions, IntPtr attrp, IntPtr argv, IntPtr envp);

    [DllImport("libc", SetLastError = true)]
    internal static extern int waitpid(int pid, out int status, int options);

    [DllImport("libc", SetLastError = true)]
    internal static extern int kill(int pid, int sig);

    /// <summary>Resolves the slave-side device path of a fresh PTY master (<c>ptsname_r(3)</c>).</summary>
    internal static string GetSlaveName(int masterFd)
    {
        var buf = new byte[256];
        int rc = ptsname_r(masterFd, buf, (nuint)buf.Length);
        return rc != 0
            ? throw new IOException($"ptsname_r({masterFd}) failed with errno {rc}.")
            : Encoding.UTF8.GetString(buf, 0, buf.IndexOf((byte)0)).TrimEnd('\0');
    }

    /// <summary>chdir(2) file action for the spawned child (platform symbol dispatch).</summary>
    internal static int SpawnFileActionsAddChdir(IntPtr fileActions, string path)
    {
        return OperatingSystem.IsMacOS()
            ? posix_spawn_file_actions_addchdir_darwin(fileActions, path)
            : posix_spawn_file_actions_addchdir_linux(fileActions, path);
    }

    /// <summary>
    ///     Spawn <paramref name="fileName" /> in a fresh session with stdio
    ///     wired to the PTY slave named <paramref name="slavePath" />, with an
    ///     optional working-directory change applied as a spawn file action.
    /// </summary>
    internal static int SpawnInPty(
        string fileName,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string> environment,
        string slavePath,
        string? workingDirectory,
        bool searchPath)
    {
        // glibc: posix_spawnattr_t = 336 B, posix_spawn_file_actions_t = 80 B.
        // 512-byte buffers give generous headroom across glibc versions.
        IntPtr attr = Marshal.AllocHGlobal(512);
        IntPtr actions = Marshal.AllocHGlobal(512);
        try
        {
            Zero(attr);
            Zero(actions);
            Check("posix_spawnattr_init", posix_spawnattr_init(attr));
            Check("posix_spawn_file_actions_init", posix_spawn_file_actions_init(actions));
            Check("addopen(stdin)", posix_spawn_file_actions_addopen(
                actions, 0, slavePath, O_RDWR | O_NOCTTY, 0));
            Check("adddup2(stdout)", posix_spawn_file_actions_adddup2(actions, 0, 1));
            Check("adddup2(stderr)", posix_spawn_file_actions_adddup2(actions, 0, 2));
            if (workingDirectory is not null)
            {
                Check("addchdir", SpawnFileActionsAddChdir(actions, workingDirectory));
            }

            Check("posix_spawnattr_setflags", posix_spawnattr_setflags(attr, POSIX_SPAWN_SETSID));

            // argv/envp are built as native NULL-terminated char*[] blocks —
            // the default string[] marshalling produced EFAULT on this host.
            string[] argv = [fileName, .. args];
            string[] envp = [.. environment.Select(kv => kv.Key + "=" + kv.Value)];
            NativeArgv argvBlock = NativeArgv.Alloc(argv);
            NativeArgv envpBlock = NativeArgv.Alloc(envp);
            try
            {
                int pid;
                if (searchPath)
                {
                    Check("posix_spawnp", posix_spawnp(out pid, fileName, actions, attr, argvBlock.Root, envpBlock.Root));
                }
                else
                {
                    Check("posix_spawn", posix_spawn(out pid, fileName, actions, attr, argvBlock.Root, envpBlock.Root));
                }

                return pid;
            }
            finally
            {
                argvBlock.Free();
                envpBlock.Free();
            }
        }
        finally
        {
            _ = posix_spawn_file_actions_destroy(actions);
            _ = posix_spawnattr_destroy(attr);
            Marshal.FreeHGlobal(actions);
            Marshal.FreeHGlobal(attr);
        }

        static void Zero(IntPtr p) => Marshal.Copy(new byte[512], 0, p, 512);

        static void Check(string what, int rc)
        {
            if (rc != 0)
            {
                throw new IOException($"{what} failed: rc={rc} errno={Marshal.GetLastWin32Error()}.");
            }
        }
    }

    /// <summary>A NULL-terminated native <c>char*[]</c> block with owned string storage.</summary>
    private readonly struct NativeArgv(nint root, nint[] strings) : IDisposable
    {
        public IntPtr Root => root;

        public static NativeArgv Alloc(IReadOnlyList<string> items)
        {
            var strings = new nint[items.Count + 1];
            for (int i = 0; i < items.Count; i++)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(items[i]);
                nint p = Marshal.AllocHGlobal(bytes.Length + 1);
                Marshal.Copy(bytes, 0, p, bytes.Length);
                Marshal.WriteByte(p, bytes.Length, 0);
                strings[i] = p;
            }

            strings[^1] = 0; // NULL terminator required by execve(2)

            nint blockRoot = Marshal.AllocHGlobal(nint.Size * strings.Length);
            for (int i = 0; i < strings.Length; i++)
            {
                Marshal.WriteIntPtr(blockRoot, i * nint.Size, strings[i]);
            }

            return new NativeArgv(blockRoot, strings);
        }

        public void Free()
        {
            foreach (nint p in strings)
            {
                if (p != 0)
                {
                    Marshal.FreeHGlobal(p);
                }
            }

            Marshal.FreeHGlobal(root);
        }

        void IDisposable.Dispose() => Free();
    }
}

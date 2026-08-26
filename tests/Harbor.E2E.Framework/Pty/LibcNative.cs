using System.Runtime.InteropServices;

namespace Harbor.E2E.Framework.Pty;

/// <summary>
///     Raw libc P/Invoke surface for the CE-5 PTY harness. Test-only code —
///     never referenced from prod binaries (BCL-only/AOT-safe rule applies to
///     src/, P/Invoke libc is sanctioned inside tests/ per the sprint charter).
///     All constants are Linux (asm-generic) values; the harness refuses to
///     start off-Linux.
/// </summary>
internal static class LibcNative
{
    // open(2) flags (Linux)
    internal const int O_RDWR = 0x2;
    internal const int O_NOCTTY = 0x400;
    internal const int O_CLOEXEC = 0x80000;

    // termios optional actions
    internal const int TCSANOW = 0;

    // ioctl request for TIOCSWINSZ (set window size), Linux x86_64/arm64.
    internal const uint TIOCSWINSZ = 0x5414;

    // posix_spawn attribute flag: setsid(2) in the spawned child.
    // Value per this host's /usr/include/spawn.h (glibc ≥ 2.44 moved SETSID
    // to 0x80; older headers had 0x400 — probed empirically before pinning).
    internal const ushort POSIX_SPAWN_SETSID = 0x80;

    internal const int SIGKILL = 9;
    internal const int WNOHANG = 1;

    /// <summary>struct winsize — kernel layout, 8 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct WinSize
    {
        public ushort Rows;
        public ushort Cols;
        public ushort XPixel;
        public ushort YPixel;
    }

    /// <summary>
    ///     Kernel struct termios (asm-generic/termbits.h), 60 bytes on
    ///     Linux x64/arm64: 4×tcflag_t + cc_t c_line + cc_t c_cc[32] +
    ///     speed_t c_ispeed/c_ospeed. Layout verified empirically against
    ///     ctypes on this host: offsets iflag=0 oflag=4 cflag=8 lflag=12
    ///     line=16 cc=17 ispeed=52 ospeed=56, sizeof=60.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct TermiosKernel
    {
        public uint IFlag;
        public uint OFlag;
        public uint CFlag;
        public uint LFlag;
        public byte Line;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] Cc;

        public uint ISpeed;
        public uint OSpeed;
    }

    [DllImport("libc", SetLastError = true)]
    internal static extern int posix_openpt(int flags);

    [DllImport("libc", SetLastError = true)]
    internal static extern int grantpt(int fd);

    [DllImport("libc", SetLastError = true)]
    internal static extern int unlockpt(int fd);

    /// <returns>0 on success; slave name written into <paramref name="buf" />.</returns>
    [DllImport("libc", SetLastError = true)]
    private static extern int ptsname_r(int fd, [Out] byte[] buf, nuint buflen);

    [DllImport("libc", SetLastError = true)]
    internal static extern int open(string path, int flags);

    [DllImport("libc", SetLastError = true)]
    internal static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    internal static extern int ioctl(int fd, uint request, ref WinSize winsize);

    [DllImport("libc", SetLastError = true)]
    internal static extern int tcgetattr(int fd, ref TermiosKernel termios);

    [DllImport("libc", SetLastError = true)]
    internal static extern int tcsetattr(int fd, int optionalActions, ref TermiosKernel termios);

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

    /// <summary>
    ///     Resolves the slave-side device path of a freshly allocated PTY
    ///     master (<c>ptsname_r(3)</c>, thread-safe form).
    /// </summary>
    internal static string GetSlaveName(int masterFd)
    {
        var buf = new byte[256];
        int rc = ptsname_r(masterFd, buf, (nuint)buf.Length);
        return rc != 0
            ? throw new IOException($"ptsname_r({masterFd}) failed with errno {rc}.")
            : Encoding.UTF8.GetString(buf, 0, buf.IndexOf((byte)0)).TrimEnd('\0');
    }

    /// <summary>
    ///     Spawn <paramref name="fileName" /> in a fresh session with stdio
    ///     wired to the PTY slave named <paramref name="slavePath" /> (which
    ///     becomes the controlling terminal). Uses <c>posix_spawnp(3)</c> with
    ///     <c>POSIX_SPAWN_SETSID</c>: no fork-in-managed-code hazards, and the
    ///     file-actions <b>open</b> of the slave path after setsid acquires
    ///     the controlling tty exactly like a login shell would.
    /// </summary>
    /// <param name="searchPath">
    ///     true → resolve <paramref name="fileName" /> via PATH (posix_spawnp);
    ///     false → treat it as an exact path.
    /// </param>
    internal static int SpawnInPty(
        string fileName,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string> environment,
        string slavePath,
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
            Check("posix_spawnattr_setflags", posix_spawnattr_setflags(attr, POSIX_SPAWN_SETSID));

            // argv/envp are built as native NULL-terminated char*[] blocks —
            // no reliance on the interop marshaller's array handling (the
            // default string[] marshalling produced EFAULT on this host).
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

namespace Harbor.App.Cli.Commands;

/// <summary>
///     #2669: shell completion script generation. Each script completes the
///     top-level verbs from <see cref="CliVerbCatalog.CompletionWordList"/>,
///     the same catalog <c>harbor help</c> is built from, so the scripts and
///     the help text cannot disagree.
/// </summary>
/// <remarks>
///     The scripts are static templates. There is no reflection, no runtime
///     code generation and no new dependency — only BCL string operations —
///     so the generator is NativeAOT-safe and the output is deterministic.
/// </remarks>
internal static class CompletionVerbs
{
    /// <summary>Generate and print a completion script; return the CLI exit code.</summary>
    internal static int Run(string? shell)
    {
        if (shell is null)
        {
            Console.Error.WriteLine("harbor completions: expected a shell (bash, zsh, fish or powershell).");
            return 2;
        }

        string script = Generate(shell);
        if (script.Length == 0)
        {
            Console.Error.WriteLine(
                $"harbor completions: unsupported shell '{shell}' (expected bash, zsh, fish or powershell).");
            return 2;
        }

        Console.Out.Write(script);
        return 0;
    }

    /// <summary>
    ///     Return the script for <paramref name="shell"/>, or an empty string
    ///     for an unsupported shell. Kept separate from <see cref="Run"/> so
    ///     the output can be asserted without swapping the process console.
    /// </summary>
    internal static string Generate(string shell) => shell.ToLowerInvariant() switch
    {
        "bash" => Bash(),
        "zsh" => Zsh(),
        "fish" => Fish(),
        "powershell" or "pwsh" => PowerShell(),
        _ => string.Empty,
    };

    private static string Bash()
    {
        string verbs = CliVerbCatalog.CompletionWordList();
        return $$"""
            _harbor_completions() {
                local cur="${COMP_WORDS[COMP_CWORD]}"
                COMPREPLY=( $(compgen -W "{{verbs}}" -- "$cur") )
            }
            complete -F _harbor_completions harbor
            """ + "\n";
    }

    private static string Zsh()
    {
        string verbs = CliVerbCatalog.CompletionWordList();
        return $$"""
            #compdef harbor
            _harbor_completions() {
              local -a commands
              commands=({{verbs}})
              _describe 'harbor command' commands
            }
            compdef _harbor_completions harbor
            """ + "\n";
    }

    private static string Fish()
    {
        string verbs = CliVerbCatalog.CompletionWordList();
        return $$"""
            complete -c harbor -f
            complete -c harbor -n '__fish_use_subcommand' -a '{{verbs}}'
            """ + "\n";
    }

    private static string PowerShell()
    {
        string verbs = CliVerbCatalog.CompletionWordList();
        return $$"""
            Register-ArgumentCompleter -Native -CommandName 'harbor' -ScriptBlock {
              param($wordToComplete, $commandAst, $cursorPosition)
              $verbs = '{{verbs}}'.Split(' ')
              $verbs | Where-Object { $_ -like "$wordToComplete*" } | ForEach-Object {
                [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_)
              }
            }
            """ + "\n";
    }
}

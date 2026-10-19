// #1614: the usings the moved files inherited from
// Harbor.Application/GlobalUsings.cs, narrowed to exactly what the onboarding
// trio resolves WITHOUT a file-local using:
//
//  - CSharpFunctionalExtensions: the Result railway the wizard's Bind chains
//    and the detector's existence-tolerant probes are built on. The CFE0001
//    ".Value without IsSuccess" analyzer gate rides Directory.Build.props
//    solution-wide and applies here too (the wizard carries the audited
//    pragma from the CfeValueBaselineTests baseline).
//  - Harbor.Abstractions.Models: ModelRef, which OnboardingPicks.
//    NormalizeModelId resolves without a file-local using.
//
// Everything else is covered elsewhere: System / IO / Threading /
// Collections.Generic come from the SDK's implicit usings; identifiers
// (ProviderId), providers (IProviderHealthCheck), agents (IAgentRegistry)
// and ILogger are declared file-locally by the moved files. Deliberately no
// ZLinq drop-in: the one LINQ call site (OnboardingWizard's live-model list
// Select+ToList) compiles against System.Linq from the implicit usings.
global using CSharpFunctionalExtensions;
global using Harbor.Abstractions.Models;

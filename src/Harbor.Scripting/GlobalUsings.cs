// Harbor.Scripting — global usings.
//
// All non-framework `using` directives live here, per project convention.
// File-scoped namespaces are used throughout. Implicit usings (SDK default)
// provide System / System.Collections.Generic / System.IO / System.Linq /
// System.Threading / System.Threading.Tasks / System.Text etc.
//
// Layer cross-references (see docs/SCRIPTING.md §Architecture):
//   Bridge      → Harbor.Abstractions only.
//   Engines     → Bridge + Harbor.Abstractions.
//   Storage     → Harbor.Abstractions only.
//   Compilation → Harbor.Abstractions only.
//   Hosting     → Engines + Storage + Compilation + Bridge + Harbor.Abstractions.
// Project namespaces are imported globally so call sites read clean.

global using System.Collections.Concurrent;
global using System.Diagnostics;
global using System.Security.Cryptography;
global using System.Text;
global using System.Text.Json;
global using CSharpFunctionalExtensions;
global using Harbor.Abstractions.Agents;
global using Harbor.Abstractions.Models;
global using Harbor.Abstractions.Models.Identifiers;
global using Harbor.Abstractions.Providers;
global using Harbor.Abstractions.Tools;
global using Harbor.Scripting.Bridge;
global using Harbor.Scripting.Compilation;
global using Harbor.Scripting.Engines;
global using Harbor.Scripting.Hosting;
global using Harbor.Scripting.Storage;
global using Jint;
global using Jint.Native;
global using Jint.Native.Function;
global using Jint.Runtime;
global using Microsoft.Extensions.Logging;

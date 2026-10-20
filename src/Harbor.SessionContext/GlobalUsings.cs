// #1624: global usings for the extracted session-context cohort, carried over
// verbatim from Harbor.Application/GlobalUsings.cs — the moved files relied on
// them there, so the mirror keeps the verbatim move compile-clean without
// touching a single moved file's using block.
global using System;
global using System.Collections.Generic;
global using System.IO;
// ZLinq replaces System.Linq throughout Harbor.SessionContext for zero-allocation LINQ.
// The SDK implicit "System.Linq" using is removed in Harbor.SessionContext.csproj.
global using ZLinq;
global using System.Text.Json;
global using System.Text.Json.Serialization;
global using System.Threading;
global using System.Threading.Tasks;
global using CSharpFunctionalExtensions;
global using Harbor.Abstractions.Agents;
global using Harbor.Abstractions.Events;
global using Harbor.Abstractions.Models;
global using Harbor.Abstractions.Models.Identifiers;
global using Harbor.Abstractions.Permissions;
global using Harbor.Abstractions.Providers;
global using Harbor.Abstractions.Sessions;
global using Harbor.Abstractions.Tools;

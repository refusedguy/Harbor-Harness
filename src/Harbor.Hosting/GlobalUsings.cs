// Harbor.Hosting — global usings.
// Union of the namespaces the composition modules need (mirrors the using
// blocks of the former apps/Harbor.App.Cli/Hosting/* partials).

global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Threading;
global using System.Threading.Tasks;

// Microsoft.Extensions
global using Microsoft.Extensions.Configuration;
global using Microsoft.Extensions.DependencyInjection;
global using Microsoft.Extensions.Logging;

// Harbor.Abstractions surface (concrete registries keep these namespaces)
global using Harbor.Abstractions.Agents;
global using Harbor.Abstractions.Events;
global using Harbor.Abstractions.Models;
global using Harbor.Abstractions.Models.Identifiers;
global using Harbor.Abstractions.Permissions;
global using Harbor.Abstractions.Providers;
global using Harbor.Abstractions.Sessions;
global using Harbor.Abstractions.Tools;
global using Harbor.Abstractions.Tui;

// Core pipeline + config
global using Harbor.Application.Agents;
global using Harbor.Application.Configuration;
global using Harbor.Core.Events;
global using Harbor.Application.Onboarding;
global using Harbor.Application.Permissions;
global using Harbor.Application.Resilience;
global using Harbor.Application.Sessions;
global using Harbor.Core.Tools;

// Desktop config types (CommonConfig / CompositeConfig / stores)
global using Harbor.Desktop.Abstractions.Configuration;

// Storage / providers / ipc / tools / tui defaults
global using Harbor.Storage.Jsonl;
global using Harbor.Storage.Memory;
global using Harbor.Providers.Ollama;
global using Harbor.Ipc.Client;
global using Harbor.Ipc.InProcess;
global using Harbor.Ipc.Server;
global using Harbor.Tools.Builtin;
global using Harbor.Tools.Mcp;
global using Harbor.Tui.Plain;
global using Harbor.Terminal.Abstractions;
global using Harbor.Ui.Framework.Panels;

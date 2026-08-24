// Harbor.Abstractions.Contracts — root namespace file.
// This project is the pure contract layer (Session, Messages, Identifiers,
// AgentEvent, PermissionRuleset) — the types referenced by Harbor.Abstractions
// interface signatures. Formerly Harbor.Domain (deleted in the F1 decoupling).
// Namespaces are preserved as
// Harbor.Abstractions.{Models,Events,Permissions,Models.Identifiers} so that
// downstream consumers require zero `using` changes — they keep referencing
// `Harbor.Abstractions.Models` and pick up the types transitively via the
// Harbor.Abstractions facade project.

global using System;
global using System.Collections.Generic;
global using System.Text.Json;
global using System.Threading;
global using System.Threading.Tasks;
global using CSharpFunctionalExtensions;

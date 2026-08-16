// Harbor.Domain — root namespace file.
// This project is the pure domain layer (Session, Messages, Identifiers,
// AgentEvent, PermissionRuleset). Namespaces are preserved as
// Harbor.Abstractions.{Models,Events,Permissions,Models.Identifiers} so that
// downstream consumers (Harbor.Application, Harbor.Registries, etc.) require
// zero `using` changes — they keep referencing `Harbor.Abstractions.Models`
// and pick up the types transitively via the Harbor.Abstractions facade.

global using System;
global using System.Collections.Generic;
global using System.Text.Json;
global using System.Threading;
global using System.Threading.Tasks;
global using CSharpFunctionalExtensions;
